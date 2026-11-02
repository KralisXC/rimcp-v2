using ModelContextProtocol.Server;
using RiMCP.McpServer.Services;

namespace RiMCP.McpServer.Tools;

/// <summary>
/// 索引统计工具。
/// </summary>
[McpServerToolType]
public static class StatsTools
{
    /// <summary>
    /// 返回索引整体统计信息：符号数、图节点/边数、向量维度、模型信息等。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static GetRimWorldStatsResultDto GetRimWorldStats()
    {
        try
        {
            var index = AppRuntime.Get<IndexService>();
            var graph = AppRuntime.Get<GraphService>();
            var b = index.Build;

            return new GetRimWorldStatsResultDto
            {
                Count = 1,
                Dim = index.Dim,
                Symbols = b.Counts?.Symbols ?? 0,
                Nodes = graph.NodeCount,
                Edges = graph.EdgeCount,
                Model = b.Model?.Name ?? string.Empty,
                LuceneOk = b.Lucene?.Ok ?? false,
                FormatVersion = b.FormatVersion,
            };
        }
        catch (Exception ex)
        {
            return new GetRimWorldStatsResultDto { Count = 0, Error = ex.Message };
        }
    }
}

/// <summary>GetRimWorldStats 响应体。</summary>
public sealed record GetRimWorldStatsResultDto
{
    public int Count { get; set; }
    public long Symbols { get; set; }
    public long Nodes { get; set; }
    public long Edges { get; set; }
    public int Dim { get; set; }
    public string Model { get; set; } = string.Empty;
    public bool LuceneOk { get; set; }
    public int FormatVersion { get; set; }
    public string? Error { get; set; }
}