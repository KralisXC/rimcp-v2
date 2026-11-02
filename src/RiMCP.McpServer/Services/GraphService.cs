using System.Text;
using RiMCP.Core.Models;
using RiMCP.Core.Paths;

namespace RiMCP.McpServer.Services;

/// <summary>
/// 图查询服务：加载 <c>index/graph/</c> 下的单一全局图（节点 id 即全局符号 id），
/// 并在加载时一次性丢弃端点落在未启用分片 id 段的边。
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
    private const byte XmlInherits = 4;
    private const byte CSharpUsedByDef = 8; // 反向边，Uses 查询应排除

    private readonly int[] _csrPointers;
    private readonly int[] _csrCols;
    private readonly byte[] _csrKinds;
    private readonly int[] _cscPointers;
    private readonly int[] _cscRows;
    private readonly byte[] _cscKinds;
    private readonly Dictionary<int, double> _pagerank;
    private readonly IndexService _index;

    /// <summary>全局节点总数（id 空间不变，含未启用分片的空洞）。</summary>
    public int NodeCount { get; }

    /// <summary>过滤后（仅两端均启用）的有向边总数。</summary>
    public int EdgeCount { get; }

    /// <summary>是否存在未启用分片（查询结果因此可能被截断）。</summary>
    public bool TruncatedByDisabledShards { get; }

    public GraphService(IndexService index)
    {
        _index = index;

        string graphDir = index.Build.Graph?.Path is { Length: > 0 } graphRel
            ? PathUtil.Resolve(index.V2Root, graphRel)
            : Path.Combine(index.IndexDir, "graph");

        string csrPath = Path.Combine(graphDir, "graph.csr.bin");
        string cscPath = Path.Combine(graphDir, "graph.csc.bin");
        if (!File.Exists(csrPath) || !File.Exists(cscPath))
            throw new InvalidDataException($"全局图文件不存在：{graphDir}（请重新执行 Indexer 构图）。");

        var (csrPointers, csrCols, csrKinds, csrNodeCount) = LoadBinary(csrPath, "CSR1");
        var (cscPointers, cscRows, cscKinds, cscNodeCount) = LoadBinary(cscPath, "CSC1");
        if (csrNodeCount != cscNodeCount)
            throw new InvalidDataException("graph.csr.bin 与 graph.csc.bin 节点数不一致。");
        if (index.Build.Graph is { NodeCount: > 0 } pointer && pointer.NodeCount != csrNodeCount)
            throw new InvalidDataException(
                $"全局图节点数 {csrNodeCount} 与 build.json {pointer.NodeCount} 不符（索引不一致，请重新执行 Indexer）。");

        NodeCount = csrNodeCount;

        // ---- 启用分片掩码：任一端点落在未启用 id 段的边全部丢弃 ----
        var enabled = new bool[NodeCount];
        foreach (var (baseId, count) in index.EnabledRanges)
        {
            int start = Math.Max(0, baseId);
            int end = Math.Min(NodeCount, baseId + count);
            for (int id = start; id < end; id++) enabled[id] = true;
        }
        TruncatedByDisabledShards = index.EnabledShards.Count < index.Build.Shards.Count;

        (csrPointers, csrCols, csrKinds) = Filter(csrPointers, csrCols, csrKinds, enabled);
        (cscPointers, cscRows, cscKinds) = Filter(cscPointers, cscRows, cscKinds, enabled);

        _csrPointers = csrPointers;
        _csrCols = csrCols;
        _csrKinds = csrKinds;
        _cscPointers = cscPointers;
        _cscRows = cscRows;
        _cscKinds = cscKinds;
        EdgeCount = csrCols.Length;

        _pagerank = LoadPageRank(Path.Combine(graphDir, "graph.pagerank.tsv"), enabled);

        Console.Error.WriteLine(
            $"[graph] 全局图：{NodeCount} 节点 / {EdgeCount} 边" +
            (TruncatedByDisabledShards ? $"（已按启用分片截断，启用 {string.Join(", ", index.EnabledShards)}）" : ""));
    }

    /// <summary>
    /// 重建 CSR/CSC：仅保留两端均在启用 id 段的边。行本身未启用时其出/入边全部丢弃。
    /// 索引仍为全局 id，指针按行重排。
    /// </summary>
    private static (int[] Pointers, int[] Indices, byte[] Kinds) Filter(
        int[] pointers, int[] indices, byte[] kinds, bool[] enabled)
    {
        int n = enabled.Length;
        var counts = new int[n];
        for (int row = 0; row < n; row++)
        {
            if (!enabled[row]) continue;
            int count = 0;
            for (int p = pointers[row]; p < pointers[row + 1]; p++)
                if (enabled[indices[p]]) count++;
            counts[row] = count;
        }

        var newPointers = new int[n + 1];
        for (int i = 0; i < n; i++) newPointers[i + 1] = newPointers[i] + counts[i];

        var newIndices = new int[newPointers[n]];
        var newKinds = new byte[newPointers[n]];
        var pos = (int[])newPointers.Clone();
        for (int row = 0; row < n; row++)
        {
            if (!enabled[row]) continue;
            for (int p = pointers[row]; p < pointers[row + 1]; p++)
            {
                if (!enabled[indices[p]]) continue;
                int at = pos[row]++;
                newIndices[at] = indices[p];
                newKinds[at] = kinds[p];
            }
        }
        return (newPointers, newIndices, newKinds);
    }

    /// <summary>下游：该符号依赖了谁（出边）。排除 CSharpUsedByDef 反向边，可选按邻居来源过滤。</summary>
    public IReadOnlyList<GraphNeighbor> Uses(int nodeId, string? kindFilter)
    {
        var result = new List<GraphNeighbor>();
        if (nodeId < 0 || nodeId >= NodeCount) return result;

        for (int p = _csrPointers[nodeId]; p < _csrPointers[nodeId + 1]; p++)
        {
            byte kind = _csrKinds[p];
            if (kind == CSharpUsedByDef) continue; // 反向边不作为用途
            int target = _csrCols[p];
            if (PassFilter(target, kindFilter))
                result.Add(Make(target, kind));
        }
        return Sort(result);
    }

    /// <summary>上游：谁依赖了该符号（入边）。可选按邻居来源过滤。</summary>
    public IReadOnlyList<GraphNeighbor> UsedBy(int nodeId, string? kindFilter)
    {
        var result = new List<GraphNeighbor>();
        if (nodeId < 0 || nodeId >= NodeCount) return result;

        for (int p = _cscPointers[nodeId]; p < _cscPointers[nodeId + 1]; p++)
        {
            int source = _cscRows[p];
            if (PassFilter(source, kindFilter))
                result.Add(Make(source, _cscKinds[p]));
        }
        return Sort(result);
    }

    /// <summary>继承树：down=子类（入边的 inherits 边之源），up=基类（出边的 inherits 边之目标）。</summary>
    public IReadOnlyList<GraphNeighbor> Inheritors(int nodeId, bool down)
    {
        var result = new List<GraphNeighbor>();
        if (nodeId < 0 || nodeId >= NodeCount) return result;

        if (down)
        {
            for (int p = _cscPointers[nodeId]; p < _cscPointers[nodeId + 1]; p++)
                if (_cscKinds[p] is Inherits or XmlInherits)
                    result.Add(Make(_cscRows[p], _cscKinds[p]));
        }
        else
        {
            for (int p = _csrPointers[nodeId]; p < _csrPointers[nodeId + 1]; p++)
                if (_csrKinds[p] is Inherits or XmlInherits)
                    result.Add(Make(_csrCols[p], _csrKinds[p]));
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

    private static (int[] Pointers, int[] Indices, byte[] Kinds, int NodeCount) LoadBinary(
        string path, string expectedMagic)
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
        return (pointers, indices, kinds, nodeCount);
    }

    /// <summary>读取全局 PageRank：三列 <c>id \t signature \t score</c>，仅保留启用节点。</summary>
    private static Dictionary<int, double> LoadPageRank(string path, bool[] enabled)
    {
        var map = new Dictionary<int, double>();
        if (!File.Exists(path)) return map;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], out var id) || id < 0 || id >= enabled.Length || !enabled[id])
                continue;
            if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var score))
                map[id] = score;
        }
        return map;
    }
}

/// <summary>一条图邻接结果。</summary>
internal readonly record struct GraphNeighbor(int NodeId, byte EdgeKind, string EdgeKindName, double PageRank);
