namespace RiMCP.Core.Models;

/// <summary>
/// 索引中的单一符号条目，统一承载 C# 符号与 XML Def。
/// <para><c>Id</c> 是贯穿 symbols.tsv / vectors.bin / graph.nodes.tsv 的唯一主键。</para>
/// </summary>
public sealed class SymbolEntry
{
    /// <summary>全局唯一主键（0..N-1），由 IdAllocator 分配。</summary>
    public int Id { get; set; }

    /// <summary>符号具体子类型。</summary>
    public SymbolKind Kind { get; set; }

    /// <summary>来源类型（C# / XML）。</summary>
    public SourceType Source { get; set; }

    /// <summary>展示名（类名 / 方法名 / DefTypeName）。</summary>
    public string ShortName { get; set; } = string.Empty;

    /// <summary>C# 命名空间；XML 为 Def 类别（可空）。</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// 展示键，沿用旧版节点格式：<c>命名空间.类型.kind returnType Name(Args)</c>，须全局唯一。
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// 相对 RimWorldData 根目录的路径，正斜杠、不含 <c>..</c>、不以 <c>/</c> 开头。
    /// </summary>
    public string FileRelPath { get; set; } = string.Empty;

    /// <summary>起始行号（1-based；XML 缺省为 0）。</summary>
    public int LineStart { get; set; }

    /// <summary>结束行号（1-based；XML 缺省为 0）。</summary>
    public int LineEnd { get; set; }

    /// <summary>是否为叶子节点。</summary>
    public bool IsLeaf { get; set; }

    /// <summary>XML 专用：&lt;def Name="..."&gt; 的标签；非 XML 为 <c>null</c>。</summary>
    public string? DefLabel { get; set; }

    /// <summary>父符号的 <c>Signature</c>；根节点为 <c>null</c>。</summary>
    public string? ParentRef { get; set; }

    /// <summary>检索展示片段，最长 MaxPreviewLength 字符。</summary>
    public string Preview { get; set; } = string.Empty;

    /// <summary>
    /// 构建期使用的规范化嵌入正文：XML Def 为去掉缩进/注释、属性排序后的元素文本；
    /// C# 留空时由 <see cref="RiMCP.Core.Indexing.ContentHash"/> 回退到 Preview。该字段不写入 symbols.tsv。
    /// </summary>
    public string? EmbedText { get; set; }

    /// <summary>Preview 字段允许的最大长度（UTF-16 代码单元）。</summary>
    public const int MaxPreviewLength = 200;
}