namespace RiMCP.Core.Models;

/// <summary>
/// 符号来源类型，用于区分 C# 代码符号与 XML Def 定义。
/// </summary>
public enum SourceType : byte
{
    /// <summary>C# 源码中的符号（类/方法/字段等）。</summary>
    CSharp = 0,

    /// <summary>RimWorld 数据目录中的 XML Def 定义。</summary>
    Xml = 1,
}