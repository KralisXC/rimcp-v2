using System.IO;
using System.Text.Json;

namespace RiMCP.Core.Indexing;

/// <summary>
/// 来源哈希的“文件属性预检”缓存（落盘于 &lt;index&gt;/.probe-cache.json）。
/// <para>
/// 扫描 XML / C# 文件集合时先取 <c>LastWriteTimeUtc</c> 与 <c>Length</c>：
/// 若与缓存记录的属性完全一致，直接复用上次算出的来源哈希，跳过读取文件与 SHA-256；
/// 只有属性发生变化才真正读取并重新哈希，并回写缓存。
/// </para>
/// <para>
/// 缓存不改变哈希算法本身（复用值仍由 <see cref="ShardCatalog.SourceHasher"/> 流式计算得出），
/// 因此与 Indexer 写出的 manifest 保持逐字节兼容，不会把“缓存接入”误判成内容变化。
/// </para>
/// </summary>
public sealed class ProbeCache
{
    /// <summary>缓存文件名（位于索引目录内）。</summary>
    public const string FileName = ".probe-cache.json";

    private const string SchemaId = "rimcp-probe";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string? _path;
    private readonly ProbeFile _data;
    private readonly HashSet<string> _recomputed = new(StringComparer.Ordinal);
    private bool _dirty;

    private ProbeCache(string? path, ProbeFile data, bool dirty)
    {
        _path = path;
        _data = data;
        _dirty = dirty;
    }

    /// <summary>本次检测中真正重新计算过哈希的分片名（空表示全部命中缓存）。</summary>
    public IReadOnlyCollection<string> RecomputedShards => _recomputed;

    /// <summary>加载索引目录下的缓存；<paramref name="ignoreCache"/>=true 时忽略并作废旧记录（强制深度检测）。</summary>
    public static ProbeCache Load(string indexDir, bool ignoreCache = false)
    {
        string path = Path.Combine(indexDir, FileName);
        if (ignoreCache)
            return new ProbeCache(path, new ProbeFile(), dirty: true);

        try
        {
            if (!File.Exists(path))
                return new ProbeCache(path, new ProbeFile(), dirty: false);

            var data = JsonSerializer.Deserialize<ProbeFile>(File.ReadAllText(path), JsonOptions);
            if (data is null || data.Schema != SchemaId || data.Version != CurrentVersion)
                return new ProbeCache(path, new ProbeFile(), dirty: false);
            return new ProbeCache(path, data, dirty: false);
        }
        catch
        {
            // 缓存损坏时按未命中处理，不阻塞诊断。
            return new ProbeCache(path, new ProbeFile(), dirty: false);
        }
    }

    /// <summary>
    /// 计算分片来源哈希：文件属性全部命中缓存时直接复用，否则重新流式哈希。
    /// </summary>
    public string ComputeSourceHash(string shardName, string rootAbs, IEnumerable<string> files)
    {
        var (stamps, existing) = ReadStamps(rootAbs, files);

        var cached = _data.Shards.GetValueOrDefault(shardName);
        if (cached is not null && SameStamps(cached.Files, stamps))
            return cached.SourceHash;

        string hash = ShardCatalog.SourceHasher.HashFiles(rootAbs, existing);
        _data.Shards[shardName] = new ProbeShard { SourceHash = hash, Files = stamps };
        _recomputed.Add(shardName);
        _dirty = true;
        return hash;
    }

    /// <summary>回写缓存（原子替换）；任何 IO 失败都只影响下次是否命中，不抛出。</summary>
    public void Save()
    {
        if (!_dirty || _path is null) return;

        try
        {
            string dir = Path.GetDirectoryName(_path)!;
            if (!Directory.Exists(dir)) return;
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch
        {
            // 缓存写失败时保持旧文件，下次检测退化为重新哈希。
        }
    }

    private static (Dictionary<string, ProbeStamp> Stamps, List<string> Existing) ReadStamps(
        string rootAbs, IEnumerable<string> files)
    {
        var stamps = new Dictionary<string, ProbeStamp>(StringComparer.OrdinalIgnoreCase);
        var existing = new List<string>();

        foreach (var file in files)
        {
            string abs = Path.GetFullPath(file);
            if (!File.Exists(abs)) continue;

            var info = new FileInfo(abs);
            string rel = Path.GetRelativePath(Path.GetFullPath(rootAbs), abs)
                .Replace('\\', '/');
            stamps[rel] = new ProbeStamp
            {
                Mtime = info.LastWriteTimeUtc.Ticks,
                Size = info.Length,
            };
            existing.Add(abs);
        }
        return (stamps, existing);
    }

    private static bool SameStamps(
        Dictionary<string, ProbeStamp> cached, Dictionary<string, ProbeStamp> current)
    {
        if (cached.Count != current.Count) return false;
        foreach (var (rel, stamp) in current)
        {
            var old = cached.GetValueOrDefault(rel);
            if (old is null || old.Mtime != stamp.Mtime || old.Size != stamp.Size) return false;
        }
        return true;
    }

    private sealed class ProbeFile
    {
        public string Schema { get; set; } = SchemaId;
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, ProbeShard> Shards { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ProbeShard
    {
        public string SourceHash { get; set; } = string.Empty;

        /// <summary>相对路径 → 上次哈希时的 mtime（UTC ticks）与文件长度。</summary>
        public Dictionary<string, ProbeStamp> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ProbeStamp
    {
        public long Mtime { get; set; }
        public long Size { get; set; }
    }
}
