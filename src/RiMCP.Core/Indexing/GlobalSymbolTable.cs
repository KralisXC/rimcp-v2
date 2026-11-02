using RiMCP.Core.Models;

namespace RiMCP.Core.Indexing;

/// <summary>一个分片参与全局合并的符号集合；<c>Entries</c> 的列表下标即临时 localSeq。</summary>
public sealed record ShardSymbolSet(
    string Shard,
    int ShardIndex,
    long BaseId,
    IReadOnlyList<SymbolEntry> Entries);

/// <summary>
/// 全局符号表合并：全局 id 由各分片 BaseId + localSeq 决定（core 在 0..Nc-1，DLC 按 shardIndex 追加）。
/// <para>
/// 同名 XML Def 覆盖：高 shardIndex 的映射项覆盖低序映射项，但不删除低序条目本身。
/// 覆盖对全区生效，因此 core 内部指向被覆盖 Def 的边也会解析到 DLC 覆盖条目。
/// </para>
/// Indexer 构图与 MCP 查询共用本实现，禁止各写一套。
/// </summary>
public sealed class GlobalSymbolTable
{
    private readonly Dictionary<string, EntryRef> _signature;
    private readonly Dictionary<string, EntryRef> _shortName;

    /// <summary>全局符号总数。</summary>
    public int SymbolCount { get; }

    /// <summary>(分片名, 本地序号) → 全局 id。</summary>
    public IReadOnlyDictionary<(string Shard, int LocalSeq), int> SlotToGlobalId { get; }

    /// <summary>全局 id → (分片名, 本地序号)。</summary>
    public IReadOnlyDictionary<int, (string Shard, int LocalSeq)> GlobalIdToSlot { get; }

    private sealed record EntryRef(int GlobalId, int ShardIndex, bool IsXml);

    private GlobalSymbolTable(
        int symbolCount,
        Dictionary<(string Shard, int LocalSeq), int> slots,
        Dictionary<int, (string Shard, int LocalSeq)> reverse)
    {
        SymbolCount = symbolCount;
        _signature = new Dictionary<string, EntryRef>(StringComparer.Ordinal);
        _shortName = new Dictionary<string, EntryRef>(StringComparer.Ordinal);
        SlotToGlobalId = slots;
        GlobalIdToSlot = reverse;
    }

    /// <summary>合并分片符号集，产出全局 id 映射与同名覆盖解析表。</summary>
    public static GlobalSymbolTable Build(IReadOnlyList<ShardSymbolSet> shards)
    {
        int total = 0;
        foreach (var s in shards) total = checked(total + s.Entries.Count);

        var slots = new Dictionary<(string, int), int>(total);
        var reverse = new Dictionary<int, (string, int)>(total);
        var table = new GlobalSymbolTable(total, slots, reverse);

        foreach (var shard in shards.OrderBy(s => s.ShardIndex))
        {
            for (int local = 0; local < shard.Entries.Count; local++)
            {
                var entry = shard.Entries[local];
                int globalId = checked((int)(shard.BaseId + local));
                slots[(shard.Shard, local)] = globalId;
                reverse[globalId] = (shard.Shard, local);

                bool isXml = entry.Source == SourceType.Xml;
                Put(table._signature, entry.Signature, globalId, shard.ShardIndex, isXml);
                Put(table._shortName, entry.ShortName, globalId, shard.ShardIndex, isXml);
            }
        }
        return table;
    }

    private static void Put(
        Dictionary<string, EntryRef> map, string key, int globalId, int shardIndex, bool isXml)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var existing))
        {
            map[key] = new EntryRef(globalId, shardIndex, isXml);
            return;
        }
        // 同名 XML（Def 及其字段）由高 shardIndex 覆盖；C# 或低序不覆盖。
        if (isXml && existing.IsXml && shardIndex > existing.ShardIndex)
            map[key] = new EntryRef(globalId, shardIndex, isXml);
    }

    /// <summary>按签名解析全局 id（应用同名 XML 覆盖规则）；未命中返回 -1。</summary>
    public int ResolveSignature(string signature)
        => _signature.TryGetValue(signature, out var e) ? e.GlobalId : -1;

    /// <summary>按短名解析全局 id（应用同名 XML 覆盖规则）；未命中返回 -1。</summary>
    public int ResolveShortName(string shortName)
        => _shortName.TryGetValue(shortName, out var e) ? e.GlobalId : -1;

    /// <summary>用 (分片名, 本地序号) 定位全局 id；未命中返回 -1。</summary>
    public int ResolveSlot(string shard, int localSeq)
        => SlotToGlobalId.TryGetValue((shard, localSeq), out var id) ? id : -1;
}
