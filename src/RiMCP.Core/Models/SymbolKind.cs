namespace RiMCP.Core.Models;

/// <summary>
/// C# 符号与 XML Def 的具体子类型。与 <see cref="SourceType"/> 配合使用。
/// </summary>
public enum SymbolKind : byte
{
    /// <summary>C# 类。</summary>
    CsClass = 0,

    /// <summary>C# 方法。</summary>
    CsMethod = 1,

    /// <summary>C# 字段。</summary>
    CsField = 2,

    /// <summary>C# 属性。</summary>
    CsProperty = 3,

    /// <summary>C# 枚举。</summary>
    CsEnum = 4,

    /// <summary>C# 接口。</summary>
    CsInterface = 5,

    /// <summary>C# 结构体。</summary>
    CsStruct = 6,

    /// <summary>XML Def 节点。</summary>
    XmlDef = 7,

    /// <summary>XML Def 字段。</summary>
    XmlDefField = 8,

    /// <summary>C# 事件。</summary>
    CsEvent = 9,

    /// <summary>C# 枚举成员。</summary>
    CsEnumValue = 10,
}