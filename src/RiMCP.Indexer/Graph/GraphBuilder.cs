// Portions of this file are based on RiMCP_hybrid
// Original: https://github.com/h7lu/RiMCP_hybrid
// Licensed under MIT License
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Indexer.Config;
using RiMCP.Indexer.Parsing;
using System.Xml.Linq;

namespace RiMCP.Indexer.Graph;

/// <summary>
/// P6：从已有 symbols.tsv 生成图（nodes/csr/csc/pagerank/meta）。
/// 节点 = int id（symbols.tsv 行号）。C# 边用 Roslyn 语法分析 + (文件,起始行) 回填源符号 id。
/// </summary>
internal sealed class GraphBuilder
{
    private const string CsrMagic = "CSR1";
    private const string CscMagic = "CSC1";
    private const int FormatVersion = 1;

    private readonly IndexerConfig _cfg;
    private readonly string _shardDir;
    private readonly bool _includeCSharp;
    private readonly string _base; // &lt;shardDir&gt;/graph（各产物 = 该基名 + 扩展名）

    public GraphBuilder(IndexerConfig cfg, string shardDir, bool includeCSharp = true)
    {
        _cfg = cfg;
        _shardDir = shardDir;
        _includeCSharp = includeCSharp;
        _base = Path.Combine(shardDir, "graph");
    }

    private static readonly HashSet<SymbolKind> TypeKinds = new()
    { SymbolKind.CsClass, SymbolKind.CsStruct, SymbolKind.CsInterface, SymbolKind.CsEnum };
    private static readonly HashSet<SymbolKind> MemberKinds = new()
    { SymbolKind.CsMethod, SymbolKind.CsProperty, SymbolKind.CsField, SymbolKind.CsEvent };

    /// <summary>运行图构建，返回有效边数。</summary>
    public long Build()
    {
        var index = SymbolIndex.LoadFromTsv(Path.Combine(_shardDir, "symbols.tsv"));
        Console.WriteLine($"[P6] 载入 {index.Count} 条符号，开始构图…");

        var edges = new ConcurrentBag<GraphEdge>();
        if (_includeCSharp)
            CollectCSharpEdges(index, edges);
        CollectXmlEdges(index, edges);

        // 反向边：C# 类 ← XML Def
        foreach (var e in edges)
            if (e.Kind == EdgeKinds.XmlBindsClass || e.Kind == EdgeKinds.XmlUsesComp)
                edges.Add(new GraphEdge(e.TargetId, e.SourceId, EdgeKinds.CSharpUsedByDef));

        var all = edges.ToArray();
        Write(index, all);
        return all.LongLength;
    }

