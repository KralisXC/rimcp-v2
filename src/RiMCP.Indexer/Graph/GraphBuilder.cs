// Portions of this file are based on RiMCP_hybrid
// Original: https://github.com/h7lu/RiMCP_hybrid
// Licensed under MIT License
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Indexer.Config;
using RiMCP.Indexer.Parsing;

namespace RiMCP.Indexer.Graph;

/// <summary>构图输入：一个分片的本地符号（列表下标即 localSeq）。</summary>
internal sealed record ShardGraphInput(
    string Name,
    int Index,
    long BaseId,
    IReadOnlyList<SymbolEntry> Entries);

/// <summary>统一构图结果（节点/边数）。</summary>
internal sealed record GraphBuildResult(long NodeCount, long EdgeCount);

/// <summary>
/// 阶段二：全局统一构图。节点 = 全部符号（全局 id 连续 0..N-1），边端点经
/// <see cref="GlobalSymbolTable"/> 解析，允许跨分片（DLC Def 继承 core Def、DLC Def 绑定 core C# 类等）。
/// 产物写入 index/graph/，分片目录不再保留 graph.*。
/// </summary>
internal sealed class GraphBuilder
{
    private const string CsrMagic = "CSR1";
    private const string CscMagic = "CSC1";
    private const int FormatVersion = 1;

    private readonly IndexerConfig _cfg;
    private readonly string _graphDir;
    private readonly IReadOnlyList<ShardGraphInput> _shards;
    private readonly GlobalSymbolTable _table;
    private readonly bool _includeCSharp;

    public GraphBuilder(
        IndexerConfig cfg, string graphDir,
        IReadOnlyList<ShardGraphInput> shards, GlobalSymbolTable table, bool includeCSharp)
    {
        _cfg = cfg;
        _graphDir = graphDir;
        _shards = shards;
        _table = table;
        _includeCSharp = includeCSharp;
    }

    private static readonly HashSet<SymbolKind> TypeKinds = new()
    { SymbolKind.CsClass, SymbolKind.CsStruct, SymbolKind.CsInterface, SymbolKind.CsEnum };
    private static readonly HashSet<SymbolKind> MemberKinds = new()
    { SymbolKind.CsMethod, SymbolKind.CsProperty, SymbolKind.CsField, SymbolKind.CsEvent };

    private sealed record Node(int Id, string Shard, SymbolEntry Entry);

    /// <summary>运行统一构图并写盘，返回节点/边数。</summary>
    public GraphBuildResult Build()
    {
        var nodes = BuildNodes();
        Console.WriteLine($"[graph] 全局节点 {nodes.Count}，跨分片构图…");

        var edges = new ConcurrentBag<GraphEdge>();
        if (_includeCSharp)
            CollectCSharpEdges(nodes, edges);
        CollectXmlEdges(nodes, edges);

        // 反向边：C# 类 ← XML Def
        foreach (var e in edges.ToArray())
        {
            if (e.Kind == EdgeKinds.XmlBindsClass || e.Kind == EdgeKinds.XmlUsesComp)
                edges.Add(new GraphEdge(e.TargetId, e.SourceId, EdgeKinds.CSharpUsedByDef));
        }

        var all = edges.ToArray();
        Write(nodes, all);
        return new GraphBuildResult(nodes.Count, all.LongLength);
    }

