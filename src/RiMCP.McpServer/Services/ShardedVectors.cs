using RiMCP.Core.Storage;

namespace RiMCP.McpServer.Services;

/// <summary>
/// 多分片向量的统一访问门面：全局 id 由 build.json / manifest 的 BaseId 决定，
/// 内部转发到对应分片的 mmap vectors.bin。保持与 <see cref="VectorsBin"/> 相同的 GetVector 语义。
/// </summary>
public sealed class ShardedVectors : IDisposable
{
    private readonly IReadOnlyList<Shard> _shards;

    private sealed record Shard(int BaseId, int SymbolCount, VectorsBin File);

    public int Dim { get; }

    public long RecordCount { get; }

    public ShardedVectors(IReadOnlyList<(int BaseId, int SymbolCount, VectorsBin File)> shards, int dim)
    {
        _shards = shards.Select(s => new Shard(s.BaseId, s.SymbolCount, s.File)).ToArray();
        Dim = dim;
        RecordCount = _shards.Sum(s => (long)s.SymbolCount);
    }

    /// <summary>按全局 id 取 Half 向量；id 超出任一分片范围时抛错。</summary>
    public ReadOnlySpan<Half> GetVector(int globalId)
    {
        foreach (var shard in _shards)
        {
            if (globalId >= shard.BaseId && globalId < shard.BaseId + shard.SymbolCount)
                return shard.File.GetVector(globalId - shard.BaseId);
        }
        throw new ArgumentOutOfRangeException(
            nameof(globalId), globalId, "全局 id 不在任何已加载分片的范围内。");
    }

    public void Close()
    {
        foreach (var shard in _shards)
            shard.File.Close();
    }

    public void Dispose() => Close();
}
