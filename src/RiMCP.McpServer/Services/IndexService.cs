using Lucene.Net.Index;
using Microsoft.ML.OnnxRuntime;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Paths;
using RiMCP.Core.Storage;
using RiMCP.Core.Support;

namespace RiMCP.McpServer.Services;

/// <summary>
/// 检索服务：启动时加载 build.json 指向的所有分片 manifest、符号表、向量、Lucene 与图资源。
/// 全局符号 id 由各分片 manifest.BaseId + 本地 id 构成，保证 0..N-1 连续。
/// </summary>
public sealed class IndexService : IDisposable
{
    /// <summary>e5-base-v2 固定维度，用于与 build.json 交叉校验。</summary>
    public const int DefaultDim = 768;

    /// <summary>嵌入最大 token 长度（与 Indexer 对齐）。</summary>
    public const int MaxLength = 256;

    private readonly IReadOnlyList<ShardRuntime> _shards;

    /// <summary>build.json 载入后的清单对象。</summary>
    public BuildJson.BuildInfo Build { get; }

    /// <summary>合并后的符号倒排索引（id/签名/短名 → 条目，id 为全局 id）。</summary>
    public SymbolIndex Symbols { get; }

    /// <summary>多分片向量的统一访问门面。</summary>
    public ShardedVectors Vectors { get; }

    /// <summary>词表分词器（查询侧与索引侧共用同一 tokenizer.json）。</summary>
    internal WordPieceTokenizer Tokenizer { get; }

    /// <summary>ONNX 推理会话。</summary>
    public InferenceSession Onnx { get; }

    /// <summary>RiMCP_v2 根目录。</summary>
    public string V2Root { get; }

    /// <summary>RimWorldData 绝对路径（源码相对此根）。</summary>
    public string RimWorldDataAbs { get; }

    /// <summary>向量维度（取自 build.json）。</summary>
    public int Dim => Build.Dim;

    /// <summary>ONNX 主输入名（通常 input_ids）。</summary>
    public string MainInputName { get; }

    /// <summary>是否需要额外提供 token_type_ids 输入。</summary>
    public bool NeedsTokenTypeIds { get; }

    /// <summary>已加载的分片（供 GraphService 与诊断使用）。</summary>
    internal IReadOnlyList<ShardRuntime> Shards => _shards;

