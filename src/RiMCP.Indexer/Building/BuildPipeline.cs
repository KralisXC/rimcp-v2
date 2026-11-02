using System.Globalization;
using System.Security.Cryptography;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Storage;
using RiMCP.Indexer.Config;
using RiMCP.Indexer.Embedding;
using RiMCP.Indexer.Graph;
using RiMCP.Indexer.Lucene;
using static RiMCP.Core.Indexing.ShardCatalog;

namespace RiMCP.Indexer.Building;

/// <summary>
/// 阶段二构建流水线：分层复用 + 符号级增量嵌入 + 全局统一构图。
/// 触发顺序固定：解析 → 符号提取 → 全局合并 → 嵌入 → Lucene → 图。
/// </summary>
internal sealed class BuildPipeline
{
    private readonly IndexerConfig _cfg;
    private readonly IReadOnlyList<ShardSource> _shards;
    private readonly IReadOnlyList<ShardCatalog.ShardEnvironment> _envs;
    private readonly string _gameVersion;
    private readonly string _builderCommit;
    private readonly string _embeddingModel;
    private readonly string _precision;
    private readonly int _dim;
    private readonly int _prepRevision;
    private readonly string _indexDir;
    private readonly string _cachePath;
    private readonly string _buildPath;
    private readonly string _graphDir;

    public BuildPipeline(
        IndexerConfig cfg,
        IReadOnlyList<ShardSource> shards,
        IReadOnlyList<ShardCatalog.ShardEnvironment> envs,
        string gameVersion,
        string builderCommit,
        string embeddingModel,
        string precision,
        int dim,
        int prepRevision)
    {
        _cfg = cfg;
        _shards = shards;
        _envs = envs;
        _gameVersion = gameVersion;
        _builderCommit = builderCommit;
        _embeddingModel = embeddingModel;
        _precision = precision;
        _dim = dim;
        _prepRevision = prepRevision;
        _indexDir = cfg.Output;
        _cachePath = Path.Combine(_indexDir, EmbeddingCache.FileName);
        _buildPath = Path.Combine(_indexDir, "build.json");
        _graphDir = Path.Combine(_indexDir, "graph");
    }

    private sealed class ShardState
    {
        public required ShardSource Source { get; init; }
        public required ShardCatalog.ShardEnvironment Env { get; init; }
        public required string Dir { get; init; }
        public required List<SymbolEntry> Entries { get; set; }
        public required bool Parsed { get; set; }
        public required bool Truncated { get; set; }
        public required int BaseId { get; set; }
        public required bool ReuseVectors { get; set; }
        public required bool ReuseLucene { get; set; }
        public ShardManifest? OldManifest { get; init; }
        public string[] Hashes { get; set; } = Array.Empty<string>();
        public VectorsBin? Vectors { get; set; }
        public BuildCheckpoint? Checkpoint { get; set; }
        public bool Resumed { get; set; }
        public int NextBatchNo { get; set; } = 1;

        public string Name => Source.Name;
        public string SymbolsPath => Path.Combine(Dir, "symbols.tsv");
        public string VectorsPath => Path.Combine(Dir, "vectors.bin");
        public string VectorsOldPath => Path.Combine(Dir, "vectors.bin.old");
    }

    private string Layer(ShardState s, string name)
        => ManifestLayer(s.OldManifest, name);

    private static string ManifestLayer(ShardManifest? manifest, string name)
        => manifest?.Layers.TryGetValue(name, out var info) == true ? info.Hash : string.Empty;

    private string LayerBuiltAt(ShardState s, string name, string fallback)
        => s.OldManifest?.Layers.TryGetValue(name, out var info) == true && info.BuiltAt.Length > 0
            ? info.BuiltAt : fallback;

    // ====================================================================
    // 主流程
    // ====================================================================
    public int Run(int? maxEntries)
    {
        if (_shards.Count == 0)
        {
            Console.Error.WriteLine("ERROR: 未发现任何分片，终止。");
            return 1;
        }
        Directory.CreateDirectory(_indexDir);

        string modelHash = ComputeModelHash(_cfg.ModelFile);
        BuildJson.BuildInfo? oldBuild = TryLoadBuild();
        EmbeddingCache cache = EmbeddingCache.Load(_cachePath);

        bool full = DetermineFallback(oldBuild, cache, maxEntries, out var reasons);
        if (reasons.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var reason in reasons)
                Console.WriteLine($"[incremental] 回退全量：{reason}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine("[incremental] 增量构建（复用未变分片的 defs+symbols / 向量 / Lucene / 图）");
        }

        var states = BuildStates(full, maxEntries, cache);
        var table = BuildGlobalTable(states);

        // ---- 符号表落盘（仅重新解析的分片）----
        foreach (var s in states.Where(x => x.Parsed))
        {
            SymbolsTsv.Save(s.SymbolsPath, s.Entries);
        }

        // ---- 嵌入层 ----
        EmbedLayer(states, cache, full, modelHash);
        // 只保留本次仍存在的符号槽位；删除/改名符号的旧 hash 不进入新表。
        RewriteCache(states, cache);

        // ---- Lucene ----
        var luceneBuilt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in states)
        {
            bool rebuild = full || s.Parsed
                || (s.OldManifest is not null && s.OldManifest.BaseId != s.BaseId)
                || Layer(s, LayerHashes.Lucene) != s.Env.LayerHashes[LayerHashes.Lucene]
                || !Directory.Exists(Path.Combine(s.Dir, "lucene"));
            if (rebuild)
            {
                BuildLucene(s);
                luceneBuilt.Add(s.Name);
                Console.WriteLine($"[{s.Name}] Lucene 重建：{s.Entries.Count} 条");
            }
        }

