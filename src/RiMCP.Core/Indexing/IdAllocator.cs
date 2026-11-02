using RiMCP.Core.Models;

namespace RiMCP.Core.Indexing;

/// <summary>
/// 在写出任何产出物之前集中分配全局 id（0..N-1），
/// 保证 symbols.tsv / vectors.bin / graph.nodes.tsv 三者行号与 id 一致。
/// </summary>
public static class IdAllocator
{
    /// <summary>
    /// 按 (Source, Kind, Namespace, Signature) 字典序排序输入，并按排序序号
    /// 就地写入各条目的 <see cref="SymbolEntry.Id"/>，返回排序后的同一列表。
    /// </summary>
    public static List<SymbolEntry> Assign(List<SymbolEntry> entries)
    {
        entries.Sort((a, b) =>
        {
            int c = a.Source.CompareTo(b.Source);
            if (c != 0) return c;
            c = a.Kind.CompareTo(b.Kind);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Namespace, b.Namespace);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Signature, b.Signature);
        });
        for (int i = 0; i < entries.Count; i++)
            entries[i].Id = i;
        return entries;
    }
}