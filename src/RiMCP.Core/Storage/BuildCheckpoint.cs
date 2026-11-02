using System.Text;

namespace RiMCP.Core.Storage;

/// <summary>
/// 嵌入计算检查点（index/.build/checkpoint.tsv）。
/// <para>只覆盖 ONNX 计算阶段；每完成一个批次追加一行：<c>batchNo \t contentHash1,contentHash2,...</c>。</para>
/// <para>恢复前必须校验 vectors.bin 的 id 从 0 连续且记录数等于全量符号数（复制阶段已完整写入），
/// 否则丢弃检查点并重跑嵌入层。</para>
/// </summary>
public sealed class BuildCheckpoint
{
    /// <summary>检查点文件名。</summary>
    public const string FileName = "checkpoint.tsv";

    /// <summary>检查点文件所在子目录（相对 index）。</summary>
    public const string DirectoryName = ".build";

    /// <summary>index/.build/checkpoint-&lt;shard&gt;.tsv（每分片独立，避免多分片重建时混用）。</summary>
    public static string PathFor(string indexDir, string shard)
        => Path.Combine(indexDir, DirectoryName, $"checkpoint-{shard}.tsv");

    /// <summary>已完成的 contentHash 集合。</summary>
    public IReadOnlySet<string> CompletedHashes { get; }

    /// <summary>最后一条已提交批次号；无记录时为 0。</summary>
    public int LastBatchNo { get; }

    private BuildCheckpoint(IReadOnlySet<string> completed, int lastBatchNo)
    {
        CompletedHashes = completed;
        LastBatchNo = lastBatchNo;
    }

    /// <summary>读取检查点；不存在返回空检查点。</summary>
    public static BuildCheckpoint Load(string path)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        int last = 0;
        if (!File.Exists(path))
            return new BuildCheckpoint(hashes, last);

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (raw.Length == 0) continue;
            int tab = raw.IndexOf('\t');
            if (tab <= 0) continue;
            if (int.TryParse(raw[..tab].Trim(), out var batch) && batch > last)
                last = batch;
            foreach (var hash in raw[(tab + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (hash.Length > 0) hashes.Add(hash);
        }
        return new BuildCheckpoint(hashes, last);
    }

    /// <summary>追加一个已完成批次（立即 flush，进程中断也不丢已提交批次）。</summary>
    public static void AppendBatch(string path, int batchNo, IEnumerable<string> contentHashes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine($"{batchNo}\t{string.Join(",", contentHashes)}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>构建成功后删除检查点（连同空目录）。</summary>
    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    /// <summary>
    /// 校验 vectors.bin：记录数等于期望值，且每条记录头的 id 严格等于其下标（0..N-1 连续）。
    /// </summary>
    public static bool ValidateVectors(string vectorsPath, long expectedCount)
    {
        if (!File.Exists(vectorsPath)) return false;
        try
        {
            using var vectors = VectorsBin.Open(vectorsPath);
            if (vectors.RecordCount != expectedCount) return false;
            for (int i = 0; i < expectedCount; i++)
                if (vectors.GetRecordId(i) != i) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