    #region C# 边
    private void CollectCSharpEdges(SymbolIndex index, ConcurrentBag<GraphEdge> edges)
    {
        var typeByShort = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var memberByShort = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var typeBySig = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLine = BuildLineIndex(index);

        for (int i = 0; i < index.Count; i++)
            if (index.GetById(i) is { } e && e.Source == SourceType.CSharp)
            {
                if (TypeKinds.Contains(e.Kind)) { Add(typeByShort, e.ShortName, i); typeBySig[e.Signature] = i; }
                else if (MemberKinds.Contains(e.Kind)) Add(memberByShort, e.ShortName, i);
            }

        string srcDir = Path.Combine(_cfg.Root, _cfg.CSharpSourceSubdir);
        var files = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(IsWhitelisted).ToArray();
        Console.WriteLine($"[P6] 扫描 {files.Length} 个 C# 文件…");

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = _cfg.MaxParallelism }, file =>
        {
            try { CollectEdgesInFile(file, index, byLine, typeByShort, memberByShort, typeBySig, edges); }
            catch (Exception ex) { Console.Error.WriteLine($"[P6] {file}: {ex.Message}"); }
        });
    }

    private bool IsWhitelisted(string file)
    {
        string rel = RelativePathUtil.ToRel(_cfg.Root, file);
        string body = rel.Replace('\\', '/');
        if (body.StartsWith(_cfg.CSharpSourceSubdir + "/", StringComparison.Ordinal))
            body = body[(_cfg.CSharpSourceSubdir.Length + 1)..];
        int slash = body.IndexOf('/');
        if (slash < 0) return true; // 源码根目录零散文件保留
        return _cfg.SourceIncludeRoots.Contains(body[..slash], StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<(string, int), int> BuildLineIndex(SymbolIndex index)
    {
        var map = new Dictionary<(string, int), int>();
        for (int i = 0; i < index.Count; i++)
            if (index.GetById(i) is { } e && e.Source == SourceType.CSharp && e.LineStart > 0)
                map[(e.FileRelPath ?? "", e.LineStart)] = i;
        return map;
    }

    private void CollectEdgesInFile(string file, SymbolIndex index,
        Dictionary<(string, int), int> byLine,
        Dictionary<string, List<int>> typeByShort, Dictionary<string, List<int>> memberByShort,
        Dictionary<string, int> typeBySig, ConcurrentBag<GraphEdge> edges)
    {
        var rel = RelativePathUtil.ToRel(_cfg.Root, file);
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var root = tree.GetRoot();

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                     .Where(t => t.Parent is not TypeDeclarationSyntax)) // 跳过嵌套类型，避免重复
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

    private static int LineId(Dictionary<(string, int), int> byLine, string rel, Microsoft.CodeAnalysis.SyntaxNode node)
    {
        var pos = node.SyntaxTree.GetLineSpan(node.Span); // 用 SyntaxTree.GetLineSpan 保证非零
        return byLine.TryGetValue((rel, pos.StartLinePosition.Line + 1), out var id) ? id : -1;
    }

    private static IEnumerable<int> ResolveType(string name, Dictionary<string, List<int>> byShort, Dictionary<string, int> bySig)
    {
        var clean = name.Split('<')[0];
        // 完全限定名 signature 精确命中即返回，不做短名二义判断（真实限定名边不应被误杀）
        if (bySig.TryGetValue(clean, out var exact)) { yield return exact; yield break; }

        int dot = clean.LastIndexOf('.');
        var shortName = dot >= 0 ? clean[(dot + 1)..] : clean;
        // 短名二义候选 > 2 => 忽略
        if (byShort.TryGetValue(shortName, out var hits) && hits.Count <= 2)
            foreach (var h in hits) yield return h;
    }

    private static IEnumerable<int> ResolveMember(string name, Dictionary<string, List<int>> byShort)
    {
        var clean = name.Split('<')[0];
        int dot = clean.LastIndexOf('.');
        var shortName = dot >= 0 ? clean[(dot + 1)..] : clean;
        // 短名二义候选 > 2 => 忽略（跨类同名是假边）
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
    private void CollectXmlEdges(SymbolIndex index, ConcurrentBag<GraphEdge> edges)
    {
        var xmlById = new Dictionary<int, SymbolEntry>();
        var xmlBySig = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < index.Count; i++)
            if (index.GetById(i) is { } e && e.Source == SourceType.Xml && e.Kind == SymbolKind.XmlDef)
            { xmlById[i] = e; xmlBySig[e.Signature] = i; }

        // xml-inherits：直接由 ParentRef（父 Def 签名）解析，无需读文件
        foreach (var (defId, e) in xmlById)
            if (!string.IsNullOrEmpty(e.ParentRef) && xmlBySig.TryGetValue(e.ParentRef, out var pid))
                edges.Add(new GraphEdge(defId, pid, EdgeKinds.XmlInherits));

        // xml-binds-class：按文件分组解析 Class 属性 → C# 类型
        var byFile = xmlById.Values.GroupBy(e => e.FileRelPath ?? "", StringComparer.Ordinal);
        Parallel.ForEach(byFile, new ParallelOptions { MaxDegreeOfParallelism = _cfg.MaxParallelism }, grp =>
        {
            var path = Path.Combine(_cfg.Root, grp.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return;
            XDocument doc;
            try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
            catch { return; }

            var typeKinds = TypeKinds;
            foreach (var e in grp)
            {
                var defNode = FindDefWithName(doc, e.ShortName);
                if (defNode == null) continue;
                var classAttr = defNode.Attribute("Class")?.Value ?? string.Empty;
                string target = SplitLineFirst(classAttr);
                int dot = target.LastIndexOf('.');
                var shortName = dot >= 0 ? target[(dot + 1)..] : target;
                for (int i = 0; i < index.Count; i++)
                    if (index.GetById(i) is { } cs && cs.Source == SourceType.CSharp && typeKinds.Contains(cs.Kind) &&
                        (cs.ShortName.Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
                         cs.Signature.Equals(target, StringComparison.Ordinal)))
                        edges.Add(new GraphEdge(e.Id, i, EdgeKinds.XmlBindsClass));
            }
        });
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
    private void Write(SymbolIndex index, IReadOnlyList<GraphEdge> edges)
    {
        var nodeCount = index.Count;
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

        WriteBinary(_base + ".csr.bin", CsrMagic, nodeCount, w, csrPointers, csrCols, csrKinds);
        WriteBinary(_base + ".csc.bin", CscMagic, nodeCount, w, cscPointers, cscRows, cscKinds);
        WriteNodes(index);
        WriteMeta(nodeCount, w);
        WritePagerank(index, nodeCount, csrPointers, csrCols);
    }

    private static int[] BuildPointers(int[] counts)
    {
        var p = new int[counts.Length + 1];
        for (int i = 0; i < counts.Length; i++) p[i + 1] = p[i] + counts[i];
        return p;
    }

    private void WriteBinary(string path, string magic, int nodeCount, int edgeCount, int[] pointers, int[] indices, byte[] kinds)
    {
        using var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20), Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(magic)); w.Write(FormatVersion);
        w.Write(nodeCount); w.Write(edgeCount);
        foreach (var p in pointers) w.Write(p);
        foreach (var i in indices) w.Write(i);
        w.Write(kinds.Length); w.Write(kinds);
    }

    private void WriteNodes(SymbolIndex index)
    {
        using var w = new StreamWriter(_base + ".nodes.tsv", false, new UTF8Encoding(false));
        for (int i = 0; i < index.Count; i++)
            w.Write(i.ToString(CultureInfo.InvariantCulture) + "\t" + (index.GetById(i)?.Signature ?? "") + "\n");
    }

    private void WriteMeta(int nodeCount, int edgeCount)
    {
        var kinds = "[\"" + string.Join("\", \"", EdgeKinds.Names) + "\"]";
        File.WriteAllText(_base + ".meta.json",
            $"{{\"nodeCount\":{nodeCount},\"edgeCount\":{edgeCount},\"edgeKinds\":{kinds}}}");
    }

    private void WritePagerank(SymbolIndex index, int nodeCount, int[] csrPointers, int[] csrCols)
    {
        var scores = PageRank.Calculate(nodeCount, csrPointers, csrCols);
        using var w = new StreamWriter(_base + ".pagerank.tsv", false, new UTF8Encoding(false));
        var ranked = Enumerable.Range(0, nodeCount)
            .OrderByDescending(i => scores[i])
            .Select(i => $"{index.GetById(i)?.Signature ?? ""}\t{scores[i]:F6}");
        foreach (var line in ranked) w.WriteLine(line);
    }
    #endregion
}