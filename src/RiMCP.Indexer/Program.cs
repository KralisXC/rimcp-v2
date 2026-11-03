using System.Diagnostics;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Storage;
using RiMCP.Indexer.Building;
using RiMCP.Indexer.Config;

// 固定 UTF-8 输出：重定向时直接写 UTF-8 字节，避免控制台代码页差异导致 Launcher 日志乱码。
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (System.IO.IOException) { }
if (Console.IsOutputRedirected)
    Console.SetOut(new System.IO.StreamWriter(
        Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
if (Console.IsErrorRedirected)
    Console.SetError(new System.IO.StreamWriter(
        Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });

Stopwatch clock = Stopwatch.StartNew();

int ExitFail(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\nERROR: {message}");
    Console.ResetColor();
    return 1;
}

IndexerConfig cfg;
try
{
    cfg = IndexerConfig.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"参数解析失败：{ex.Message}");
    return 2;
}

Console.WriteLine($"[indexer] 目标索引目录: {cfg.Output}");
Console.WriteLine($"RimWorldData : {cfg.Root}");
Console.WriteLine($"output       : {cfg.Output}");
Console.WriteLine($"model        : {cfg.ModelFile}");
Console.WriteLine($"batch/maxLen : {cfg.BatchSize}/{cfg.MaxLength}  provider={cfg.OnnxProvider}");

IReadOnlyList<ShardSource> shards;
try
{
    shards = ShardCatalog.Discover(cfg.Root, cfg.CSharpSourceSubdir);
}
catch (Exception ex)
{
    return ExitFail($"分片发现失败：{ex.Message}");
}

string gameVersion = ShardCatalog.DetectGameVersion(cfg.Root);
string builderCommit = ResolveBuilderCommit();
int? maxEntries = ParseMaxEntries(args);
string embeddingModel = "e5-base-v2-onnx";
string precision = "fp16";

Console.WriteLine($"gameVersion  : {gameVersion}");
Console.WriteLine($"builderCommit: {builderCommit}");
Console.WriteLine($"分片         : {string.Join(", ", shards.Select(s => $"{s.Name}({s.Index})"))}");

// 一次算出全部分片的期望层哈希（graph 层使用覆盖跨分片边的全局图哈希）。
IReadOnlyList<ShardCatalog.ShardEnvironment> envs;
try
{
    envs = ShardCatalog.BuildEnvironments(
        shards, cfg.Root, gameVersion, cfg.OnnxProvider, precision,
        embeddingModel, IndexerConfig.Dim, builderCommit);
}
catch (Exception ex)
{
    return ExitFail($"环境哈希计算失败：{ex.Message}");
}

foreach (var shard in shards.OrderBy(s => s.Index))
{
    var env = envs.First(e => e.ShardName.Equals(shard.Name, StringComparison.Ordinal));
    PrintExistingComparison(cfg.Output, shard, env);
}

var pipeline = new BuildPipeline(
    cfg, shards, envs, gameVersion, builderCommit,
    embeddingModel, precision, IndexerConfig.Dim, ShardCatalog.EmbeddingPreprocessingRevision);

// --graph-only：只更新符号/图，不加载模型。
if (cfg.GraphOnly)
{
    if (maxEntries is not null)
        return ExitFail("--graph-only 不支持 --max-entries 截断样本。");
    return pipeline.RunGraphOnly();
}

// dry-run：逐分片解析并打印，不加载模型 / 不落盘。
if (cfg.DryRun)
    return RunDryRun(cfg, shards, envs, maxEntries);

return pipeline.Run(maxEntries);

/// <summary>解析 --max-entries N；缺失或非法返回 null。</summary>
static int? ParseMaxEntries(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--max-entries", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return int.TryParse(args[i + 1], out var n) ? n : null;
    }
    return null;
}

/// <summary>构建前打印已存在 manifest 的逐分片比对结果（只打印，不跳过）。</summary>
static void PrintExistingComparison(
    string outputDir, ShardSource shard, ShardCatalog.ShardEnvironment env)
{
    string manifestPath = Path.Combine(outputDir, shard.Name, "manifest.json");
    ShardManifest? manifest = null;
    if (File.Exists(manifestPath))
    {
        try { manifest = ShardManifest.Load(manifestPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[manifest] {shard.Name}: 读取失败（{ex.Message}）");
            return;
        }
    }
    var comparison = ManifestComparer.Compare(manifest, env);
    Console.WriteLine($"[manifest] {ManifestComparer.Describe(comparison)}");
}

/// <summary>dry-run：逐分片解析、统计并打印 manifest 比对，不加载模型、不落盘。</summary>
int RunDryRun(
    IndexerConfig cfg, IReadOnlyList<ShardSource> shards,
    IReadOnlyList<ShardCatalog.ShardEnvironment> envs, int? maxEntries)
{
    int remaining = maxEntries ?? int.MaxValue;
    int total = 0;

    foreach (var shard in shards.OrderBy(s => s.Index))
    {
        clock.Restart();
        int? shardLimit = maxEntries is null ? null : Math.Max(0, remaining);
        var entries = ShardBuilder.CollectEntries(cfg, shard, shardLimit);
        total += entries.Count;
        remaining -= entries.Count;

        Console.WriteLine(
            $"[{shard.Name}] 解析 {entries.Count} 条（{clock.Elapsed.TotalSeconds:F1}s）");
        PrintKindBreakdown(entries);
    }

    Console.WriteLine($"  [dry-run] 合计 {total} 条，未写入任何文件");
    return 0;
}

/// <summary>按 SymbolKind 拆分数量打印。</summary>
static void PrintKindBreakdown(List<SymbolEntry> entries)
{
    int c = 0, s = 0, i = 0, en = 0, m = 0, p = 0, f = 0, ev = 0, env = 0, d = 0, df = 0;
    foreach (var e in entries)
    {
        switch (e.Kind)
        {
            case SymbolKind.CsClass: c++; break;
            case SymbolKind.CsStruct: s++; break;
            case SymbolKind.CsInterface: i++; break;
            case SymbolKind.CsEnum: en++; break;
            case SymbolKind.CsMethod: m++; break;
            case SymbolKind.CsProperty: p++; break;
            case SymbolKind.CsField: f++; break;
            case SymbolKind.CsEvent: ev++; break;
            case SymbolKind.CsEnumValue: env++; break;
            case SymbolKind.XmlDef: d++; break;
            case SymbolKind.XmlDefField: df++; break;
        }
    }
    Console.WriteLine($"     type: Class={c} Struct={s} Interface={i} Enum={en}");
    Console.WriteLine($"     member: Method={m} Property={p} Field={f} Event={ev} EnumValue={env}");
    Console.WriteLine($"     xml: Def={d} DefField={df}");
}

/// <summary>构建者提交号：优先环境变量，否则由外部脚本注入；默认 unknown。</summary>
static string ResolveBuilderCommit()
{
    var env = Environment.GetEnvironmentVariable("RIMCP_BUILDER_COMMIT");
    return string.IsNullOrWhiteSpace(env) ? "unknown" : env.Trim();
}