    public IndexService(string v2Root, string indexDir, string modelDir)
    {
        V2Root = Path.GetFullPath(v2Root);

        // ---- build.json 载入 ----
        string buildPath = Path.Combine(indexDir, "build.json");
        if (!File.Exists(buildPath))
            throw new InvalidDataException($"build.json 不存在：{buildPath}（请先运行 Indexer 全量构建）。");
        Build = BuildJson.Load(buildPath);

        string indexAbs = Build.Paths.TryGetValue("indexDir", out var indexRel)
            ? PathUtil.Resolve(V2Root, indexRel)
            : Path.GetFullPath(indexDir);
        RimWorldDataAbs = Build.Paths.TryGetValue("rimeworldData", out var dataRel)
            ? PathUtil.Resolve(V2Root, dataRel)
            : Path.GetFullPath(Path.Combine(V2Root, "RimWorldData"));

        // ---- 逐分片加载 symbols / vectors / lucene ----
        var runtimes = new List<ShardRuntime>();
        var mergedEntries = new List<SymbolEntry>();
        int expectedBase = 0;

        foreach (var pointer in Build.Shards.OrderBy(p => p.Index))
        {
            string manifestAbs = PathUtil.Resolve(V2Root, pointer.ManifestPath);
            if (!File.Exists(manifestAbs))
                throw new InvalidDataException($"分片 manifest 不存在：{manifestAbs}");

            var manifest = ShardManifest.Load(manifestAbs);
            if (!manifest.Shard.Equals(pointer.Name, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"manifest 分片名 {manifest.Shard} 与 build.json 指针 {pointer.Name} 不符。");
            if (manifest.ShardIndex != pointer.Index)
                throw new InvalidDataException(
                    $"manifest 分片序号 {manifest.ShardIndex} 与 build.json 指针 {pointer.Index} 不符。");
            if (manifest.BaseId != expectedBase)
                throw new InvalidDataException(
                    $"分片 {manifest.Shard} BaseId={manifest.BaseId}，期望 {expectedBase}（分片不连续，请全量重建）。");

            string shardDir = Path.GetDirectoryName(manifestAbs)!;
            string symbolsPath = Path.Combine(shardDir, "symbols.tsv");
            string vectorsPath = Path.Combine(shardDir, "vectors.bin");
            string lucenePath = Path.Combine(shardDir, "lucene");
            if (!File.Exists(symbolsPath))
                throw new InvalidDataException($"分片 symbols.tsv 不存在：{symbolsPath}");
            if (!File.Exists(vectorsPath))
                throw new InvalidDataException($"分片 vectors.bin 不存在：{vectorsPath}");
            if (!System.IO.Directory.Exists(lucenePath))
                throw new InvalidDataException($"分片 Lucene 目录不存在：{lucenePath}");

            var localEntries = SymbolsTsv.Load(symbolsPath);
            if (localEntries.Count != manifest.SymbolCount)
                throw new InvalidDataException(
                    $"分片 {manifest.Shard} symbols.tsv 条数 {localEntries.Count} 与 manifest {manifest.SymbolCount} 不符。");

            int baseId = checked((int)manifest.BaseId);
            foreach (var e in localEntries)
            {
                e.Id = checked(baseId + e.Id);
                mergedEntries.Add(e);
            }

            var vectors = VectorsBin.Open(vectorsPath);
            if (vectors.RecordCount != localEntries.Count)
                throw new InvalidDataException(
                    $"分片 {manifest.Shard} vectors 条数 {vectors.RecordCount} 与符号数 {localEntries.Count} 不符。");
            if (vectors.Dim != Build.Dim)
                throw new InvalidDataException(
                    $"分片 {manifest.Shard} vectors 维度 {vectors.Dim} 与 build.json {Build.Dim} 不符。");

            var luceneDir = Lucene.Net.Store.FSDirectory.Open(lucenePath);
            runtimes.Add(new ShardRuntime(
                manifest, shardDir, baseId, localEntries.Count, vectors, luceneDir));
            expectedBase += localEntries.Count;
        }

        _shards = runtimes;
        Symbols = new SymbolIndex(mergedEntries);
        BuildJson.Validate(Build, Build.Dim, Symbols.Count);
        Vectors = new ShardedVectors(
            runtimes.Select(r => (r.BaseId, r.SymbolCount, r.Vectors)).ToArray(), Build.Dim);

        // ---- 逐分片 manifest 诊断（只打印，不跳过任何分片）----
        string provider = OnnxProviderResolver.Resolve();
        PrintManifestDiagnostics(provider);

        // ---- ONNX 模型 + tokenizer ----
        string onnxFile = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(onnxFile))
            throw new FileNotFoundException("model.onnx 不存在。", onnxFile);
        string tokenizerFile = Path.Combine(modelDir, "tokenizer.json");
        if (!File.Exists(tokenizerFile))
            throw new FileNotFoundException("tokenizer.json 不存在。", tokenizerFile);

        Tokenizer = new WordPieceTokenizer(tokenizerFile);
        Onnx = CreateInferenceSession(onnxFile, provider);
        var inputs = Onnx.InputMetadata.Keys.ToList();
        NeedsTokenTypeIds = inputs.Contains("token_type_ids", StringComparer.Ordinal);
        MainInputName = inputs.First(n =>
            n.Equals("input_ids", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("input_ids", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>逐分片比对 manifest 与当前环境，输出到 stderr。</summary>
    private void PrintManifestDiagnostics(string provider)
    {
        try
        {
            var sources = ShardCatalog.Discover(RimWorldDataAbs, ShardCatalog.DefaultCSharpSubdir)
                .ToDictionary(s => s.Name, StringComparer.Ordinal);
            string gameVersion = string.IsNullOrWhiteSpace(Build.GameVersion)
                ? ShardCatalog.DetectGameVersion(RimWorldDataAbs)
                : Build.GameVersion;
            string model = Build.Model?.Name is { Length: > 0 } name ? name : "e5-base-v2-onnx";
            string precision = "fp16";
            string builderCommit = string.IsNullOrWhiteSpace(Build.BuilderCommit)
                ? "unknown" : Build.BuilderCommit;

            foreach (var runtime in _shards)
            {
                var source = sources.TryGetValue(runtime.Manifest.Shard, out var s) ? s : null;
                if (source is null)
                {
                    Console.Error.WriteLine(
                        $"[manifest] {runtime.Manifest.Shard}: 缺失（当前 RimWorldData 中找不到该分片来源）");
                    continue;
                }

                var env = ShardCatalog.BuildEnvironment(
                    source, RimWorldDataAbs, gameVersion, provider, precision,
                    model, Build.Dim, builderCommit);
                var comparison = ManifestComparer.Compare(runtime.Manifest, env);
                Console.Error.WriteLine($"[manifest] {ManifestComparer.Describe(comparison)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[manifest] 诊断跳过：{ex.Message}");
        }
    }

    /// <summary>创建 ONNX 推理会话。</summary>
    private static InferenceSession CreateInferenceSession(string onnxFile, string provider)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        ApplyProvider(opts, provider);
        Console.Error.WriteLine($"[ONNX] provider = {provider}");
        return new InferenceSession(onnxFile, opts);
    }

    /// <summary>按后端挂载 ONNX 执行提供方。DirectML 仅在该后端编译时（DIRECTML 符号）可用。</summary>
    private static void ApplyProvider(SessionOptions options, string provider)
    {
        try
        {
            switch (provider)
            {
                case "cuda":
                    options.AppendExecutionProvider_CUDA(0);
                    break;
#if DIRECTML
                case "directml":
                    options.AppendExecutionProvider_DML(0);
                    break;
#endif
                case "cpu":
                default:
                    // 默认 CPU，无需附加执行提供方
                    break;
            }
        }
        catch (Exception ex)
        {
            // 后端不可用：打印后继续（CPU 作为兜底，由 ORT 自动选择）
            Console.Error.WriteLine($"[ONNX] 无法启用 {provider}，将回退 CPU：{ex.Message}");
        }
    }

    /// <summary>打开一个跨分片的只读 Lucene reader；调用方负责 Dispose。</summary>
    public IndexReader NewReader()
    {
        if (_shards.Count == 0)
            throw new InvalidOperationException("没有已加载的分片。");

        var readers = _shards
            .Select(s => (IndexReader)DirectoryReader.Open(s.LuceneDirectory))
            .ToArray();
        return readers.Length == 1 ? readers[0] : new MultiReader(readers, closeSubReaders: true);
    }

    /// <summary>按全局 id 取符号条目；不存在返回 null。</summary>
    public SymbolEntry? EntryOf(int id) => Symbols.GetById(id);

    /// <summary>
    /// 解析符号引用。支持多种形式（按优先级）：
    /// 数字 id（<c>#123</c>）→ 签名精确 → 短名精确。还会自动剥离 <c>xml:</c> 前缀后重试。
    /// 均失败返回 <c>null</c>。
    /// </summary>
    public SymbolEntry? ResolveSymbol(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string refStr = reference.Trim();

        if (refStr.StartsWith('#') && int.TryParse(refStr.AsSpan(1), out var id) && Symbols.GetById(id) is { } byId)
            return byId;

        SymbolEntry? found = MatchExactly(refStr);
        if (found is not null)
            return found;

        // 兼容 "xml:ThingDef:Door" 这种带前缀的引用，去掉前缀后用签名/短名再试一次
        if (refStr.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
        {
            string stripped = refStr[4..].TrimStart();
            if (stripped.Length > 0 && (found = MatchExactly(stripped)) is not null)
                return found;
        }

        return null;
    }

    private SymbolEntry? MatchExactly(string refStr)
    {
        var bySig = Symbols.GetBySignature(refStr);
        if (bySig is { Count: > 0 }) return Symbols.GetById(bySig[0]);

        var byName = Symbols.GetByShortName(refStr);
        if (byName is { Count: > 0 }) return Symbols.GetById(byName[0]);

        return null;
    }

    /// <summary>把符号的相对路径 + 行号解析为 RimWorldData 内的绝对源码路径。</summary>
    public string SourcePath(SymbolEntry e)
    {
        if (string.IsNullOrEmpty(e.FileRelPath))
            return string.Empty;
        return Path.GetFullPath(Path.Combine(RimWorldDataAbs, e.FileRelPath));
    }

    public void Dispose()
    {
        Onnx?.Dispose();
        Vectors?.Close();
        foreach (var shard in _shards)
            shard.LuceneDirectory.Dispose();
    }
}

/// <summary>一个已加载分片的运行时资源。</summary>
internal sealed class ShardRuntime(
    ShardManifest manifest,
    string shardDir,
    int baseId,
    int symbolCount,
    VectorsBin vectors,
    Lucene.Net.Store.FSDirectory luceneDirectory)
{
    public ShardManifest Manifest { get; } = manifest;
    public string ShardDir { get; } = shardDir;
    public int BaseId { get; } = baseId;
    public int SymbolCount { get; } = symbolCount;
    public VectorsBin Vectors { get; } = vectors;
    public Lucene.Net.Store.FSDirectory LuceneDirectory { get; } = luceneDirectory;
}
