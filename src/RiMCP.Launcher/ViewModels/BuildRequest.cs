using System.IO;
using RiMCP.Launcher.Services;

namespace RiMCP.Launcher.ViewModels;

/// <summary>构建操作类型（与 Indexer CLI 一一对应）。</summary>
public enum BuildOperation
{
    /// <summary>--incremental（默认行为）。</summary>
    Incremental,

    /// <summary>--force-clean。</summary>
    Full,

    /// <summary>--graph-only。</summary>
    GraphOnly,
}

/// <summary>
/// 一次构建请求：把 UI 选择拼装成 Indexer 命令行参数。
/// 注意：Indexer 当前没有“单层触发 / 按分片选择”参数，仅嵌入与仅 Lucene 降级为增量 + 分片勾选提示。
/// </summary>
public sealed class BuildRequest
{
    public required BuildOperation Operation { get; init; }

    /// <summary>勾选的分片（仅提示用；Indexer 无法只构建指定分片，未变分片会自动跳过）。</summary>
    public IReadOnlyList<string> SelectedShards { get; init; } = Array.Empty<string>();

    public required string RootDir { get; init; }

    /// <summary>索引目录名（如 index_17），通过 <c>--index-dir</c> 传给 Indexer。</summary>
    public required string IndexDirName { get; init; }

    public required string ModelDir { get; init; }
    public required string Provider { get; init; }
    public int BatchSize { get; init; } = 8;

    /// <summary>拼装 Indexer 参数（不含 dotnet 可执行文件本身）。</summary>
    public List<string> ToArguments()
    {
        var args = new List<string>
        {
            "--root", Path.Combine(RootDir, "RimWorldData"),
            "--index-dir", IndexDirName,
            "--model-dir", ModelDir,
            "--onnx-provider", Provider,
            "--batch-size", Math.Max(1, BatchSize).ToString(),
        };

        args.Add(Operation switch
        {
            BuildOperation.Full => "--force-clean",
            BuildOperation.GraphOnly => "--graph-only",
            _ => "--incremental",
        });

        return args;
    }

    /// <summary>当前操作的中文描述（用于日志与确认对话框）。</summary>
    public string Describe()
    {
        string baseText = Operation switch
        {
            BuildOperation.Full => "全量重建",
            BuildOperation.GraphOnly => "仅图谱",
            _ => "增量构建",
        };
        if (SelectedShards.Count == 0) return baseText;
        return $"{baseText}（勾选分片：{string.Join(", ", SelectedShards)}；Indexer 将自动跳过未变分片）";
    }

    /// <summary>按 LauncherConfig 取值构造请求。</summary>
    public static BuildRequest Create(
        BuildOperation operation, LauncherConfig config, string rootDir, string indexDirName,
        string modelDir, IEnumerable<string> selectedShards)
    {
        return new BuildRequest
        {
            Operation = operation,
            SelectedShards = selectedShards.ToList(),
            RootDir = rootDir,
            IndexDirName = indexDirName,
            ModelDir = modelDir,
            Provider = config.ResolveProvider(),
            BatchSize = config.BatchSize,
        };
    }
}
