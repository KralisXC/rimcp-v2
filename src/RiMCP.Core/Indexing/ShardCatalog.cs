using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RiMCP.Core.Indexing;

/// <summary>
/// 一个分片的物理来源：XML Def 文件与程序集/反编译源码文件。
/// </summary>
public sealed record ShardSource(
    string Name,
    int Index,
    string DataDirName,
    IReadOnlyList<string> XmlFiles,
    IReadOnlyList<string> AssemblyFiles,
    bool IncludeCSharp);

/// <summary>
/// 分片发现、来源哈希与层级哈希计算。Indexer、McpServer、Launcher 共用。
/// </summary>
public static class ShardCatalog
{
    /// <summary>默认 C# 源码子目录（相对 RimWorldData 根）。</summary>
    public const string DefaultCSharpSubdir = "RimWorld_Decompiled/Assembly-CSharp";

    /// <summary>当前解析器修订号；解析逻辑变化时递增，可使 symbols 层失效。</summary>
    public const int ParserRevision = 1;

    /// <summary>当前图构建修订号。</summary>
    public const int GraphRevision = 1;

    /// <summary>当前 Lucene 构建修订号。</summary>
    public const int LuceneRevision = 1;

    /// <summary>当前嵌入预处理修订号（前缀、池化、归一化等）。</summary>
    public const int EmbeddingPreprocessingRevision = 1;

