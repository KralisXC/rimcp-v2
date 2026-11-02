using System.Diagnostics;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Storage;
using RiMCP.Indexer.Config;
using RiMCP.Indexer.Embedding;
using RiMCP.Indexer.Graph;
using RiMCP.Indexer.Lucene;
using RiMCP.Indexer.Parsing;
using RiMCP.Indexer.Verification;

namespace RiMCP.Indexer.Building;

/// <summary>单个分片的构建结果。</summary>
internal sealed record ShardBuildResult(
    ShardSource Source,
    long BaseId,
    long SymbolCount,
    long EdgeCount,
    string ShardDir,
    ShardManifest Manifest);

/// <summary>按分片执行解析、嵌入、Lucene、图与 manifest 写出。</summary>
internal static class ShardBuilder
{
    /// <summary>解析该分片的 XML（core 额外包含 C#），不做截断。</summary>
    public static List<SymbolEntry> ParseEntries(IndexerConfig cfg, ShardSource shard)
    {
        var entries = new List<SymbolEntry>();
        if (shard.IncludeCSharp)
            entries.AddRange(CSharpParser.ParseDirectory(
                cfg.Root, cfg.CSharpSourceSubdir, cfg.MaxParallelism, cfg.SourceIncludeRoots));
        entries.AddRange(XmlParser.ParseFiles(cfg.Root, shard.XmlFiles, cfg.MaxParallelism));
        return entries;
    }

    /// <summary>解析并应用 --max-entries 截断。</summary>
    public static List<SymbolEntry> CollectEntries(IndexerConfig cfg, ShardSource shard, int? maxEntries)
    {
        var entries = ParseEntries(cfg, shard);
        if (maxEntries is int mx && mx >= 0 && entries.Count > mx)
            entries = entries.Take(mx).ToList();
        return entries;
    }

