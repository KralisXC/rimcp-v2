using Lucene.Net.Index;
using Microsoft.ML.OnnxRuntime;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Paths;
using RiMCP.Core.Storage;
using RiMCP.Core.Support;

namespace RiMCP.McpServer.Services;

/// <summary>
/// 检索服务：启动时加载 build.json、按启用分片加载 symbols/vectors/Lucene，
/// 并维护全局符号 id 空间（未启用分片 id 段保留为空洞，保证 id 不漂移）。
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

    /// <summary>多分片向量的统一访问门面（仅含启用分片）。</summary>
    public ShardedVectors Vectors { get; }

    /// <summary>规范化后的启用分片名（顺序 = build.json 分片顺序）。</summary>
    public IReadOnlyList<string> EnabledShards { get; }

    /// <summary>已启用分片的全局 id 区间（BaseId, SymbolCount），供 GraphService 过滤边。</summary>
    public IReadOnlyList<(int BaseId, int SymbolCount)> EnabledRanges { get; }

    /// <summary>已加载分片构建的全局符号表；同名 XML Def 覆盖规则与 Indexer 共用。</summary>
    internal GlobalSymbolTable SymbolTable { get; }

    /// <summary>词表分词器（查询侧与索引侧共用同一 tokenizer.json）。</summary>
    internal WordPieceTokenizer Tokenizer { get; }

    /// <summary>ONNX 推理会话。</summary>
    public InferenceSession Onnx { get; }

    /// <summary>RiMCP_v2 根目录。</summary>
    public string V2Root { get; }

    /// <summary>当前加载的索引目录（绝对路径，由 --index-dir / RIMCP_INDEX_DIR 决定）。</summary>
    public string IndexDir { get; }

    /// <summary>RimWorldData 绝对路径（源码相对此根）。</summary>
    public string RimWorldDataAbs { get; }

    /// <summary>向量维度（取自 build.json）。</summary>
    public int Dim => Build.Dim;

    /// <summary>ONNX 主输入名（通常 input_ids）。</summary>
    public string MainInputName { get; }

    /// <summary>是否需要额外提供 token_type_ids 输入。</summary>
    public bool NeedsTokenTypeIds { get; }

    /// <summary>已加载（启用）的分片（供 GraphService 与诊断使用）。</summary>
    internal IReadOnlyList<ShardRuntime> Shards => _shards;

    /// <summary>加载模式：mmap（默认）或 full（向量与 Lucene 常驻堆内存）。</summary>
    public string LoadMode { get; }

    private readonly bool _fullLoad;

    public IndexService(string v2Root, string indexDir, string modelDir, string loadMode = LoadModes.Mmap)
    {
        V2Root = Path.GetFullPath(v2Root);
        IndexDir = Path.GetFullPath(indexDir);
        LoadMode = LoadModes.Normalize(loadMode);
        _fullLoad = LoadMode == LoadModes.Full;

        // ---- build.json 载入 ----
        string buildPath = Path.Combine(indexDir, "build.json");
        if (!File.Exists(buildPath))
            throw new InvalidDataException($"build.json 不存在：{buildPath}（请先运行 Indexer 全量构建）。");
        Build = BuildJson.Load(buildPath);

        RimWorldDataAbs = Build.Paths.TryGetValue("rimeworldData", out var dataRel)
            ? PathUtil.Resolve(V2Root, dataRel)
            : Path.GetFullPath(Path.Combine(V2Root, "RimWorldData"));

        // ---- 启用分片解析 ----
        var enabled = ResolveEnabledShards(Build);
        EnabledShards = enabled;
        var enabledSet = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);

        int totalCount = checked((int)(Build.Counts?.Symbols ?? 0));

        // ---- 只对启用分片校验 manifest 文件存在性；未启用只校验指针 ----
        BuildJson.Validate(Build, Build.Dim, totalCount, enabled, V2Root);

        if (Build.Graph is not { } globalGraph || globalGraph.Hash.Length == 0)
            throw new InvalidDataException(
                "build.json 缺少全局图指针（索引处于半成品/不一致状态），请重新执行 Indexer（增量或全量）。");

        // ---- 逐分片加载：启用分片建运行时，未启用保留 id 空洞 ----
        var runtimes = new List<ShardRuntime>();
        var merged = new SymbolEntry?[totalCount];
        var sets = new List<ShardSymbolSet>();
        long expectedBase = 0;

        foreach (var pointer in Build.Shards.OrderBy(p => p.Index))
        {
            if (pointer.BaseId != expectedBase)
                throw new InvalidDataException(
                    $"分片 {pointer.Name} BaseId={pointer.BaseId}，期望 {expectedBase}（分片不连续，请全量重建）。");

            if (enabledSet.Contains(pointer.Name))
            {
                string manifestAbs = PathUtil.Resolve(V2Root, pointer.ManifestPath);
                var manifest = ShardManifest.Load(manifestAbs);
                if (!manifest.Shard.Equals(pointer.Name, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"manifest 分片名 {manifest.Shard} 与 build.json 指针 {pointer.Name} 不符。");
                if (manifest.ShardIndex != pointer.Index)
                    throw new InvalidDataException(
                        $"manifest 分片序号 {manifest.ShardIndex} 与 build.json 指针 {pointer.Index} 不符。");
                if (manifest.BaseId != expectedBase)
                    throw new InvalidDataException(
                        $"分片 {manifest.Shard} manifest.BaseId={manifest.BaseId}，期望 {expectedBase}（分片不连续，请全量重建）。");
                if (manifest.SymbolCount != pointer.SymbolCount)
                    throw new InvalidDataException(
                        $"分片 {manifest.Shard} manifest.SymbolCount={manifest.SymbolCount} 与 build.json {pointer.SymbolCount} 不符（索引不一致，请重新执行 Indexer）。");
                string graphLayerHash = manifest.Layers.TryGetValue(ShardCatalog.LayerHashes.Graph, out var graphLayer)
                    ? graphLayer.Hash : string.Empty;
                if (!graphLayerHash.Equals(globalGraph.Hash, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"分片 {manifest.Shard} 的 graph 层哈希与 build.json 全局图哈希不一致（索引处于半成品/不一致状态），请重新执行 Indexer（增量或全量）。");

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

                int baseId = checked((int)expectedBase);
                for (int local = 0; local < localEntries.Count; local++)
                {
                    var e = localEntries[local];
                    e.Id = checked(baseId + local);
                    merged[e.Id] = e;
                }

                var vectors = _fullLoad ? VectorsBin.OpenInMemory(vectorsPath) : VectorsBin.Open(vectorsPath);
                if (vectors.RecordCount != localEntries.Count)
                    throw new InvalidDataException(
                        $"分片 {manifest.Shard} vectors 条数 {vectors.RecordCount} 与符号数 {localEntries.Count} 不符。");
                if (vectors.Dim != Build.Dim)
                    throw new InvalidDataException(
                        $"分片 {manifest.Shard} vectors 维度 {vectors.Dim} 与 build.json {Build.Dim} 不符。");

                Lucene.Net.Store.Directory luceneDir;
                if (_fullLoad)
                {
                    // 整份段文件复制到 RAMDirectory，查询时不再触达磁盘。
                    var fsDir = Lucene.Net.Store.FSDirectory.Open(lucenePath);
                    luceneDir = new Lucene.Net.Store.RAMDirectory(fsDir, Lucene.Net.Store.IOContext.DEFAULT);
                    fsDir.Dispose();
                }
                else
                {
                    luceneDir = Lucene.Net.Store.FSDirectory.Open(lucenePath);
                }
                runtimes.Add(new ShardRuntime(
                    manifest, shardDir, baseId, localEntries.Count, vectors, luceneDir, localEntries));
                sets.Add(new ShardSymbolSet(pointer.Name, pointer.Index, baseId, localEntries));
            }

            expectedBase += pointer.SymbolCount;
        }

        if (expectedBase != totalCount)
            throw new InvalidDataException(
                $"分片指针合计 {expectedBase} 与 build.json 符号总数 {totalCount} 不符（请全量重建）。");

        _shards = runtimes;
        Symbols = new SymbolIndex(merged, totalCount);
        SymbolTable = GlobalSymbolTable.Build(sets);
        EnabledRanges = runtimes.Select(r => (r.BaseId, r.SymbolCount)).ToArray();
        Vectors = new ShardedVectors(
            runtimes.Select(r => (r.BaseId, r.SymbolCount, r.Vectors)).ToArray(), Build.Dim);

        // ---- 诊断：默认轻量；RIMCP_VERIFY_ENV=1 才做重量级环境比对 ----
        string provider = OnnxProviderResolver.Resolve();
        PrintDiagnostics(provider);

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

    /// <summary>
    /// 启用分片解析顺序：RIMCP_ENABLED_SHARDS 环境变量 → build.json.enabledShards → 报错。
    /// 结果按 build.json 分片顺序规范化；每个名字必须存在于 build.json.shards。
    /// </summary>
    private static List<string> ResolveEnabledShards(BuildJson.BuildInfo build)
    {
        var available = build.Shards
            .OrderBy(p => p.Index)
            .Select(p => p.Name)
            .Where(n => n.Length > 0)
            .ToList();
        if (available.Count == 0)
            throw new InvalidDataException("build.json 缺少分片指针（shards），旧索引不兼容，请执行全量重建。");

        var availableSet = available.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rawEnv = Environment.GetEnvironmentVariable("RIMCP_ENABLED_SHARDS");
        IReadOnlyList<string> requested;
        if (!string.IsNullOrWhiteSpace(rawEnv))
        {
            requested = rawEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        else if (build.EnabledShards is { Count: > 0 })
        {
            requested = build.EnabledShards
                .Select(n => n.Trim())
                .Where(n => n.Length > 0)
                .ToList();
        }
        else
        {
            throw new InvalidDataException(
                "未配置启用分片：请设置 RIMCP_ENABLED_SHARDS，或重新运行 Indexer 以写入 build.json.enabledShards。");
        }

        if (requested.Count == 0)
            throw new InvalidDataException("RIMCP_ENABLED_SHARDS 为空，请显式配置至少一个分片名。");

        foreach (var name in requested)
        {
            if (!availableSet.Contains(name))
                throw new InvalidDataException(
                    $"启用的分片 '{name}' 不在 build.json.shards 中。可用分片：{string.Join(", ", available)}。");
        }

        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return available.Where(requestedSet.Contains).ToList();
    }

    /// <summary>
    /// 默认只做轻量校验（build.json + manifest，已在加载阶段完成）；
    /// 仅当 RIMCP_VERIFY_ENV=1 且 RimWorldData 可发现时，才重算环境哈希并逐层比对。
    /// 数据缺失只打印跳过，绝不导致启动失败。
    /// </summary>
    private void PrintDiagnostics(string provider)
    {
        Console.Error.WriteLine(
            $"[index] 启用分片：{string.Join(", ", EnabledShards)}（共 {Build.Shards.Count} 个）；" +
            $"formatVersion={Build.FormatVersion}；graphHash={Truncate(Build.Graph?.Hash)}；loadMode={LoadMode}");

        if (!"1".Equals(Environment.GetEnvironmentVariable("RIMCP_VERIFY_ENV"), StringComparison.Ordinal))
            return;

        try
        {
            if (!System.IO.Directory.Exists(RimWorldDataAbs))
            {
                Console.Error.WriteLine($"[manifest] 环境校验跳过：RimWorldData 不存在（{RimWorldDataAbs}）");
                return;
            }

            var sources = ShardCatalog.Discover(RimWorldDataAbs, ShardCatalog.DefaultCSharpSubdir)
                .ToDictionary(s => s.Name, StringComparer.Ordinal);
            string gameVersion = string.IsNullOrWhiteSpace(Build.GameVersion)
                ? ShardCatalog.DetectGameVersion(RimWorldDataAbs)
                : Build.GameVersion;
            string model = Build.Model?.Name is { Length: > 0 } name ? name : "e5-base-v2-onnx";
            string precision = _shards.Count > 0 ? _shards[0].Manifest.Precision : "fp16";
            string builderCommit = string.IsNullOrWhiteSpace(Build.BuilderCommit)
                ? "unknown" : Build.BuilderCommit;

            var envs = ShardCatalog.BuildEnvironments(
                sources.Values.ToList(), RimWorldDataAbs, gameVersion, provider, precision,
                model, Build.Dim, builderCommit);

            foreach (var runtime in _shards)
            {
                var env = envs.FirstOrDefault(e => e.ShardName.Equals(runtime.Manifest.Shard, StringComparison.Ordinal));
                if (env is null)
                {
                    Console.Error.WriteLine(
                        $"[manifest] {runtime.Manifest.Shard}: 缺失（当前 RimWorldData 中找不到该分片来源）");
                    continue;
                }
                var comparison = ManifestComparer.Compare(runtime.Manifest, env);
                Console.Error.WriteLine($"[manifest] {ManifestComparer.Describe(comparison)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[manifest] 环境校验跳过：{ex.Message}");
        }
    }

    private static string Truncate(string? hash)
        => hash is { Length: > 12 } ? hash[..12] + "…" : (hash ?? string.Empty);

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

    /// <summary>打开一个跨启用分片的只读 Lucene reader；调用方负责 Dispose。</summary>
    public IndexReader NewReader()
    {
        if (_shards.Count == 0)
            throw new InvalidOperationException("没有已加载的分片。");

        var readers = _shards
            .Select(s => (IndexReader)DirectoryReader.Open(s.LuceneDirectory))
            .ToArray();
        return readers.Length == 1 ? readers[0] : new MultiReader(readers, closeSubReaders: true);
    }

    /// <summary>按全局 id 取符号条目；未启用 id 段（空洞）返回 null。</summary>
    public SymbolEntry? EntryOf(int id) => Symbols.GetById(id);

    /// <summary>
    /// 解析符号引用。支持数字 id（<c>#123</c>）、完整签名、短名，并自动剥离 <c>xml:</c> 前缀。
    /// 签名/短名解析优先走 <see cref="GlobalSymbolTable"/>，应用同名 XML Def 覆盖规则；均失败返回 null。
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
        int globalId = SymbolTable.ResolveSignature(refStr);
        if (globalId >= 0 && Symbols.GetById(globalId) is { } bySignature)
            return bySignature;

        globalId = SymbolTable.ResolveShortName(refStr);
        if (globalId >= 0 && Symbols.GetById(globalId) is { } byShortName)
            return byShortName;

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

/// <summary>一个已加载（启用）分片的运行时资源。</summary>
internal sealed class ShardRuntime(
    ShardManifest manifest,
    string shardDir,
    int baseId,
    int symbolCount,
    VectorsBin vectors,
    Lucene.Net.Store.Directory luceneDirectory,
    IReadOnlyList<SymbolEntry> localEntries)
{
    public ShardManifest Manifest { get; } = manifest;
    public string ShardDir { get; } = shardDir;
    public int BaseId { get; } = baseId;
    public int SymbolCount { get; } = symbolCount;
    public VectorsBin Vectors { get; } = vectors;
    public Lucene.Net.Store.Directory LuceneDirectory { get; } = luceneDirectory;

    /// <summary>本地符号列表（下标即 localSeq，Id 已改写为全局 id）。</summary>
    public IReadOnlyList<SymbolEntry> LocalEntries { get; } = localEntries;
}