        // ---- 全局统一构图 ----
        string graphHash = states[0].Env.LayerHashes[LayerHashes.Graph];
        long nodeCount;
        long edgeCount;
        string graphBuiltAt;
        bool graphReused = !full && oldBuild?.Graph is { } g
                           && g.Hash.Equals(graphHash, StringComparison.Ordinal)
                           && GraphFilesExist();
        if (graphReused)
        {
            nodeCount = oldBuild!.Graph!.NodeCount;
            edgeCount = oldBuild!.Graph!.EdgeCount;
            graphBuiltAt = oldBuild!.Graph!.BuiltAt;
            Console.WriteLine($"[graph] 复用全局图：{nodeCount} 节点 / {edgeCount} 边");
        }
        else
        {
            var result = new GraphBuilder(
                    _cfg, _graphDir,
                    states.Select(s => new ShardGraphInput(s.Name, s.Env.ShardIndex, s.BaseId, s.Entries)).ToList(),
                    table,
                    states.Any(s => s.Source.IncludeCSharp))
                .Build();
            nodeCount = result.NodeCount;
            edgeCount = result.EdgeCount;
            graphBuiltAt = NowIso();
            Console.WriteLine($"[graph] 全局构图完成：{nodeCount} 节点 / {edgeCount} 边");
        }
        CleanupShardGraphFiles(states);