    /// <summary>全量构建一个分片。</summary>
    public static ShardBuildResult BuildFull(
        IndexerConfig cfg, ShardSource shard, long baseId,
        ShardCatalog.ShardEnvironment env, int? maxEntries)
    {
        var clock = Stopwatch.StartNew();
        var rawEntries = ParseEntries(cfg, shard);
        bool truncated = false;
        var entries = rawEntries;
        if (maxEntries is int mx && mx >= 0 && rawEntries.Count > mx)
        {
            truncated = true;
            entries = rawEntries.Take(mx).ToList();
        }
        if (entries.Count == 0)
        {
            Console.Error.WriteLine($"[{shard.Name}] 警告：未解析到任何符号，将写出空分片。");
        }

        entries = IdAllocator.Assign(entries);
        string shardDir = Path.Combine(cfg.Output, shard.Name);
        Directory.CreateDirectory(shardDir);

        string symbolsPath = Path.Combine(shardDir, "symbols.tsv");
        string vectorsPath = Path.Combine(shardDir, "vectors.bin");

        // P4 符号表
        clock.Restart();
        SymbolsTsv.Save(symbolsPath, entries);
        Console.WriteLine($"[{shard.Name}] symbols.tsv：{entries.Count} 条（{clock.Elapsed.TotalMilliseconds:F0}ms）");

        // P3 嵌入 + vectors.bin
        clock.Restart();
        using (var vec = VectorsBin.Create(vectorsPath, IndexerConfig.Dim, entries.Count, seedHash: 0))
        {
            if (entries.Count > 0)
            {
                using var gen = new OnnxEmbeddingGenerator(
                    cfg.ModelFile, cfg.TokenizerPath, IndexerConfig.Dim,
                    cfg.BatchSize, cfg.MaxLength, cfg.OnnxProvider);
                int done = 0;
                foreach (var chunk in Chunk(entries, cfg.BatchSize))
                {
                    var texts = chunk.Select(BuildEmbedText).ToArray();
                    var half = gen.Embed(texts);
                    for (int i = 0; i < half.Length; i++)
                        vec.WriteVector(done + i, half[i]);
                    done += half.Length;
                    if (done % (cfg.BatchSize * 20) < cfg.BatchSize || done == entries.Count)
                        Console.WriteLine($"  {shard.Name} 嵌入 {done}/{entries.Count}（{clock.Elapsed.TotalSeconds:F0}s）");
                }
            }
        }
        Console.WriteLine($"[{shard.Name}] 嵌入完成（{clock.Elapsed.TotalSeconds:F1}s）");

        // P5 Lucene：id 用全局 id，保证多分片检索结果可直接回查符号表
        clock.Restart();
        using (var lucene = new LuceneIndexBuilder(Path.Combine(shardDir, "lucene")))
        {
            foreach (var e in entries)
                lucene.Add(e, checked((int)(baseId + e.Id)));
            lucene.Commit();
        }
        Console.WriteLine($"[{shard.Name}] Lucene：{entries.Count} 条（{clock.Elapsed.TotalSeconds:F1}s）");

        // P6 图
        clock.Restart();
        long edgeCount = new GraphBuilder(cfg, shardDir, shard.IncludeCSharp).Build();
        Console.WriteLine($"[{shard.Name}] 图：{edgeCount} 条边（{clock.Elapsed.TotalSeconds:F1}s）");

        // P8 自检
        var errors = SelfCheck.Run(cfg.Root, shardDir, vectorsPath, symbolsPath, entries, IndexerConfig.Dim);
        if (errors.Count > 0)
            throw new InvalidDataException(
                $"[{shard.Name}] 自检未通过：{string.Join("；", errors)}");

        // manifest
        string builtAt = DateTime.UtcNow.ToString("o");
        var manifest = new ShardManifest
        {
            Shard = shard.Name,
            ShardIndex = shard.Index,
            BaseId = baseId,
            SymbolCount = entries.Count,
            Truncated = truncated,
            GameVersion = env.GameVersion,
            SourceHash = env.SourceHash,
            BuilderCommit = env.BuilderCommit,
            OnnxProvider = env.OnnxProvider,
            Precision = env.Precision,
            EmbeddingModel = env.EmbeddingModel,
            BuiltAt = builtAt,
        };
        foreach (var layer in ShardManifest.LayerNames)
        {
            manifest.Layers[layer] = new LayerInfo
            {
                Hash = env.LayerHashes.TryGetValue(layer, out var h) ? h : string.Empty,
                BuiltAt = builtAt,
            };
        }
        manifest.Save(Path.Combine(shardDir, "manifest.json"));

        return new ShardBuildResult(shard, baseId, entries.Count, edgeCount, shardDir, manifest);
    }

    /// <summary>只重建一个分片的图，并更新 manifest 的 graph 层。</summary>
    public static long BuildGraphOnly(IndexerConfig cfg, ShardSource shard)
    {
        string shardDir = Path.Combine(cfg.Output, shard.Name);
        string symbolsPath = Path.Combine(shardDir, "symbols.tsv");
        string manifestPath = Path.Combine(shardDir, "manifest.json");
        if (!File.Exists(symbolsPath))
            throw new FileNotFoundException($"symbols.tsv 不存在：{symbolsPath}（请先全量构建）");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"manifest.json 不存在：{manifestPath}（旧索引请全量重建）");

        long edges = new GraphBuilder(cfg, shardDir, shard.IncludeCSharp).Build();

        var manifest = ShardManifest.Load(manifestPath);
        string symbolsHash = manifest.Layers.TryGetValue(ShardCatalog.LayerHashes.Symbols, out var s)
            ? s.Hash : string.Empty;
        manifest.Layers[ShardCatalog.LayerHashes.Graph] = new LayerInfo
        {
            Hash = ShardCatalog.LayerHashes.GraphFromSymbols(symbolsHash),
            BuiltAt = DateTime.UtcNow.ToString("o"),
        };
        manifest.BuiltAt = DateTime.UtcNow.ToString("o");
        manifest.Save(manifestPath);
        return edges;
    }

    private static string BuildEmbedText(SymbolEntry e) => $"{e.Signature}\n{e.Preview}";

    private static IEnumerable<T[]> Chunk<T>(IReadOnlyList<T> list, int size)
    {
        for (int i = 0; i < list.Count; i += size)
            yield return list.Skip(i).Take(size).ToArray();
    }
}
