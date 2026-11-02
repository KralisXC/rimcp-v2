using System.Text;

namespace RiMCP.Core.Storage;

/// <summary>
/// 嵌入内容哈希映射表（index/embedding-cache.tsv）。
/// <para>结构：<c>contentHash \t shardName \t localId</c>。禁止使用 globalId 作 key。</para>
/// <para>
/// 每个 contentHash 只需要一条旧记录即可定位旧 vectors.bin 中的向量；
/// 重复文本取首次出现的记录。构建完成后整体重写，只保留本次仍存在的 hash。
/// </para>
/// </summary>
public sealed class EmbeddingCache
{
    /// <summary>映射表文件名（位于 index 根目录）。</summary>
    public const string FileName = "embedding-cache.tsv";

    /// <summary>一行映射：内容哈希 → 旧分片 + 旧本地序号。</summary>
    public sealed record Row(string ContentHash, string Shard, int LocalId);

    private readonly Dictionary<string, Row> _byHash;
    private readonly Dictionary<(string Shard, int LocalId), string> _bySlot;

    /// <summary>文件在加载时是否存在。</summary>
    public bool Exists { get; }

    /// <summary>按文件行顺序保存的原始行（重写时用于保留未变分片）。</summary>
    public IReadOnlyList<Row> Rows { get; }

    private EmbeddingCache(IReadOnlyList<Row> rows, bool exists)
    {
        Rows = rows;
        Exists = exists;
        _byHash = new Dictionary<string, Row>(rows.Count, StringComparer.Ordinal);
        _bySlot = new Dictionary<(string, int), string>(rows.Count);
        foreach (var row in rows)
        {
            _byHash.TryAdd(row.ContentHash, row);
            _bySlot.TryAdd((row.Shard, row.LocalId), row.ContentHash);
        }
    }

    /// <summary>读取映射表；文件不存在返回空表（<see cref="Exists"/>=false）。</summary>
    public static EmbeddingCache Load(string path)
    {
        if (!File.Exists(path))
            return new EmbeddingCache(Array.Empty<Row>(), exists: false);

        var rows = new List<Row>();
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            var parts = raw.Split('\t');
            if (parts.Length < 3) continue;
            string hash = parts[0].Trim();
            string shard = parts[1].Trim();
            if (hash.Length == 0 || shard.Length == 0) continue;
            if (!int.TryParse(parts[2].Trim(), out var local) || local < 0) continue;
            rows.Add(new Row(hash, shard, local));
        }
        return new EmbeddingCache(rows, exists: true);
    }

    /// <summary>按内容哈希定位旧记录；未命中返回 false。</summary>
    public bool TryGet(string contentHash, out string shard, out int localId)
    {
        if (_byHash.TryGetValue(contentHash, out var row))
        {
            shard = row.Shard;
            localId = row.LocalId;
            return true;
        }
        shard = string.Empty;
        localId = -1;
        return false;
    }

    /// <summary>
    /// 按 (分片名, 本地序号) 还原内容哈希。未变分片复用 symbols.tsv 时
    /// （EmbedText 不落盘）用本方法取得构建期内容哈希。
    /// </summary>
    public bool TryGetBySlot(string shard, int localId, out string contentHash)
    {
        if (_bySlot.TryGetValue((shard, localId), out var hash))
        {
            contentHash = hash;
            return true;
        }
        contentHash = string.Empty;
        return false;
    }

    /// <summary>只返回属于指定分片的记录（未变分片重写映射表时复用）。</summary>
    public IEnumerable<Row> RowsForShard(string shard)
    {
        foreach (var row in Rows)
            if (row.Shard.Equals(shard, StringComparison.Ordinal)) yield return row;
    }

    /// <summary>整体重写映射表（先写临时文件再替换，避免中断留下半张表）。</summary>
    public static void Save(string path, IEnumerable<Row> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(false)))
        {
            foreach (var row in rows)
                writer.WriteLine($"{row.ContentHash}\t{row.Shard}\t{row.LocalId}");
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }
}
