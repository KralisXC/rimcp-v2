using System.ComponentModel;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;
using ModelContextProtocol.Server;
using RiMCP.Core.Models;
using RiMCP.McpServer.Services;

namespace RiMCP.McpServer.Tools;

/// <summary>
/// 检索类工具：语义搜索（Lucene 粗筛 + 语义向量重排）与符号名查找。
/// </summary>
[McpServerToolType]
public static partial class SearchTools
{
    // Lucene 索引字段名（与 Indexer 的 LuceneIndexBuilder 常量对齐，McpServer 不引用 Indexer 故本地声明）
    private const string FId = "id";

    /// <summary>
    /// 搜索 RimWorld 代码与 Def。先用 Lucene 按短名前缀粗筛出候选，
    /// 再用 e5 语义向量余弦重排，返回最相关的前若干条。
    /// 拿到结果后可用 GetRimWorldSource 取源码。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static SearchResultDto SearchRimWorldCode(
        [Description("搜索关键词，英文效果最佳，例如 'door opening speed'、'pawn hunger'")] string query,
        [Description("可选过滤：csharp（只搜 C#）或 def/xml（只搜 XML Def）")] string? kind = null,
        [Description("返回条数上限，默认 5")] int maxResults = 5)
    {
        try
        {
            var index = AppRuntime.Get<IndexService>();
            string q = (query ?? string.Empty).Trim();
            if (q.Length == 0)
                return new SearchResultDto { Count = 0, Results = new List<CompoundResult>() };

            int cap = Math.Clamp(maxResults, 1, 20);
            int candidates = Math.Max(200, cap * 20);

            // 1) Lucene 粗筛：多字段分词查询（signature/shortName/preview），收集候选 id
            var ids = new List<int>();
            var reader = index.NewReader();
            try
            {
                var searcher = new IndexSearcher(reader);
                var analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
                // MultiFieldQueryParser 对多词查询做 OR 合并，任一词命中即进候选
                var qp = new MultiFieldQueryParser(
                    LuceneVersion.LUCENE_48,
                    new[] { "signature", "shortName", "preview" },
                    analyzer);
                qp.DefaultOperator = QueryParserBase.OR_OPERATOR;
                var luceneQuery = qp.Parse(QueryParserBase.Escape(q));
                var top = searcher.Search(luceneQuery, candidates);
                foreach (var sd in top.ScoreDocs)
                {
                    var doc = searcher.Doc(sd.Doc);
                    if (int.TryParse(doc.Get(FId), out var id))
                        ids.Add(id);
                }
            }
            finally
            {
                reader.Dispose();
            }

            // 2) 向量重排：对上一步候选按与查询向量的余弦相似度排序
            var qvec = AppRuntime.Get<QueryEmbedding>().Embed(q);
            var scored = new List<(SymbolEntry Entry, double Score)>();
            foreach (var id in ids)
            {
                var e = index.EntryOf(id);
                if (e is null) continue;
                if (!PassSourceFilter(e, kind)) continue;
                scored.Add((e, Cosine(qvec, index.Vectors.GetVector(id))));
            }
            scored.Sort((a, b) =>
            {
                int tierCmp = KindTier(a.Entry.Kind).CompareTo(KindTier(b.Entry.Kind));
                return tierCmp != 0 ? tierCmp : b.Score.CompareTo(a.Score);
            });

            var results = scored.Take(cap).Select(t => ToCompound(t.Entry, t.Score)).ToList();
            return new SearchResultDto { Count = results.Count, Results = results };
        }
        catch (Exception ex)
        {
            return new SearchResultDto { Count = 0, Results = new List<CompoundResult>(), Error = ex.Message };
        }
    }

    /// <summary>
    /// 按名称在符号索引中反查符号。支持 exact / prefix / fuzzy 三种匹配，返回匹配列表。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static SymbolNameResultDto SearchSymbol(
        [Description("符号名，例如 'Need_Food' 或完整签名的一部分")] string name,
        [Description("匹配模式：exact 精确 / prefix 前缀 / fuzzy 包含（不区分大小写），默认 fuzzy")] string matchMode = "fuzzy",
        [Description("返回条数上限，默认 20")] int maxResults = 20)
    {
        try
        {
            var index = AppRuntime.Get<IndexService>();
            string n = (name ?? string.Empty).Trim();
            if (n.Length == 0)
                return new SymbolNameResultDto { Count = 0, Results = new List<SymbolHitDto>() };

            int cap = Math.Clamp(maxResults, 1, 100);
            var hits = new List<SymbolHitDto>();

            if (matchMode.Equals("exact", StringComparison.OrdinalIgnoreCase))
            {
                var e = index.ResolveSymbol(n);
                if (e is not null) hits.Add(ToHit(e));
            }
            else if (matchMode.Equals("prefix", StringComparison.OrdinalIgnoreCase))
            {
                hits.AddRange(Enumerate(index)
                    .Where(e => e.ShortName.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                    .Take(cap).Select(ToHit));
            }
            else // fuzzy
            {
                hits.AddRange(Enumerate(index)
                    .Where(e => e.ShortName.Contains(n, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Signature.Length)
                    .Take(cap).Select(ToHit));
            }

            return new SymbolNameResultDto { Count = hits.Count, Results = hits };
        }
        catch (Exception ex)
        {
            return new SymbolNameResultDto { Count = 0, Results = new List<SymbolHitDto>(), Error = ex.Message };
        }
    }

    private static IEnumerable<SymbolEntry> Enumerate(IndexService index)
    {
        for (int i = 0; i < index.Symbols.Count; i++)
        {
            var e = index.EntryOf(i);
            if (e is not null) yield return e;
        }
    }

    private static CompoundResult ToCompound(SymbolEntry e, double score) => new()
    {
        Id = e.Id,
        Signature = e.Signature,
        Name = e.ShortName,
        Kind = e.Kind.ToString(),
        Source = SourceName(e),
        File = e.FileRelPath,
        LineStart = e.LineStart,
        Score = Math.Round(score, 4),
    };

    private static SymbolHitDto ToHit(SymbolEntry e) => new()
    {
        Id = e.Id,
        Signature = e.Signature,
        Name = e.ShortName,
        Kind = e.Kind.ToString(),
        Source = SourceName(e),
        File = e.FileRelPath,
        LineStart = e.LineStart,
    };

    private static string SourceName(SymbolEntry e) => e.Source == SourceType.CSharp ? "cs" : "xml";

    private static bool PassSourceFilter(SymbolEntry e, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return true;
        if (kind.Equals("csharp", StringComparison.OrdinalIgnoreCase) || kind.Equals("cs", StringComparison.OrdinalIgnoreCase))
            return e.Source == SourceType.CSharp;
        if (kind.Equals("xml", StringComparison.OrdinalIgnoreCase) || kind.Equals("def", StringComparison.OrdinalIgnoreCase))
            return e.Source == SourceType.Xml;
        return true;
    }

    private static double Cosine(ReadOnlySpan<Half> a, ReadOnlySpan<Half> b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += (double)a[i] * (double)b[i];
        return dot; // 两侧均已 L2 归一化，内积即余弦相似度
    }

    /// <summary>kind 排序层级：数值越小越靠前（顶层定义优先于方法/字段/枚举值）。</summary>
    private static int KindTier(SymbolKind kind) => kind switch
    {
        SymbolKind.CsClass => 0,
        SymbolKind.CsStruct => 1,
        SymbolKind.CsInterface => 2,
        SymbolKind.CsEnum => 3,
        SymbolKind.XmlDef => 4,
        SymbolKind.CsMethod => 5,
        SymbolKind.CsProperty => 6,
        SymbolKind.CsField => 7,
        SymbolKind.CsEnumValue => 8,
        SymbolKind.CsEvent => 9,
        _ => 10,
    };
}

/// <summary>SearchRimWorldCode 的单条结果。</summary>
public sealed record CompoundResult
{
    public int Id { get; set; }
    public string Signature { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int LineStart { get; set; }
    public double Score { get; set; }
}

/// <summary>SearchRimWorldCode 响应体。</summary>
public sealed record SearchResultDto
{
    public int Count { get; set; }
    public List<CompoundResult> Results { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>SearchSymbol 的单条命中。</summary>
public sealed record SymbolHitDto
{
    public int Id { get; set; }
    public string Signature { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int LineStart { get; set; }
}

/// <summary>SearchSymbol 响应体。</summary>
public sealed record SymbolNameResultDto
{
    public int Count { get; set; }
    public List<SymbolHitDto> Results { get; set; } = new();
    public string? Error { get; set; }
}