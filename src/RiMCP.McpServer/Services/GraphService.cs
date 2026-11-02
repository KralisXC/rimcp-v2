using System.Text;
using RiMCP.Core.Models;

namespace RiMCP.McpServer.Services;

/// <summary>
/// 图查询服务：按分片加载 CSR/CSC 稀疏图 + PageRank，并把分片本地 id 映射为全局 id。
/// <list type="bullet">
/// <item>uses（下游）：CSR 行优先出边，即“它依赖了谁”。</item>
/// <item>usedBy（上游）：CSC 列优先入边，即“谁依赖了它”。</item>
/// </list>
/// 边 kind 编码即 <see cref="EdgeKinds"/> 数组下标。
/// </summary>
internal sealed class GraphService
{
    /// <summary>edgeKinds 固定顺序（与 Indexer GraphBuilder.EdgeKinds 对齐，顺序不可改）。</summary>
    internal static readonly string[] EdgeKinds =
    {
        "inherits", "implements", "calls", "references",
        "xml-inherits", "xml-references", "xml-binds-class",
        "xml-uses-comp", "csharp-used-by-def",
    };

    // kind 常量（与 EdgeKinds 下标一致）
    internal const byte Inherits = 0;
    internal const byte Implements = 1;
    private const byte Calls = 2;
    private const byte References = 3;
    private const byte XmlInherits = 4;
    private const byte CSharpUsedByDef = 8; // 反向边，Uses 查询应排除

    private sealed class ShardGraph
    {
        public required int BaseId { get; init; }
        public required int NodeCount { get; init; }
        public required int[] CsrPointers { get; init; }
        public required int[] CsrCols { get; init; }
        public required byte[] CsrKinds { get; init; }
        public required int[] CscPointers { get; init; }
        public required int[] CscRows { get; init; }
        public required byte[] CscKinds { get; init; }
    }

    private readonly IReadOnlyList<ShardGraph> _graphs;
    private readonly Dictionary<int, double> _pagerank;
    private readonly IndexService _index;

    /// <summary>节点总数（所有分片符号数之和）。</summary>
    public int NodeCount { get; }

    /// <summary>有向边总数。</summary>
    public int EdgeCount { get; }

    public GraphService(IndexService index)
    {
        _index = index;
        var graphs = new List<ShardGraph>();
        _pagerank = new Dictionary<int, double>();
        int totalNodes = 0;
        int totalEdges = 0;

        foreach (var runtime in index.Shards)
        {
            string baseAbs = Path.Combine(runtime.ShardDir, "graph");
            var (csrPointers, csrCols, csrKinds) = LoadBinary(baseAbs + ".csr.bin", "CSR1");
            var (cscPointers, cscRows, cscKinds) = LoadBinary(baseAbs + ".csc.bin", "CSC1");
            int nodeCount = LoadNodeCount(baseAbs + ".csr.bin");
            if (nodeCount != runtime.SymbolCount)
                throw new InvalidDataException(
                    $"分片 {runtime.Manifest.Shard} 图节点数 {nodeCount} 与符号数 {runtime.SymbolCount} 不符。");

            graphs.Add(new ShardGraph
            {
                BaseId = runtime.BaseId,
                NodeCount = nodeCount,
                CsrPointers = csrPointers,
                CsrCols = csrCols,
                CsrKinds = csrKinds,
                CscPointers = cscPointers,
                CscRows = cscRows,
                CscKinds = cscKinds,
            });

            foreach (var (id, score) in LoadPageRank(
                         index, runtime.BaseId, runtime.SymbolCount, baseAbs + ".pagerank.tsv"))
                _pagerank[id] = score;

            totalNodes += nodeCount;
            totalEdges += csrCols.Length;
        }

        _graphs = graphs;
        NodeCount = totalNodes;
        EdgeCount = totalEdges;
    }

    private ShardGraph? FindShard(int globalId)
    {
        foreach (var g in _graphs)
            if (globalId >= g.BaseId && globalId < g.BaseId + g.NodeCount)
                return g;
        return null;
    }

    /// <summary>下游：该符号依赖了谁（出边）。排除 CSharpUsedByDef 反向边，可选按邻居来源过滤。</summary>
    public IReadOnlyList<GraphNeighbor> Uses(int nodeId, string? kindFilter)
    {
        var result = new List<GraphNeighbor>();
        var shard = FindShard(nodeId);
        if (shard is null) return result;

        int local = nodeId - shard.BaseId;
        for (int p = shard.CsrPointers[local]; p < shard.CsrPointers[local + 1]; p++)
        {
            byte kind = shard.CsrKinds[p];
            if (kind == CSharpUsedByDef) continue; // 反向边不作为用途
            int globalTarget = shard.BaseId + shard.CsrCols[p];
            if (PassFilter(globalTarget, kindFilter))
                result.Add(Make(globalTarget, kind));
        }
        return Sort(result);
    }

