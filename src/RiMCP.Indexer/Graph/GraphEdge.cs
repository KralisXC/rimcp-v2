namespace RiMCP.Indexer.Graph;

/// <summary>图边：源/目标用 int id（= symbols.tsv 行号），kind 为 edgeKinds[] 的编码值。</summary>
internal readonly record struct GraphEdge(int SourceId, int TargetId, byte Kind);

/// <summary>edgeKinds 固定顺序；edgeKinds[i] 即 kind 编码 = i。顺序不可改动（改则须重建图）。</summary>
internal static class EdgeKinds
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "inherits",        // 0  C# 类继承
        "implements",       // 1  C# 接口实现
        "calls",            // 2  方法调用
        "references",       // 3  字段/类型引用
        "xml-inherits",     // 4  XML Def 继承（ParentName）
        "xml-references",   // 5  XML Def 间引用
        "xml-binds-class",  // 6  XML Def → C# 类（class 属性）
        "xml-uses-comp",    // 7  XML Def → C# 组件类
        "csharp-used-by-def", // 8  C# 类 ← XML Def（反向边）
    };

    public const byte Inherits = 0;
    public const byte Implements = 1;
    public const byte Calls = 2;
    public const byte References = 3;
    public const byte XmlInherits = 4;
    public const byte XmlReferences = 5;
    public const byte XmlBindsClass = 6;
    public const byte XmlUsesComp = 7;
    public const byte CSharpUsedByDef = 8;
}