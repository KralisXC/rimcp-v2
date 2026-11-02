using System.Diagnostics;
using System.Security.Cryptography;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Storage;
using RiMCP.Indexer.Building;
using RiMCP.Indexer.Config;

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

// --graph-only：逐分片从已有 symbols.tsv 构图，不加载模型
if (cfg.GraphOnly)
    return RunGraphOnly(cfg, shards, gameVersion, builderCommit);

// dry-run：逐分片解析并打印，不加载模型 / 不落盘
if (cfg.DryRun)
    return RunDryRun(cfg, shards, gameVersion, builderCommit, maxEntries, embeddingModel, precision);

if (shards.Count == 0)
    return ExitFail("未发现任何分片，终止。");

Directory.CreateDirectory(cfg.Output);

long baseId = 0;
int remaining = maxEntries ?? int.MaxValue;
var results = new List<ShardBuildResult>();

foreach (var shard in shards)
{
    var env = ShardCatalog.BuildEnvironment(
        shard, cfg.Root, gameVersion, cfg.OnnxProvider, precision,
        embeddingModel, IndexerConfig.Dim, builderCommit);
    PrintExistingComparison(cfg.Output, shard, env);

    int? shardLimit = maxEntries is null ? null : Math.Max(0, remaining);
    clock.Restart();
    ShardBuildResult result;
    try
    {
        result = ShardBuilder.BuildFull(cfg, shard, baseId, env, shardLimit);
    }
    catch (Exception ex)
    {
        return ExitFail($"[{shard.Name}] 构建失败：{ex.Message}");
    }

    results.Add(result);
    baseId += result.SymbolCount;
    remaining -= (int)result.SymbolCount;
    Console.WriteLine($"[{shard.Name}] 完成：{result.SymbolCount} 符号，{result.EdgeCount} 边（{clock.Elapsed.TotalSeconds:F1}s）");
}

string buildPath = Path.Combine(cfg.Output, "build.json");
WriteBuildJson(cfg, results, gameVersion, builderCommit, buildPath);

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine(
    $"\nDONE 索引构建完成：{results.Sum(r => r.SymbolCount)} 符号，{results.Count} 个分片，{new DirectoryInfo(cfg.Output).FullName}");
Console.ResetColor();
return 0;

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

/// <summary>--graph-only：逐分片构图并更新 manifest / build.json 的边数。</summary>
int RunGraphOnly(
    IndexerConfig cfg, IReadOnlyList<ShardSource> shards,
    string gameVersion, string builderCommit)
{
    var results = new List<ShardBuildResult>();
    int failures = 0;

    foreach (var shard in shards)
    {
        string shardDir = Path.Combine(cfg.Output, shard.Name);
        if (!File.Exists(Path.Combine(shardDir, "manifest.json")))
        {
            Console.Error.WriteLine(
                $"[{shard.Name}] 跳过：manifest 不存在（旧索引请先全量重建）");
            failures++;
            continue;
        }

        clock.Restart();
        try
        {
            long edges = ShardBuilder.BuildGraphOnly(cfg, shard);
            var manifest = ShardManifest.Load(Path.Combine(shardDir, "manifest.json"));
            results.Add(new ShardBuildResult(
                shard, manifest.BaseId, manifest.SymbolCount, edges, shardDir, manifest));
            Console.WriteLine($"[{shard.Name}] 图完成：{edges} 条边（{clock.Elapsed.TotalSeconds:F1}s）");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{shard.Name}] 构图失败：{ex.Message}");
            failures++;
        }
    }

    if (results.Count > 0)
    {
        string buildPath = Path.Combine(cfg.Output, "build.json");
        if (File.Exists(buildPath))
        {
            var info = BuildJson.Load(buildPath);
            var byName = results.ToDictionary(r => r.Source.Name, StringComparer.Ordinal);
            foreach (var pointer in info.Shards)
            {
                if (!byName.TryGetValue(pointer.Name, out var r)) continue;
                pointer.EdgeCount = r.EdgeCount;
                pointer.SymbolCount = r.SymbolCount;
                pointer.ManifestPath = Rel(cfg.V2Root, Path.Combine(r.ShardDir, "manifest.json"));
                pointer.ManifestHash = ShardManifest.ComputeFileHash(
                    Path.Combine(r.ShardDir, "manifest.json"));
            }
            info.Counts ??= new BuildJson.CountsInfo();
            info.Counts.Edges = info.Shards.Sum(p => p.EdgeCount);
            info.Counts.Symbols = info.Shards.Sum(p => p.SymbolCount);
            info.Counts.Nodes = info.Counts.Symbols;
            info.GameVersion = gameVersion;
            info.BuilderCommit = builderCommit;
            BuildJson.Save(buildPath, info);
            Console.WriteLine("[P4] build.json 分片指针与边数已更新");
        }
        else
        {
            Console.Error.WriteLine("build.json 不存在，仅完成分片 manifest 更新。");
        }
    }

    return failures == 0 ? 0 : 1;
}