    private List<Node> BuildNodes()
    {
        int total = 0;
        foreach (var shard in _shards) total = checked(total + shard.Entries.Count);
        var nodes = new List<Node>(total);
        for (int id = 0; id < total; id++) nodes.Add(null!);

        foreach (var shard in _shards)
        {
            for (int local = 0; local < shard.Entries.Count; local++)
            {
                int globalId = checked((int)(shard.BaseId + local));
                if (globalId < 0 || globalId >= total)
                    throw new InvalidDataException($"全局 id {globalId} 越界（总分片符号数 {total}）。");
                nodes[globalId] = new Node(globalId, shard.Name, shard.Entries[local]);
            }
        }

        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i] is null)
                throw new InvalidDataException($"全局节点 {i} 未分配符号，分片 BaseId 不连续。");
        return nodes;
    }

    #region C# 边
    private void CollectCSharpEdges(List<Node> nodes, ConcurrentBag<GraphEdge> edges)
    {
        var typeByShort = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var memberByShort = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var typeBySig = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLine = BuildLineIndex(nodes);

        foreach (var node in nodes)
        {
            var e = node.Entry;
            if (e.Source != SourceType.CSharp) continue;
            if (TypeKinds.Contains(e.Kind))
            {
                Add(typeByShort, e.ShortName, node.Id);
                typeBySig[e.Signature] = node.Id;
            }
            else if (MemberKinds.Contains(e.Kind))
            {
                Add(memberByShort, e.ShortName, node.Id);
            }
        }

        string srcDir = Path.Combine(_cfg.Root, _cfg.CSharpSourceSubdir);
        if (!Directory.Exists(srcDir))
        {
            Console.Error.WriteLine($"[graph] C# 源码目录不存在，跳过 C# 边：{srcDir}");
            return;
        }
        var files = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(IsWhitelisted).ToArray();
        Console.WriteLine($"[graph] 扫描 {files.Length} 个 C# 文件…");

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = _cfg.MaxParallelism }, file =>
        {
            try { CollectEdgesInFile(file, byLine, typeByShort, memberByShort, typeBySig, edges); }
            catch (Exception ex) { Console.Error.WriteLine($"[graph] {file}: {ex.Message}"); }
        });
    }

    private bool IsWhitelisted(string file)
    {
        string rel = RelativePathUtil.ToRel(_cfg.Root, file);
        string body = rel.Replace('\\', '/');
        if (body.StartsWith(_cfg.CSharpSourceSubdir + "/", StringComparison.Ordinal))
            body = body[(_cfg.CSharpSourceSubdir.Length + 1)..];
        int slash = body.IndexOf('/');
        if (slash < 0) return true;
        return _cfg.SourceIncludeRoots.Contains(body[..slash], StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<(string, int), int> BuildLineIndex(List<Node> nodes)
    {
        var map = new Dictionary<(string, int), int>();
        foreach (var node in nodes)
        {
            var e = node.Entry;
            if (e.Source == SourceType.CSharp && e.LineStart > 0)
                map[(e.FileRelPath ?? "", e.LineStart)] = node.Id;
        }
        return map;
    }

    private void CollectEdgesInFile(string file,
        Dictionary<(string, int), int> byLine,
        Dictionary<string, List<int>> typeByShort, Dictionary<string, List<int>> memberByShort,
        Dictionary<string, int> typeBySig, ConcurrentBag<GraphEdge> edges)
    {
        var rel = RelativePathUtil.ToRel(_cfg.Root, file);
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var root = tree.GetRoot();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                     .Where(t => t.Parent is not TypeDeclarationSyntax))
        {
            var srcId = LineId(byLine, rel, type);
            if (srcId < 0) continue;

            if (type.BaseList is { } bl)
            {
                byte kind = type is InterfaceDeclarationSyntax ? EdgeKinds.Implements : EdgeKinds.Inherits;
                foreach (var bt in bl.Types)
                    foreach (var t in ResolveType(bt.ToString(), typeByShort, typeBySig))
                        edges.Add(new GraphEdge(srcId, t, kind));
            }

            foreach (var member in type.DescendantNodes())
            {
                int mid = member switch
                {
                    MethodDeclarationSyntax m => LineId(byLine, rel, m),
                    PropertyDeclarationSyntax p => LineId(byLine, rel, p),
                    FieldDeclarationSyntax f => LineId(byLine, rel, f),
                    EventDeclarationSyntax ev => LineId(byLine, rel, ev),
                    _ => -1,
                };
                if (mid < 0) continue;

                foreach (var inv in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    foreach (var t in ResolveMember(inv.Expression.ToString(), memberByShort))
                        edges.Add(new GraphEdge(mid, t, EdgeKinds.Calls));

                foreach (var mc in member.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (mc.Parent is InvocationExpressionSyntax) continue;
                    foreach (var t in ResolveMember(mc.ToString(), memberByShort))
                        edges.Add(new GraphEdge(mid, t, EdgeKinds.References));
                }

                foreach (var oc in member.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                    foreach (var t in ResolveType(oc.Type.ToString(), typeByShort, typeBySig))
                        edges.Add(new GraphEdge(mid, t, EdgeKinds.References));
            }
        }
    }

    private static int LineId(Dictionary<(string, int), int> byLine, string rel,
        Microsoft.CodeAnalysis.SyntaxNode node)
    {
        var pos = node.SyntaxTree.GetLineSpan(node.Span);
        return byLine.TryGetValue((rel, pos.StartLinePosition.Line + 1), out var id) ? id : -1;
    }

    private static IEnumerable<int> ResolveType(string name,
        Dictionary<string, List<int>> byShort, Dictionary<string, int> bySig)
    {
        var clean = name.Split('<')[0];
        if (bySig.TryGetValue(clean, out var exact)) { yield return exact; yield break; }

        int dot = clean.LastIndexOf('.');
        var shortName = dot >= 0 ? clean[(dot + 1)..] : clean;
        if (byShort.TryGetValue(shortName, out var hits) && hits.Count <= 2)
            foreach (var h in hits) yield return h;
    }

    private static IEnumerable<int> ResolveMember(string name, Dictionary<string, List<int>> byShort)
    {
        var clean = name.Split('<')[0];
        int dot = clean.LastIndexOf('.');
        var shortName = dot >= 0 ? clean[(dot + 1)..] : clean;
        if (byShort.TryGetValue(shortName, out var hits) && hits.Count <= 2)
            foreach (var h in hits) yield return h;
    }

    private static void Add(Dictionary<string, List<int>> map, string key, int id)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
        list.Add(id);
    }
    #endregion

    #region XML 边
    private void CollectXmlEdges(List<Node> nodes, ConcurrentBag<GraphEdge> edges)
    {
        var xmlDefs = nodes.Where(n => n.Entry.Source == SourceType.Xml &&
                                        n.Entry.Kind == SymbolKind.XmlDef).ToList();

        // xml-inherits：ParentRef 经全局覆盖表解析（core 指向被覆盖 Def 的边落到 DLC 覆盖条目）
        foreach (var node in xmlDefs)
        {
            string? parent = node.Entry.ParentRef;
            if (string.IsNullOrEmpty(parent)) continue;
            int pid = _table.ResolveSignature(parent);
            if (pid >= 0) edges.Add(new GraphEdge(node.Id, pid, EdgeKinds.XmlInherits));
        }

        // C# 类型解析表（XML Def 的 Class 属性 → 全局 C# 类型）
        var csTypesByShort = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var csTypesBySig = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var e = node.Entry;
            if (e.Source != SourceType.CSharp || !TypeKinds.Contains(e.Kind)) continue;
            Add(csTypesByShort, e.ShortName, node.Id);
            csTypesBySig[e.Signature] = node.Id;
        }

        // xml-binds-class：按 (分片, 文件) 分组读取 Class 属性
        var byFile = xmlDefs.GroupBy(n => (n.Shard, n.Entry.FileRelPath ?? ""));
        Parallel.ForEach(byFile, new ParallelOptions { MaxDegreeOfParallelism = _cfg.MaxParallelism }, grp =>
        {
            var (_, rel) = grp.Key;
            if (rel.Length == 0) return;
            var path = Path.Combine(_cfg.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return;
            XDocument doc;
            try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
            catch { return; }

            foreach (var node in grp)
            {
                var defNode = FindDefWithName(doc, node.Entry.ShortName);
                if (defNode == null) continue;
                var classAttr = defNode.Attribute("Class")?.Value ?? string.Empty;
                foreach (var target in ResolveCsType(classAttr, csTypesByShort, csTypesBySig))
                    edges.Add(new GraphEdge(node.Id, target, EdgeKinds.XmlBindsClass));
            }
        });
    }

    private static IEnumerable<int> ResolveCsType(
        string classAttr, Dictionary<string, List<int>> byShort, Dictionary<string, int> bySig)
    {
        string target = SplitLineFirst(classAttr.Trim());
        if (target.Length == 0) yield break;
        if (bySig.TryGetValue(target, out var exact)) { yield return exact; yield break; }

        int dot = target.LastIndexOf('.');
        var shortName = dot >= 0 ? target[(dot + 1)..] : target;
        if (byShort.TryGetValue(shortName, out var hits) && hits.Count <= 2)
            foreach (var h in hits) yield return h;
    }

    private static string SplitLineFirst(string s)
        => s.IndexOfAny(new[] { '|', '<' }) is var k && k >= 0 ? s[..k] : s;

    /// <summary>在同文件中定位与 ShortName（defName）匹配的 Def 元素。</summary>
    private static XElement? FindDefWithName(XDocument doc, string defName)
    {
        foreach (var el in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var name = el.Element("defName")?.Value ?? el.Element("DefName")?.Value;
            if (name != null && name.Equals(defName, StringComparison.OrdinalIgnoreCase)) return el;
        }
        return null;
    }
    #endregion

    #region 写盘
    private void Write(List<Node> nodes, IReadOnlyList<GraphEdge> edges)
    {
        Directory.CreateDirectory(_graphDir);
        var nodeCount = nodes.Count;
        var rowCounts = new int[nodeCount];
        var colCounts = new int[nodeCount];
        int valid = 0;
        foreach (var e in edges)
        {
            if (e.SourceId < 0 || e.SourceId >= nodeCount || e.TargetId < 0 || e.TargetId >= nodeCount) continue;
            rowCounts[e.SourceId]++; colCounts[e.TargetId]++; valid++;
        }

        var csrPointers = BuildPointers(rowCounts);
        var cscPointers = BuildPointers(colCounts);
        var csrCols = new int[valid]; var cscRows = new int[valid];
        var csrKinds = new byte[valid]; var cscKinds = new byte[valid];
        var rowPos = (int[])csrPointers.Clone(); var colPos = (int[])cscPointers.Clone();
        int w = 0;
        foreach (var e in edges)
        {
            if (e.SourceId < 0 || e.SourceId >= nodeCount || e.TargetId < 0 || e.TargetId >= nodeCount) continue;
            csrCols[rowPos[e.SourceId]] = e.TargetId; csrKinds[rowPos[e.SourceId]] = e.Kind; rowPos[e.SourceId]++;
            cscRows[colPos[e.TargetId]] = e.SourceId; cscKinds[colPos[e.TargetId]] = e.Kind; colPos[e.TargetId]++;
            w++;
        }

        WriteBinary(Path.Combine(_graphDir, "graph.csr.bin"), CsrMagic, nodeCount, w, csrPointers, csrCols, csrKinds);
        WriteBinary(Path.Combine(_graphDir, "graph.csc.bin"), CscMagic, nodeCount, w, cscPointers, cscRows, cscKinds);
        WriteNodes(nodes);
        WriteMeta(nodeCount, w);
        WritePagerank(nodes, nodeCount, csrPointers, csrCols);
    }

    private static int[] BuildPointers(int[] counts)
    {
        var p = new int[counts.Length + 1];
        for (int i = 0; i < counts.Length; i++) p[i + 1] = p[i] + counts[i];
        return p;
    }

    private static void WriteBinary(string path, string magic, int nodeCount, int edgeCount,
        int[] pointers, int[] indices, byte[] kinds)
    {
        using var w = new BinaryWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20), Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(magic)); w.Write(FormatVersion);
        w.Write(nodeCount); w.Write(edgeCount);
        foreach (var p in pointers) w.Write(p);
        foreach (var i in indices) w.Write(i);
        w.Write(kinds.Length); w.Write(kinds);
    }

    private void WriteNodes(List<Node> nodes)
    {
        using var w = new StreamWriter(
            Path.Combine(_graphDir, "graph.nodes.tsv"), false, new UTF8Encoding(false));
        foreach (var node in nodes)
            w.Write(node.Id.ToString(CultureInfo.InvariantCulture) + "\t"
                    + (node.Entry?.Signature ?? "") + "\t" + node.Shard + "\n");
    }

    private void WriteMeta(int nodeCount, int edgeCount)
    {
        var kinds = "[\"" + string.Join("\", \"", EdgeKinds.Names) + "\"]";
        File.WriteAllText(Path.Combine(_graphDir, "graph.meta.json"),
            $"{{\"nodeCount\":{nodeCount},\"edgeCount\":{edgeCount},\"edgeKinds\":{kinds}," +
            "\"crossShard\":true,\"formatVersion\":1}");
    }

    private void WritePagerank(List<Node> nodes, int nodeCount, int[] csrPointers, int[] csrCols)
    {
        var scores = PageRank.Calculate(nodeCount, csrPointers, csrCols);
        using var w = new StreamWriter(
            Path.Combine(_graphDir, "graph.pagerank.tsv"), false, new UTF8Encoding(false));
        var ranked = Enumerable.Range(0, nodeCount)
            .OrderByDescending(i => scores[i])
            .Select(i => $"{i}\t{nodes[i].Entry?.Signature ?? ""}\t{scores[i]:F6}");
        foreach (var line in ranked) w.WriteLine(line);
    }
    #endregion
}
