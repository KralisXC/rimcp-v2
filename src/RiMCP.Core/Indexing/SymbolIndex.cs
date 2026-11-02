using RiMCP.Core.Models;
using RiMCP.Core.Storage;

namespace RiMCP.Core.Indexing;

/// <summary>
/// 启动时全量加载 symbols.tsv 构建的内存索引：主存储 + 正/反查字典。
/// </summary>
public sealed class SymbolIndex
{
    private readonly Dictionary<int, SymbolEntry> _byId;
    private readonly Dictionary<string, List<int>> _bySignature;
    private readonly Dictionary<string, List<int>> _byShortName;

    /// <summary>从条目列表构建索引。</summary>
    public SymbolIndex(IReadOnlyList<SymbolEntry> entries)
    {
        _byId = new Dictionary<int, SymbolEntry>(entries.Count);
        _bySignature = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        _byShortName = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var e in entries)
        {
            _byId[e.Id] = e;
            AddOrDefault(e.Signature, e.Id, _bySignature);
            AddOrDefault(e.ShortName, e.Id, _byShortName);
        }
    }

    /// <summary>直接以 symbols.tsv 文件路径载入并构建索引。</summary>
    public static SymbolIndex LoadFromTsv(string tsvPath)
        => new(SymbolsTsv.Load(tsvPath));

    /// <summary>索引条目总数。</summary>
    public int Count => _byId.Count;

    private static void AddOrDefault(string key, int id, Dictionary<string, List<int>> map)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>();
            map[key] = list;
        }
        list.Add(id);
    }

    /// <summary>按 id 取条目；不存在返回 <c>null</c>。</summary>
    public SymbolEntry? GetById(int id)
        => _byId.TryGetValue(id, out var e) ? e : null;

    /// <summary>按签名查 id 列表（重复签名亦聚合于此）；未命中返回 <c>null</c>。</summary>
    public IReadOnlyList<int>? GetBySignature(string signature)
        => _bySignature.TryGetValue(signature, out var list) ? list : null;

    /// <summary>按短名查 id 列表；未命中返回 <c>null</c>。</summary>
    public IReadOnlyList<int>? GetByShortName(string name)
        => _byShortName.TryGetValue(name, out var list) ? list : null;
}