        // ---- manifest + build.json（最后阶段）----
        string now = NowIso();
        foreach (var s in states)
        {
            var manifest = new ShardManifest
            {
                FormatVersion = ShardManifest.CurrentFormatVersion,
                Shard = s.Name,
                ShardIndex = s.Env.ShardIndex,
                BaseId = s.BaseId,
                SymbolCount = s.Entries.Count,
                Truncated = s.Truncated,
                GameVersion = _gameVersion,
                SourceHash = s.Env.SourceHash,
                BuilderCommit = _builderCommit,
                OnnxProvider = s.Env.OnnxProvider,
                Precision = _precision,
                EmbeddingModel = _embeddingModel,
                PreprocessingRevision = _prepRevision,
                Dim = _dim,
                BuiltAt = now,
            };
            manifest.Layers[LayerHashes.Defs] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Defs],
                BuiltAt = s.Parsed ? now : LayerBuiltAt(s, LayerHashes.Defs, now),
            };
            manifest.Layers[LayerHashes.Symbols] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Symbols],
                BuiltAt = s.Parsed ? now : LayerBuiltAt(s, LayerHashes.Symbols, now),
            };
            manifest.Layers[LayerHashes.Vectors] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Vectors],
                BuiltAt = s.ReuseVectors ? LayerBuiltAt(s, LayerHashes.Vectors, now) : now,
            };
            manifest.Layers[LayerHashes.Lucene] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Lucene],
                BuiltAt = luceneBuilt.Contains(s.Name) ? now : LayerBuiltAt(s, LayerHashes.Lucene, now),
            };
            manifest.Layers[LayerHashes.Graph] = new LayerInfo { Hash = graphHash, BuiltAt = graphBuiltAt };
            SaveManifestAtomic(manifest, s.Dir);
        }
        WriteBuildJson(states, oldBuild, modelHash, nodeCount, edgeCount, graphHash, graphBuiltAt, now);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"\nDONE 索引构建完成：{states.Sum(s => s.Entries.Count)} 符号，{states.Count} 个分片，" +
            $"图 {nodeCount} 节点 / {edgeCount} 边，{new DirectoryInfo(_indexDir).FullName}");
        Console.ResetColor();
        return 0;
    }

    // ====================================================================
    // 回退判定
    // ====================================================================
    private bool DetermineFallback(
        BuildJson.BuildInfo? oldBuild, EmbeddingCache cache, int? maxEntries,
        out List<string> reasons)
    {
        reasons = new List<string>();
        bool full = _cfg.ForceClean;
        if (_cfg.ForceClean) reasons.Add("--force-clean 指定强制全量");

        if (oldBuild is null)
        {
            full = true;
            reasons.Add("缺少旧 build.json（旧索引不兼容，请执行全量重建）");
        }
        else
        {
            if (oldBuild.FormatVersion != BuildJson.CurrentFormatVersion)
            {
                full = true;
                reasons.Add($"build.json 格式版本 {oldBuild.FormatVersion} 不支持（请执行全量重建）");
            }
            if (oldBuild.Dim != _dim)
            {
                full = true;
                reasons.Add($"向量维度变化：旧 {oldBuild.Dim}，当前 {_dim}");
            }
            if (!string.IsNullOrEmpty(oldBuild.Model?.Name) &&
                !oldBuild.Model!.Name.Equals(_embeddingModel, StringComparison.OrdinalIgnoreCase))
            {
                full = true;
                reasons.Add($"嵌入模型变化：旧 {oldBuild.Model!.Name}，当前 {_embeddingModel}");
            }
        }

        if (!cache.Exists)
        {
            full = true;
            reasons.Add("缺少 embedding-cache.tsv（无法按符号复用向量）");
        }
        if (maxEntries is not null)
        {
            full = true;
            reasons.Add("指定 --max-entries（截断样本不可增量）");
        }

        foreach (var shard in _shards.OrderBy(s => s.Index))
        {
            string manifestPath = Path.Combine(_indexDir, shard.Name, "manifest.json");
            ShardManifest? manifest = null;
            if (File.Exists(manifestPath))
            {
                try { manifest = ShardManifest.Load(manifestPath); }
                catch { manifest = null; }
            }
            if (manifest is null)
            {
                full = true;
                reasons.Add($"{shard.Name} manifest 缺失（请执行全量重建）");
                continue;
            }
            if (manifest.FormatVersion != ShardManifest.CurrentFormatVersion)
            {
                full = true;
                reasons.Add($"{shard.Name} manifest 版本 {manifest.FormatVersion} 不支持（请执行全量重建）");
            }
            if (manifest.Truncated)
            {
                full = true;
                reasons.Add($"{shard.Name} 为 --max-entries 截断样本");
            }
            if (!manifest.Precision.Equals(_precision, StringComparison.OrdinalIgnoreCase))
            {
                full = true;
                reasons.Add($"{shard.Name} 向量精度变化：{manifest.Precision} → {_precision}");
            }
            if (manifest.PreprocessingRevision != 0 && manifest.PreprocessingRevision != _prepRevision)
            {
                full = true;
                reasons.Add($"{shard.Name} 预处理 revision 变化：{manifest.PreprocessingRevision} → {_prepRevision}");
            }
        }
        return full;
    }

    // ====================================================================
    // 解析 / 复用
    // ====================================================================
    private List<ShardState> BuildStates(bool full, int? maxEntries, EmbeddingCache cache)
    {
        var states = new List<ShardState>(_shards.Count);
        int remaining = maxEntries ?? int.MaxValue;

        foreach (var shard in _shards.OrderBy(s => s.Index))
        {
            var env = _envs.First(e => e.ShardName.Equals(shard.Name, StringComparison.Ordinal));
            string dir = Path.Combine(_indexDir, shard.Name);
            Directory.CreateDirectory(dir);

            ShardManifest? old = null;
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (File.Exists(manifestPath))
            {
                try { old = ShardManifest.Load(manifestPath); } catch { old = null; }
            }

            bool parsed;
            bool truncated = false;
            List<SymbolEntry> entries;

            bool canReuse = !full && old is not null && !old.Truncated
                            && ManifestLayer(old, LayerHashes.Defs).Equals(env.LayerHashes[LayerHashes.Defs], StringComparison.Ordinal)
                            && ManifestLayer(old, LayerHashes.Symbols).Equals(env.LayerHashes[LayerHashes.Symbols], StringComparison.Ordinal);

            string symbolsPath = Path.Combine(dir, "symbols.tsv");
            if (canReuse && File.Exists(symbolsPath))
            {
                entries = SymbolsTsv.Load(symbolsPath);
                bool slotsOk = entries.Count == old!.SymbolCount;
                for (int i = 0; slotsOk && i < entries.Count; i++)
                    slotsOk = cache.TryGetBySlot(shard.Name, i, out _);
                if (slotsOk)
                {
                    for (int i = 0; i < entries.Count; i++) entries[i].Id = i;
                    parsed = false;
                }
                else
                {
                    entries = ParseShard(shard, maxEntries, ref remaining, out truncated);
                    parsed = true;
                }
            }
            else
            {
                entries = ParseShard(shard, maxEntries, ref remaining, out truncated);
                parsed = true;
            }

            states.Add(new ShardState
            {
                Source = shard,
                Env = env,
                Dir = dir,
                Entries = entries,
                Parsed = parsed,
                Truncated = truncated,
                BaseId = 0,
                ReuseVectors = false,
                ReuseLucene = false,
                OldManifest = old,
            });
        }

        int baseId = 0;
        foreach (var s in states)
        {
            s.BaseId = baseId;
            baseId = checked(baseId + s.Entries.Count);
        }
        return states;
    }

    private List<SymbolEntry> ParseShard(
        ShardSource shard, int? maxEntries, ref int remaining, out bool truncated)
    {
        if (maxEntries is int m && remaining <= 0)
        {
            truncated = true;
            return new List<SymbolEntry>();
        }

        var parsed = ShardBuilder.ParseEntries(_cfg, shard);
        int? limit = maxEntries is null ? null : Math.Max(0, remaining);
        var (entries, wasTruncated) = ShardBuilder.Limit(parsed, limit);
        truncated = wasTruncated;
        if (maxEntries is not null)
            remaining -= entries.Count;

        IdAllocator.Assign(entries);
        return entries;
    }

    private static GlobalSymbolTable BuildGlobalTable(IReadOnlyList<ShardState> states)
        => GlobalSymbolTable.Build(states
            .Select(s => new ShardSymbolSet(s.Name, s.Env.ShardIndex, s.BaseId, s.Entries))
            .ToList());

    // ====================================================================
    // 嵌入层
    // ====================================================================
    private void EmbedLayer(
        IReadOnlyList<ShardState> states, EmbeddingCache cache, bool full, string modelHash)
    {
        // 计算每个槽位的内容哈希，并决定 vectors 复用。
        foreach (var s in states)
        {
            s.Hashes = new string[s.Entries.Count];
            for (int i = 0; i < s.Entries.Count; i++)
            {
                s.Hashes[i] = s.Parsed
                    ? ContentHash.ForEntry(s.Entries[i], _embeddingModel, _dim, _precision, _prepRevision)
                    : CacheSlotHash(cache, s, i);
            }

            s.ReuseVectors = !full
                && s.OldManifest is not null
                && Layer(s, LayerHashes.Vectors).Equals(s.Env.LayerHashes[LayerHashes.Vectors], StringComparison.Ordinal)
                && VectorsRecordCount(s.VectorsPath) == s.Entries.Count;
            s.ReuseLucene = !full && !s.Parsed
                && s.OldManifest is not null
                && Layer(s, LayerHashes.Lucene).Equals(s.Env.LayerHashes[LayerHashes.Lucene], StringComparison.Ordinal);
        }

        var rebuilding = states.Where(s => !s.ReuseVectors).ToList();
        if (rebuilding.Count == 0)
            return;

        // ---- 步骤 0：续跑判定 ----
        var fresh = new List<ShardState>();
        foreach (var s in rebuilding)
        {
            string ckPath = BuildCheckpoint.PathFor(_indexDir, s.Name);
            bool resume = !full && File.Exists(ckPath)
                          && BuildCheckpoint.ValidateVectors(s.VectorsPath, s.Entries.Count);
            if (resume)
            {
                s.Checkpoint = BuildCheckpoint.Load(ckPath);
                s.NextBatchNo = s.Checkpoint.LastBatchNo + 1;
                s.Resumed = true;
                s.Vectors = VectorsBin.OpenWritable(s.VectorsPath);
                Console.WriteLine($"[{s.Name}] 检查点校验通过，续跑（已完成批次 {s.Checkpoint.LastBatchNo}）");
            }
            else
            {
                BuildCheckpoint.Delete(ckPath);
                s.Checkpoint = null;
                fresh.Add(s);
            }
        }

        // ---- 步骤 1-3：新建 + 占位 + 复制命中 ----
        // 先为全部分片完成改名与占位，再统一打开旧文件复制，避免“旧文件仍被占用时改名”；
        // 也保证跨分片复制时旧文件已统一落到 vectors.bin.old。
        foreach (var s in fresh)
            PrepareFreshVectors(s, modelHash);

        var pool = new OldVectorsPool();
        foreach (var s in states)
            pool.SetDirectory(s.Name, s.Dir);
        var copied = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // 强制全量（--force-clean 或回退全量）不复用任何旧向量，全部走 ONNX 重算。
            if (!full)
            {
                foreach (var s in fresh)
                {
                    for (int i = 0; i < s.Entries.Count; i++)
                    {
                        string hash = s.Hashes[i];
                        if (!cache.TryGet(hash, out var oldShard, out var oldLocal)) continue;
                        var oldVectors = pool.Get(oldShard);
                        if (oldVectors is null) continue;
                        if (oldLocal >= oldVectors.RecordCount) continue;
                        s.Vectors!.WriteVector(i, oldVectors.GetVector((int)oldLocal));
                        copied.Add(hash);
                    }
                }
            }

            // ---- 步骤 4：未命中分批 ONNX ----
            var needed = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal);
            var slotsByHash = new Dictionary<string, List<(ShardState Shard, int Local)>>(StringComparer.Ordinal);

            foreach (var s in rebuilding)
            {
                for (int i = 0; i < s.Entries.Count; i++)
                {
                    string hash = s.Hashes[i];
                    if (IsFilled(s, hash, copied, cache)) continue;
                    needed.TryAdd(hash, s.Entries[i]);
                    if (!slotsByHash.TryGetValue(hash, out var list))
                        slotsByHash[hash] = list = new List<(ShardState, int)>();
                    list.Add((s, i));
                }
            }

            if (needed.Count > 0)
            {
                int abortAfter = ParseAbortAfterBatch();
                int totalBatches = (needed.Count + _cfg.BatchSize - 1) / _cfg.BatchSize;
                int done = 0;
                using var generator = new OnnxEmbeddingGenerator(
                    _cfg.ModelFile, _cfg.TokenizerPath, _dim,
                    _cfg.BatchSize, _cfg.MaxLength, _cfg.OnnxProvider);

                foreach (var batchHashes in needed.Keys.OrderBy(h => h, StringComparer.Ordinal).Chunk(_cfg.BatchSize))
                {
                    var texts = batchHashes
                        .Select(h => OnnxText(needed[h]))
                        .ToList();
                    var vectors = generator.Embed(texts);
                    for (int k = 0; k < batchHashes.Length; k++)
                        foreach (var slot in slotsByHash[batchHashes[k]])
                            slot.Shard.Vectors!.WriteVector(slot.Local, vectors[k]);

                    // 每个涉及分片各写一次，仅记录该分片相关 hash。
                    var involved = batchHashes
                        .SelectMany(h => slotsByHash[h].Select(x => x.Shard.Name))
                        .Distinct(StringComparer.Ordinal);
                    foreach (var name in involved)
                    {
                        var s = rebuilding.First(x => x.Name == name);
                        var shardHashes = batchHashes
                            .Where(h => slotsByHash[h].Any(x => x.Shard.Name == name))
                            .ToArray();
                        BuildCheckpoint.AppendBatch(
                            BuildCheckpoint.PathFor(_indexDir, name), s.NextBatchNo++, shardHashes);
                    }

                    done++;
                    Console.WriteLine($"[embed] 批次 {done}/{totalBatches}：{batchHashes.Length} 个新向量");
                    if (abortAfter > 0 && done >= abortAfter)
                        throw new InvalidOperationException(
                            $"RIMCP_TEST_ABORT_AFTER_BATCH={abortAfter}：模拟中断，已写入 {done} 个批次。");
                }
            }
        }
        finally
        {
            pool.Dispose();
            foreach (var s in rebuilding)
            {
                s.Vectors?.Dispose();
                s.Vectors = null;
            }
        }

        // ---- 步骤 5：成功收尾 ----
        foreach (var s in rebuilding)
        {
            if (File.Exists(s.VectorsOldPath)) File.Delete(s.VectorsOldPath);
            BuildCheckpoint.Delete(BuildCheckpoint.PathFor(_indexDir, s.Name));
        }
    }

    private void PrepareFreshVectors(ShardState s, string modelHash)
    {
        if (File.Exists(s.VectorsOldPath) && File.Exists(s.VectorsPath))
            File.Delete(s.VectorsPath);           // 上次中断留下的半成品
        if (File.Exists(s.VectorsPath))
            File.Move(s.VectorsPath, s.VectorsOldPath, overwrite: true);

        long seed = 0;
        if (modelHash.Length >= 16 &&
            long.TryParse(modelHash[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            seed = parsed;

        s.Vectors = VectorsBin.Create(s.VectorsPath, _dim, s.Entries.Count, seed);
        for (int i = 0; i < s.Entries.Count; i++)
            s.Vectors.WriteRecordId(i);
    }

    /// <summary>
    /// 该槽位是否已具备向量：本次复制的 hash 命中；续跑分片此前复制的 hash 仍在旧 cache 中，
    /// 此前 ONNX 计算的 hash 记录在检查点 <see cref="BuildCheckpoint.CompletedHashes"/>。
    /// </summary>
    private static bool IsFilled(
        ShardState s, string hash, IReadOnlySet<string> copied, EmbeddingCache cache)
    {
        if (copied.Contains(hash)) return true;
        if (s.Resumed && s.Checkpoint is not null && s.Checkpoint.CompletedHashes.Contains(hash))
            return true;
        // 续跑分片：上一轮从旧 cache 复制的向量仍在文件中（旧文件保留到成功后删除）。
        return s.Resumed && cache.TryGet(hash, out _, out _);
    }

    private static string CacheSlotHash(EmbeddingCache cache, ShardState s, int local)
        => cache.TryGetBySlot(s.Name, local, out var hash) ? hash : string.Empty;

    private static string OnnxText(SymbolEntry entry)
        => ContentHash.NormalizeText($"{entry.Signature}\n{entry.EmbedText ?? entry.Preview}");

    private static int ParseAbortAfterBatch()
    {
        var raw = Environment.GetEnvironmentVariable("RIMCP_TEST_ABORT_AFTER_BATCH");
        return int.TryParse(raw, out var n) && n > 0 ? n : 0;
    }

    private static long VectorsRecordCount(string path)
    {
        if (!File.Exists(path)) return -1;
        try
        {
            using var v = VectorsBin.Open(path);
            return v.RecordCount;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>旧向量文件池：优先使用 .old（本次已改名），否则使用仍在的 vectors.bin。</summary>
    private sealed class OldVectorsPool : IDisposable
    {
        private readonly Dictionary<string, VectorsBin?> _open = new(StringComparer.Ordinal);

        public VectorsBin? Get(string shard)
        {
            if (_open.TryGetValue(shard, out var cached)) return cached;

            // 需要知道分片目录；由调用方在构造前注入路径表。
            string dir = _dirs.TryGetValue(shard, out var d) ? d : string.Empty;
            if (dir.Length == 0) return _open[shard] = null;

            string oldPath = Path.Combine(dir, "vectors.bin.old");
            string curPath = Path.Combine(dir, "vectors.bin");
            string path = File.Exists(oldPath) ? oldPath : curPath;
            VectorsBin? handle = null;
            if (File.Exists(path))
            {
                try { handle = VectorsBin.Open(path); } catch { handle = null; }
            }
            _open[shard] = handle;
            return handle;
        }

        private readonly Dictionary<string, string> _dirs;

        public OldVectorsPool()
        {
            _dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public void SetDirectory(string shard, string dir) => _dirs[shard] = dir;

        public void Dispose()
        {
            foreach (var handle in _open.Values)
                handle?.Dispose();
            _open.Clear();
        }
    }

    private void RewriteCache(IReadOnlyList<ShardState> states, EmbeddingCache cache)
    {
        var rows = new List<EmbeddingCache.Row>();
        foreach (var s in states)
            for (int i = 0; i < s.Entries.Count; i++)
                if (s.Hashes[i].Length > 0)
                    rows.Add(new EmbeddingCache.Row(s.Hashes[i], s.Name, i));
        EmbeddingCache.Save(_cachePath, rows);
        Console.WriteLine($"[embed] embedding-cache.tsv 重写：{rows.Count} 行");
    }

    // ====================================================================
    // Lucene / 图 / 输出
    // ====================================================================
    private void BuildLucene(ShardState s)
    {
        string luceneDir = Path.Combine(s.Dir, "lucene");
        if (Directory.Exists(luceneDir)) Directory.Delete(luceneDir, recursive: true);
        using var builder = new LuceneIndexBuilder(luceneDir);
        foreach (var e in s.Entries)
            builder.Add(e, checked(s.BaseId + e.Id));
        builder.Commit();
    }

    private bool GraphFilesExist()
    {
        string[] names =
        {
            "graph.csr.bin", "graph.csc.bin", "graph.nodes.tsv", "graph.pagerank.tsv", "graph.meta.json",
        };
        return names.All(n => File.Exists(Path.Combine(_graphDir, n)));
    }

    private static void CleanupShardGraphFiles(IEnumerable<ShardState> states)
    {
        string[] names =
        {
            "graph.csr.bin", "graph.csc.bin", "graph.nodes.tsv", "graph.pagerank.tsv", "graph.meta.json",
        };
        foreach (var s in states)
            foreach (var name in names)
            {
                string path = Path.Combine(s.Dir, name);
                if (File.Exists(path)) File.Delete(path);
            }
    }

    private void SaveManifestAtomic(ShardManifest manifest, string dir)
    {
        manifest.EnsureLayers();
        string tmp = Path.Combine(dir, "manifest.json.tmp");
        string dst = Path.Combine(dir, "manifest.json");
        manifest.Save(tmp);
        File.Move(tmp, dst, overwrite: true);
    }

    private void WriteBuildJson(
        IReadOnlyList<ShardState> states, BuildJson.BuildInfo? oldBuild, string modelHash,
        long nodeCount, long edgeCount, string graphHash, string graphBuiltAt, string now)
    {
        var pointers = states.Select(s => new BuildJson.ShardPointer
        {
            Name = s.Name,
            Index = s.Env.ShardIndex,
            BaseId = s.BaseId,
            SymbolCount = s.Entries.Count,
            EdgeCount = 0,
            ManifestPath = Rel(Path.Combine(s.Dir, "manifest.json")),
            ManifestHash = ShardManifest.ComputeFileHash(Path.Combine(s.Dir, "manifest.json")),
        }).ToList();

        var info = new BuildJson.BuildInfo
        {
            Schema = BuildJson.Schema,
            FormatVersion = BuildJson.CurrentFormatVersion,
            Generator = new BuildJson.GeneratorInfo { Name = "RiMCP.Indexer", Version = "0.2.0" },
            Dim = _dim,
            Model = new BuildJson.ModelInfo
            {
                Name = _embeddingModel,
                File = Rel(_cfg.ModelFile),
                Hash = modelHash,
            },
            Counts = new BuildJson.CountsInfo
            {
                Symbols = pointers.Sum(p => p.SymbolCount),
                Nodes = nodeCount,
                Edges = edgeCount,
            },
            GameVersion = _gameVersion,
            BuilderCommit = _builderCommit,
            Shards = pointers,
            EnabledShards = states.Select(s => s.Name).ToList(),
            Graph = new BuildJson.GlobalGraphInfo
            {
                Path = Rel(_graphDir),
                Hash = graphHash,
                NodeCount = nodeCount,
                EdgeCount = edgeCount,
                BuiltAt = graphBuiltAt.Length > 0 ? graphBuiltAt : now,
                CrossShard = true,
            },
            Paths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rimeworldData"] = Rel(_cfg.Root),
                ["indexDir"] = Rel(_indexDir),
                ["graph"] = Rel(_graphDir),
                ["embeddingCache"] = Rel(_cachePath),
            },
            Granularity = "symbol-with-line",
            Lucene = new BuildJson.LuceneInfo { Ok = true, Version = "4.8.0-beta00016" },
        };

        string tmp = _buildPath + ".tmp";
        BuildJson.Save(tmp, info);
        File.Move(tmp, _buildPath, overwrite: true);
        Console.WriteLine("[build] build.json 已更新（formatVersion 3）");
    }

    private string Rel(string abs) => Path.GetRelativePath(_cfg.V2Root, abs).Replace('\\', '/');

    private BuildJson.BuildInfo? TryLoadBuild()
    {
        if (!File.Exists(_buildPath)) return null;
        try { return BuildJson.Load(_buildPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[build] build.json 读取失败（{ex.Message}）");
            return null;
        }
    }

    private static string ComputeModelHash(string modelFile)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(modelFile);
        return Convert.ToHexString(sha.ComputeHash(fs)).Substring(0, 16).ToLowerInvariant();
    }

    private static string NowIso() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    // ====================================================================
    // --graph-only
    // ====================================================================
    public int RunGraphOnly()
    {
        var oldBuild = TryLoadBuild();
        if (oldBuild is null || oldBuild.FormatVersion != BuildJson.CurrentFormatVersion)
        {
            Console.Error.WriteLine("ERROR: 旧索引不兼容（build.json 缺失或版本不符），请执行全量重建。");
            return 1;
        }

        var cache = EmbeddingCache.Load(_cachePath);
        if (!cache.Exists)
        {
            Console.Error.WriteLine("ERROR: 缺少嵌入缓存，无法判断文本是否变化，请执行全量重建或增量构建。");
            return 1;
        }

        var states = new List<ShardState>();
        foreach (var shard in _shards.OrderBy(s => s.Index))
        {
            var env = _envs.First(e => e.ShardName.Equals(shard.Name, StringComparison.Ordinal));
            string dir = Path.Combine(_indexDir, shard.Name);
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"ERROR: 分片 {shard.Name} manifest 缺失，请执行全量重建。");
                return 1;
            }
            var old = ShardManifest.Load(manifestPath);
            if (old.FormatVersion != ShardManifest.CurrentFormatVersion)
            {
                Console.Error.WriteLine($"ERROR: 分片 {shard.Name} manifest 版本不兼容，请执行全量重建。");
                return 1;
            }

            bool reuse = ManifestLayer(old, LayerHashes.Defs).Equals(env.LayerHashes[LayerHashes.Defs], StringComparison.Ordinal)
                         && ManifestLayer(old, LayerHashes.Symbols).Equals(env.LayerHashes[LayerHashes.Symbols], StringComparison.Ordinal);
            List<SymbolEntry> entries;
            if (reuse)
            {
                entries = SymbolsTsv.Load(Path.Combine(dir, "symbols.tsv"));
                if (entries.Count != old.SymbolCount)
                {
                    Console.Error.WriteLine($"ERROR: 分片 {shard.Name} 符号数变化，--graph-only 被拒绝，请执行增量构建。");
                    return 1;
                }
            }
            else
            {
                entries = ShardBuilder.ParseEntries(_cfg, shard);
                IdAllocator.Assign(entries);
                var previous = SymbolsTsv.Load(Path.Combine(dir, "symbols.tsv"));
                if (!SameSymbolSequence(previous, entries))
                {
                    Console.Error.WriteLine(
                        $"ERROR: 分片 {shard.Name} 符号序列发生变化，--graph-only 被拒绝，请执行增量构建。");
                    return 1;
                }
            }

            states.Add(new ShardState
            {
                Source = shard, Env = env, Dir = dir, Entries = entries,
                Parsed = !reuse, Truncated = false, BaseId = 0,
                ReuseVectors = true, ReuseLucene = true, OldManifest = old,
            });
        }

        int baseId = 0;
        foreach (var s in states)
        {
            s.BaseId = baseId;
            baseId = checked(baseId + s.Entries.Count);
        }

        // 文本内容哈希集合比较：相等才允许“仅图”。
        var oldHashes = cache.Rows.Select(r => r.ContentHash).ToHashSet(StringComparer.Ordinal);
        var newHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in states)
        {
            s.Hashes = new string[s.Entries.Count];
            for (int i = 0; i < s.Entries.Count; i++)
            {
                s.Hashes[i] = s.Parsed
                    ? ContentHash.ForEntry(s.Entries[i], _embeddingModel, _dim, _precision, _prepRevision)
                    : CacheSlotHash(cache, s, i);
                if (s.Hashes[i].Length > 0) newHashes.Add(s.Hashes[i]);
            }
        }
        if (!oldHashes.SetEquals(newHashes))
        {
            Console.Error.WriteLine(
                "ERROR: --graph-only 被拒绝：文本内容发生变化，需要重新嵌入。请执行增量构建（不要加 --graph-only）。");
            return 1;
        }

        var table = BuildGlobalTable(states);
        foreach (var s in states.Where(x => x.Parsed))
            SymbolsTsv.Save(s.SymbolsPath, s.Entries);

        string graphHash = states[0].Env.LayerHashes[LayerHashes.Graph];
        var result = new GraphBuilder(
                _cfg, _graphDir,
                states.Select(s => new ShardGraphInput(s.Name, s.Env.ShardIndex, s.BaseId, s.Entries)).ToList(),
                table,
                states.Any(s => s.Source.IncludeCSharp))
            .Build();
        CleanupShardGraphFiles(states);

        string now = NowIso();
        foreach (var s in states)
        {
            var manifest = s.OldManifest!;
            manifest.BaseId = s.BaseId;
            manifest.SymbolCount = s.Entries.Count;
            // 文本内容已确认未变，来源文件字节变化（如属性换序）也应同步，避免 manifest 与五层哈希自相矛盾。
            manifest.SourceHash = s.Env.SourceHash;
            manifest.BuiltAt = now;
            manifest.Layers[LayerHashes.Defs] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Defs], BuiltAt = s.Parsed ? now : LayerBuiltAt(s, LayerHashes.Defs, now),
            };
            manifest.Layers[LayerHashes.Symbols] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Symbols], BuiltAt = s.Parsed ? now : LayerBuiltAt(s, LayerHashes.Symbols, now),
            };
            manifest.Layers[LayerHashes.Graph] = new LayerInfo { Hash = graphHash, BuiltAt = now };
            // 文本集合未变 → 向量与 Lucene 仍然有效，写入新环境哈希。
            manifest.Layers[LayerHashes.Vectors] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Vectors], BuiltAt = LayerBuiltAt(s, LayerHashes.Vectors, now),
            };
            manifest.Layers[LayerHashes.Lucene] = new LayerInfo
            {
                Hash = s.Env.LayerHashes[LayerHashes.Lucene], BuiltAt = LayerBuiltAt(s, LayerHashes.Lucene, now),
            };
            SaveManifestAtomic(manifest, s.Dir);
        }

        WriteBuildJson(states, oldBuild, oldBuild.Model?.Hash ?? string.Empty,
            result.NodeCount, result.EdgeCount, graphHash, now, now);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nDONE --graph-only 完成：图 {result.NodeCount} 节点 / {result.EdgeCount} 边，未触碰向量与 Lucene。");
        Console.ResetColor();
        return 0;
    }

    private static bool SameSymbolSequence(
        IReadOnlyList<SymbolEntry> previous, IReadOnlyList<SymbolEntry> current)
    {
        if (previous.Count != current.Count) return false;
        for (int i = 0; i < previous.Count; i++)
        {
            var a = previous[i];
            var b = current[i];
            if (a.Signature != b.Signature || a.Kind != b.Kind || a.Source != b.Source
                || a.ShortName != b.ShortName || a.FileRelPath != b.FileRelPath
                || a.LineStart != b.LineStart)
                return false;
        }
        return true;
    }
}