/// <summary>dry-run：逐分片解析、统计并打印 manifest 比对，不加载模型、不落盘。</summary>
int RunDryRun(
    IndexerConfig cfg, IReadOnlyList<ShardSource> shards,
    string gameVersion, string builderCommit, int? maxEntries,
    string embeddingModel, string precision)
{
    int remaining = maxEntries ?? int.MaxValue;
    int total = 0;

    foreach (var shard in shards)
    {
        var env = ShardCatalog.BuildEnvironment(
            shard, cfg.Root, gameVersion, cfg.OnnxProvider, precision,
            embeddingModel, IndexerConfig.Dim, builderCommit);
        PrintExistingComparison(cfg.Output, shard, env);

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

/// <summary>写顶层 build.json：只汇总分片 manifest 指针与总计。</summary>
static void WriteBuildJson(
    IndexerConfig cfg, IReadOnlyList<ShardBuildResult> results,
    string gameVersion, string builderCommit, string buildPath)
{
    string modelHash = ComputeModelHash(cfg.ModelFile);

    var pointers = results
        .OrderBy(r => r.Source.Index)
        .Select(r => new BuildJson.ShardPointer
        {
            Name = r.Source.Name,
            Index = r.Source.Index,
            BaseId = r.BaseId,
            SymbolCount = r.SymbolCount,
            EdgeCount = r.EdgeCount,
            ManifestPath = Rel(cfg.V2Root, Path.Combine(r.ShardDir, "manifest.json")),
            ManifestHash = ShardManifest.ComputeFileHash(
                Path.Combine(r.ShardDir, "manifest.json")),
        })
        .ToList();

    var info = new BuildJson.BuildInfo
    {
        Schema = BuildJson.Schema,
        FormatVersion = BuildJson.CurrentFormatVersion,
        Generator = new BuildJson.GeneratorInfo { Name = "RiMCP.Indexer", Version = "0.1.0" },
        Dim = IndexerConfig.Dim,
        Model = new BuildJson.ModelInfo
        {
            Name = "e5-base-v2-onnx",
            File = Rel(cfg.V2Root, cfg.ModelFile),
            Hash = modelHash,
        },
        Counts = new BuildJson.CountsInfo
        {
            Symbols = pointers.Sum(p => p.SymbolCount),
            Nodes = pointers.Sum(p => p.SymbolCount),
            Edges = pointers.Sum(p => p.EdgeCount),
        },
        GameVersion = gameVersion,
        BuilderCommit = builderCommit,
        Shards = pointers,
        Paths = new Dictionary<string, string>
        {
            ["rimeworldData"] = Rel(cfg.V2Root, cfg.Root),
            ["indexDir"] = Rel(cfg.V2Root, cfg.Output),
        },
        Granularity = "symbol-with-line",
        Lucene = new BuildJson.LuceneInfo { Ok = true, Version = "4.8.0-beta00016" },
    };

    BuildJson.Save(buildPath, info);
}

static string Rel(string rootAbs, string abs)
    => Path.GetRelativePath(rootAbs, abs).Replace('\\', '/');

/// <summary>构建者提交号：优先环境变量，否则由外部脚本注入；默认 unknown。</summary>
static string ResolveBuilderCommit()
{
    var env = Environment.GetEnvironmentVariable("RIMCP_BUILDER_COMMIT");
    return string.IsNullOrWhiteSpace(env) ? "unknown" : env.Trim();
}

static string ComputeModelHash(string modelFile)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(modelFile);
    byte[] digest = sha.ComputeHash(fs);
    return Convert.ToHexString(digest).Substring(0, 16).ToLowerInvariant();
}