    /// <summary>上游：谁依赖了该符号（入边）。可选按邻居来源过滤。</summary>
    public IReadOnlyList<GraphNeighbor> UsedBy(int nodeId, string? kindFilter)
    {
        var result = new List<GraphNeighbor>();
        var shard = FindShard(nodeId);
        if (shard is null) return result;

        int local = nodeId - shard.BaseId;
        for (int p = shard.CscPointers[local]; p < shard.CscPointers[local + 1]; p++)
        {
            int globalSource = shard.BaseId + shard.CscRows[p];
            if (PassFilter(globalSource, kindFilter))
                result.Add(Make(globalSource, shard.CscKinds[p]));
        }
        return Sort(result);
    }

    /// <summary>继承树：down=子类（入边的 inherits 边之源），up=基类（出边的 inherits 边之目标）。</summary>
    public IReadOnlyList<GraphNeighbor> Inheritors(int nodeId, bool down)
    {
        var result = new List<GraphNeighbor>();
        var shard = FindShard(nodeId);
        if (shard is null) return result;

        int local = nodeId - shard.BaseId;
        if (down)
        {
            for (int p = shard.CscPointers[local]; p < shard.CscPointers[local + 1]; p++)
                if (shard.CscKinds[p] is Inherits or XmlInherits)
                    result.Add(Make(shard.BaseId + shard.CscRows[p], shard.CscKinds[p]));
        }
        else
        {
            for (int p = shard.CsrPointers[local]; p < shard.CsrPointers[local + 1]; p++)
                if (shard.CsrKinds[p] is Inherits or XmlInherits)
                    result.Add(Make(shard.BaseId + shard.CsrCols[p], shard.CsrKinds[p]));
        }
        return Sort(result);
    }

    private GraphNeighbor Make(int id, byte kind)
        => new(id, kind, EdgeKinds[kind % EdgeKinds.Length], _pagerank.TryGetValue(id, out var s) ? s : 0.0);

    private bool PassFilter(int neighborId, string? kindFilter)
    {
        if (string.IsNullOrWhiteSpace(kindFilter)) return true;
        var e = _index.EntryOf(neighborId);
        if (e is null) return false;
        string k = kindFilter.ToLowerInvariant();
        bool wantCs = k is "csharp" or "cs";
        bool wantXml = k is "xml" or "def";
        return wantCs ? e.Source == SourceType.CSharp
             : wantXml ? e.Source == SourceType.Xml
             : true;
    }

    private static List<GraphNeighbor> Sort(List<GraphNeighbor> list)
    {
        list.Sort((a, b) => b.PageRank.CompareTo(a.PageRank) != 0
            ? b.PageRank.CompareTo(a.PageRank)
            : a.NodeId.CompareTo(b.NodeId));
        return list;
    }

    private static int LoadNodeCount(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(r.ReadBytes(4));
        if (magic != "CSR1") throw new InvalidDataException($"graph magic 不匹配：{path}");
        r.ReadInt32();         // version
        return r.ReadInt32();  // nodeCount
    }

    private static (int[], int[], byte[]) LoadBinary(string path, string expectedMagic)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != expectedMagic)
            throw new InvalidDataException($"graph magic 不匹配：{path}（期望 {expectedMagic}，实际 {magic}）");
        int version = reader.ReadInt32();
        if (version != 1) throw new InvalidDataException($"graph 版本 {version} 不支持。");
        int nodeCount = reader.ReadInt32();
        int edgeCount = reader.ReadInt32();
        var pointers = new int[nodeCount + 1];
        for (int i = 0; i <= nodeCount; i++) pointers[i] = reader.ReadInt32();
        var indices = new int[edgeCount];
        for (int i = 0; i < edgeCount; i++) indices[i] = reader.ReadInt32();
        int kindsLength = reader.ReadInt32();
        if (kindsLength != edgeCount)
            throw new InvalidDataException($"kinds 长度 {kindsLength} 与边数 {edgeCount} 不符。");
        var kinds = reader.ReadBytes(edgeCount);
        return (pointers, indices, kinds);
    }

    /// <summary>
    /// pagerank.tsv 只有 signature + 分数；先在本分片范围内建立 signature → 全局 id，
    /// 再回填分数，避免跨分片同名签名串号。
    /// </summary>
    private static Dictionary<int, double> LoadPageRank(
        IndexService index, int baseId, int symbolCount, string path)
    {
        var map = new Dictionary<int, double>();
        if (!File.Exists(path)) return map;

        var sigToGlobal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int local = 0; local < symbolCount; local++)
        {
            var e = index.EntryOf(baseId + local);
            if (e is not null) sigToGlobal.TryAdd(e.Signature, baseId + local);
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string sig = line[..tab];
            if (!double.TryParse(line[(tab + 1)..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var score)) continue;
            if (sigToGlobal.TryGetValue(sig, out var id))
                map[id] = score;
        }
        return map;
    }
}

/// <summary>一条图邻接结果。</summary>
internal readonly record struct GraphNeighbor(int NodeId, byte EdgeKind, string EdgeKindName, double PageRank);
