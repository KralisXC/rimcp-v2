using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Indexer.Config;
using RiMCP.Indexer.Parsing;

namespace RiMCP.Indexer.Building;

/// <summary>
/// 分片解析入口：只负责 XML/C# 解析与本地序号分配（临时 localSeq），
/// 全局 id 由 <see cref="GlobalSymbolTable"/> 统一分配，不在解析阶段落盘。
/// </summary>
internal static class ShardBuilder
{
    /// <summary>解析该分片的 XML（core 额外包含 C#），不做截断。</summary>
    public static List<SymbolEntry> ParseEntries(IndexerConfig cfg, ShardSource shard)
    {
        var entries = new List<SymbolEntry>();
        if (shard.IncludeCSharp)
            entries.AddRange(CSharpParser.ParseDirectory(
                cfg.Root, cfg.CSharpSourceSubdir, cfg.MaxParallelism, cfg.SourceIncludeRoots));
        entries.AddRange(XmlParser.ParseFiles(cfg.Root, shard.XmlFiles, cfg.MaxParallelism));
        return entries;
    }

    /// <summary>解析并应用 --max-entries 截断。</summary>
    public static List<SymbolEntry> CollectEntries(IndexerConfig cfg, ShardSource shard, int? maxEntries)
        => ApplyLimit(ParseEntries(cfg, shard), maxEntries);

    /// <summary>按全局剩余额度截断；返回 (entries, truncated)。</summary>
    public static (List<SymbolEntry> Entries, bool Truncated) Limit(List<SymbolEntry> entries, int? maxEntries)
    {
        if (maxEntries is int mx && mx >= 0 && entries.Count > mx)
            return (entries.Take(mx).ToList(), true);
        return (entries, false);
    }

    private static List<SymbolEntry> ApplyLimit(List<SymbolEntry> entries, int? maxEntries)
        => Limit(entries, maxEntries).Entries;
}
