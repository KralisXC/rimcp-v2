namespace RiMCP.Core.Indexing;

/// <summary>
/// 分片命名与全局 id 约定的唯一来源。分片目录名与分片名一致。
/// </summary>
public static class ShardLayout
{
    /// <summary>核心分片名。</summary>
    public const string Core = "core";

    /// <summary>DLC 分片名前缀。</summary>
    public const string DlcPrefix = "dlc_";

    /// <summary>核心 XML Def 所在的数据目录名。</summary>
    public const string CoreDataDirName = "Core";

    /// <summary>按 Data 下的目录名得到分片名：Core → core，其余 → dlc_&lt;name&gt;。</summary>
    public static string NameForDataDir(string dataDirName)
    {
        if (dataDirName.Equals(CoreDataDirName, StringComparison.OrdinalIgnoreCase))
            return Core;
        return DlcPrefix + dataDirName.Trim().ToLowerInvariant().Replace(' ', '_');
    }
}