    /// <summary>
    /// 扫描 RimWorldData/Data，得到 core + DLC 分片。core 永远第一，其余按目录名排序。
    /// </summary>
    public static IReadOnlyList<ShardSource> Discover(string rootAbs, string csharpSubdir)
    {
        string dataRoot = Path.Combine(rootAbs, "Data");
        if (!Directory.Exists(dataRoot))
            throw new DirectoryNotFound(dataRoot);

        string csharpDir = Path.GetFullPath(Path.Combine(rootAbs, csharpSubdir.Replace('\\', '/')));
        var result = new List<ShardSource>();

        // ---- core：Data/Core（若有）或整个 Data（旧布局回退）----
        string coreData = Path.Combine(dataRoot, ShardLayout.CoreDataDirName);
        IReadOnlyList<string> coreXml = Directory.Exists(coreData)
            ? EnumerateFiles(coreData, "*.xml")
                .Concat(EnumerateFiles(dataRoot, "*.xml", recursive: false))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : EnumerateFiles(dataRoot, "*.xml", recursive: true)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var coreAssemblies = new List<string>();
        if (Directory.Exists(csharpDir))
            coreAssemblies.AddRange(EnumerateFiles(csharpDir, "*.cs"));
        coreAssemblies.AddRange(EnumerateFiles(Path.Combine(coreData, "Assemblies"), "*.dll"));
        coreAssemblies.Sort(StringComparer.OrdinalIgnoreCase);

        result.Add(new ShardSource(
            ShardLayout.Core, 0, ShardLayout.CoreDataDirName, coreXml, coreAssemblies,
            Directory.Exists(csharpDir)));

        // ---- DLC：Data 下除 Core 的子目录，按目录名排序 ----
        int index = 1;
        foreach (var dir in Directory.GetDirectories(dataRoot)
                     .Select(Path.GetFileName)
                     .Where(n => n != null && !n.Equals(ShardLayout.CoreDataDirName, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.Combine(dataRoot, dir!);
            var xml = EnumerateFiles(full, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            var dll = EnumerateFiles(Path.Combine(full, "Assemblies"), "*.dll")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (xml.Length == 0 && dll.Length == 0) continue;

            result.Add(new ShardSource(
                ShardLayout.NameForDataDir(dir!), index++, dir!, xml, dll, IncludeCSharp: false));
        }

        return result;
    }

    /// <summary>读取游戏版本：优先环境变量 RIMCP_GAME_VERSION，否则取各 About.xml 的 supportedVersions 众数。</summary>
    public static string DetectGameVersion(string rootAbs)
    {
        var env = Environment.GetEnvironmentVariable("RIMCP_GAME_VERSION");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string dataRoot = Path.Combine(rootAbs, "Data");
        if (Directory.Exists(dataRoot))
        {
            foreach (var about in Directory.GetDirectories(dataRoot)
                         .Select(d => Path.Combine(d, "About", "About.xml"))
                         .Where(File.Exists))
            {
                try
                {
                    var doc = XDocument.Load(about);
                    var version = doc.Root?
                        .Element("supportedVersions")?
                        .Elements("li")?
                        .Select(e => e.Value.Trim())
                        .FirstOrDefault(v => v.Length > 0);
                    if (!string.IsNullOrEmpty(version))
                        counts[version] = counts.TryGetValue(version, out var n) ? n + 1 : 1;
                }
                catch
                {
                    // About.xml 损坏时跳过，不阻塞构建
                }
            }
        }

        return counts.Count == 0
            ? "unknown"
            : counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    /// <summary>构建一个分片在当前环境下的期望值（来源哈希 + 五层哈希）。</summary>
    public static ShardEnvironment BuildEnvironment(
        ShardSource shard, string rootAbs, string gameVersion, string onnxProvider,
        string precision, string embeddingModel, int dim, string builderCommit,
        ProbeCache? probeCache = null)
    {
        string sourceHash = ComputeSourceHash(rootAbs, shard, probeCache);
        return new ShardEnvironment(
            shard.Name, shard.Index, gameVersion, sourceHash, onnxProvider, precision,
            embeddingModel, dim, builderCommit,
            LayerHashes.Compute(sourceHash, embeddingModel, dim, precision));
    }

    /// <summary>
    /// 一次计算全部分片在当前环境下的期望值。graph 层使用全局统一图哈希
    /// （由所有分片 symbols 层哈希合成），保证跨分片边与任一分片符号变化都被覆盖。
    /// Indexer 与 McpServer 必须共用本方法，禁止各写一套。
    /// </summary>
    public static IReadOnlyList<ShardEnvironment> BuildEnvironments(
        IReadOnlyList<ShardSource> shards, string rootAbs, string gameVersion, string onnxProvider,
        string precision, string embeddingModel, int dim, string builderCommit,
        ProbeCache? probeCache = null)
    {
        var local = new List<ShardEnvironment>(shards.Count);
        foreach (var shard in shards)
        {
            local.Add(BuildEnvironment(
                shard, rootAbs, gameVersion, onnxProvider, precision,
                embeddingModel, dim, builderCommit, probeCache));
        }

        string globalSymbolsHash = LayerHashes.ComputeGlobalSymbolsHash(
            local.Select(e => (e.ShardName, e.LayerHashes[LayerHashes.Symbols])));
        string globalGraphHash = LayerHashes.ComputeGlobalGraphHash(globalSymbolsHash);

        var result = new List<ShardEnvironment>(local.Count);
        foreach (var env in local)
        {
            var hashes = new Dictionary<string, string>(env.LayerHashes, StringComparer.Ordinal)
            {
                [LayerHashes.Graph] = globalGraphHash,
            };
            result.Add(new ShardEnvironment(
                env.ShardName, env.ShardIndex, env.GameVersion, env.SourceHash,
                env.OnnxProvider, env.Precision, env.EmbeddingModel, env.Dim,
                env.BuilderCommit, hashes));
        }
        return result;
    }

    /// <summary>
    /// 该分片 Def + 程序集文件的内容哈希。
    /// 传入 <paramref name="probeCache"/> 时优先复用 mtime/size 未变化的缓存值，跳过读取与 SHA-256。
    /// </summary>
    public static string ComputeSourceHash(string rootAbs, ShardSource shard, ProbeCache? probeCache = null)
        => probeCache is null
            ? SourceHasher.HashFiles(rootAbs, shard.XmlFiles.Concat(shard.AssemblyFiles))
            : probeCache.ComputeSourceHash(
                shard.Name, rootAbs, shard.XmlFiles.Concat(shard.AssemblyFiles));

    private static IReadOnlyList<string> EnumerateFiles(string dir, string pattern, bool recursive = true)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .ToArray()
            : Array.Empty<string>();

    /// <summary>由来源哈希派生的五层逻辑哈希；用于决定层是否失效，而不是哈希庞大的输出文件。</summary>
    public static class LayerHashes
    {
        public const string Defs = "defs";
        public const string Symbols = "symbols";
        public const string Graph = "graph";
        public const string Vectors = "vectors";
        public const string Lucene = "lucene";

        public static Dictionary<string, string> Compute(
            string sourceHash, string embeddingModel, int dim, string precision)
        {
            string defs = Hash("defs|" + sourceHash);
            string symbols = Hash($"symbols|{defs}|parser={ParserRevision}");
            string graph = Hash($"graph|{symbols}|graph={GraphRevision}");
            string vectors = Hash(
                $"vectors|{symbols}|model={embeddingModel}|dim={dim}|precision={precision}|prep={EmbeddingPreprocessingRevision}");
            string lucene = Hash($"lucene|{symbols}|lucene={LuceneRevision}");
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Defs] = defs,
                [Symbols] = symbols,
                [Graph] = graph,
                [Vectors] = vectors,
                [Lucene] = lucene,
            };
        }

        /// <summary>由既有 symbols 层哈希推导 graph 层哈希（--graph-only 复用符号层时使用）。</summary>
        public static string GraphFromSymbols(string symbolsHash)
            => Hash($"graph|{symbolsHash}|graph={GraphRevision}");

        /// <summary>
        /// 全局符号表哈希：H(sorted(join(shard|symbolsHash)))。任一分片符号变化都会使全局图失效。
        /// </summary>
        public static string ComputeGlobalSymbolsHash(IEnumerable<(string Shard, string SymbolsHash)> shards)
        {
            var joined = string.Join(
                "\n",
                shards.OrderBy(s => s.Shard, StringComparer.Ordinal)
                      .Select(s => $"{s.Shard}|{s.SymbolsHash}"));
            return Hash(joined);
        }

        /// <summary>全局图哈希：跨分片边参与输入，避免“分片 hash 都没变但图变了”的漏判。</summary>
        public static string ComputeGlobalGraphHash(string globalSymbolsHash)
            => Hash($"graph|{globalSymbolsHash}|graph={GraphRevision}|cross-shard=1");

        private static string Hash(string text)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>分片在当前环境下的期望值。</summary>
    public sealed record ShardEnvironment(
        string ShardName,
        int ShardIndex,
        string GameVersion,
        string SourceHash,
        string OnnxProvider,
        string Precision,
        string EmbeddingModel,
        int Dim,
        string BuilderCommit,
        IReadOnlyDictionary<string, string> LayerHashes);

    /// <summary>按相对路径 + 内容对文件集合做确定性 SHA-256。</summary>
    public static class SourceHasher
    {
        public static string HashFiles(string rootAbs, IEnumerable<string> files)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            foreach (var file in files
                         .Where(File.Exists)
                         .Select(f => new
                         {
                             Abs = Path.GetFullPath(f),
                             Rel = Path.GetRelativePath(rootAbs, Path.GetFullPath(f))
                                 .Replace('\\', '/'),
                         })
                         .OrderBy(x => x.Rel, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(file.Rel));
                hash.AppendData(new byte[] { 0 });
                using var stream = File.OpenRead(file.Abs);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
                hash.AppendData(new byte[] { 0 });
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    private sealed class DirectoryNotFound : Exception
    {
        public DirectoryNotFound(string path)
            : base($"XML Def 目录不存在：{path}") { }
    }
}
