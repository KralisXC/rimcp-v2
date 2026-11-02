using System.ComponentModel;
using ModelContextProtocol.Server;
using RiMCP.Core.Models;
using RiMCP.McpServer.Services;

namespace RiMCP.McpServer.Tools;

/// <summary>
/// 图查询工具：基于 CSR/CSC 稀疏图返回符号的下游依赖 / 上游被依赖 / 继承树。
/// </summary>
[McpServerToolType]
public static class GraphTools
{
    /// <summary>
    /// 【下游】某符号依赖了什么（它用了谁）。用于理解一个符号的实现细节。
    /// 可传 kind 过滤邻居是 C# 还是 XML。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static GraphResultDto GetRimWorldUses(
        [Description("符号引用：'#id'、完整签名或短名")] string symbol,
        [Description("可选过滤邻居来源：csharp 或 xml/def")] string? kind = null,
        [Description("返回条数上限（默认 20，1-100）")] int maxResults = 20)
        => Query(symbol, "uses", kind, maxResults);

    /// <summary>
    /// 【上游】某符号被谁引用（谁用了它）。用于评估改动影响范围。
    /// 可传 kind 过滤邻居是 C# 还是 XML。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static GraphResultDto GetRimWorldUsedBy(
        [Description("符号引用：'#id'、完整签名或短名")] string symbol,
        [Description("可选过滤邻居来源：csharp 或 xml/def")] string? kind = null,
        [Description("返回条数上限（默认 20，1-100）")] int maxResults = 20)
        => Query(symbol, "usedby", kind, maxResults);

    /// <summary>
    /// 【继承树】返回某符号的子类（direction=down，默认）或基类（direction=up）。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static GraphResultDto GetRimWorldInheritors(
        [Description("符号引用：'#id'、完整签名或短名")] string symbol,
        [Description("方向：down=子类（默认）/ up=基类")] string direction = "down",
        [Description("返回条数上限（默认 20，1-100）")] int maxResults = 20)
        => Query(symbol, "inherit-" + direction, null, maxResults);

    private static GraphResultDto Query(string symbol, string mode, string? kind, int maxResults)
    {
        try
        {
            var index = AppRuntime.Get<IndexService>();
            var graph = AppRuntime.Get<GraphService>();
            var entry = index.ResolveSymbol(symbol);
            if (entry is null)
            {
                return new GraphResultDto
                {
                    Count = 0,
                    Symbol = symbol,
                    Error = $"未找到符号 '{symbol}'。",
                };
            }

            int nodeId = entry.Id;
            if (nodeId < 0 || nodeId >= graph.NodeCount)
            {
                return new GraphResultDto
                {
                    Count = 0,
                    Symbol = entry.Signature,
                    Error = $"符号不在图中（id={nodeId}）。",
                };
            }

            List<GraphNeighbor> neighbors = mode switch
            {
                "uses" => graph.Uses(nodeId, kind).ToList(),
                "usedby" => graph.UsedBy(nodeId, kind).ToList(),
                "inherit-down" => graph.Inheritors(nodeId, down: true).ToList(),
                "inherit-up" => graph.Inheritors(nodeId, down: false).ToList(),
                _ => new List<GraphNeighbor>(),
            };

            int total = neighbors.Count;                       // 截断前总结果数
            int cap = maxResults <= 0 ? 20 : Math.Min(maxResults, 100);
            var results = neighbors.Take(cap).Select(n =>
            {
                var e = index.EntryOf(n.NodeId);
                return new GraphHitDto
                {
                    Id = n.NodeId,
                    Signature = e?.Signature ?? $"?{n.NodeId}",
                    Name = e?.ShortName ?? string.Empty,
                    EdgeKind = n.EdgeKindName,
                    Source = e is null ? "" : (e.Source == SourceType.CSharp ? "cs" : "xml"),
                    PageRank = Math.Round(n.PageRank, 6),
                };
            }).ToList();

            return new GraphResultDto
            {
                Count = results.Count,
                Symbol = entry.Signature,
                Direction = mode switch
                {
                    "uses" => "downstream",
                    "usedby" => "upstream",
                    "inherit-down" => "inherit_down",
                    "inherit-up" => "inherit_up",
                    _ => string.Empty,
                },
                TotalCount = total,
                ReturnedCount = results.Count,
                Truncated = total > cap,
                TruncatedByDisabledShards = graph.TruncatedByDisabledShards,
                Results = results,
            };
        }
        catch (Exception ex)
        {
            return new GraphResultDto { Count = 0, Symbol = symbol, Error = ex.Message };
        }
    }
}

/// <summary>图查询单条结果。</summary>
public sealed record GraphHitDto
{
    public int Id { get; set; }
    public string Signature { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EdgeKind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double PageRank { get; set; }
}

/// <summary>图查询响应体。</summary>
public sealed record GraphResultDto
{
    public int Count { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int ReturnedCount { get; set; }
    public bool Truncated { get; set; }

    /// <summary>是否因存在未启用分片而可能截断图查询结果（端点落在未启用 id 段的边已过滤）。</summary>
    public bool TruncatedByDisabledShards { get; set; }

    public List<GraphHitDto> Results { get; set; } = new();
    public string? Error { get; set; }
}