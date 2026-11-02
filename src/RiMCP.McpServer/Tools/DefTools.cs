using System.ComponentModel;
using ModelContextProtocol.Server;
using RiMCP.Core.Models;
using RiMCP.McpServer.Services;

namespace RiMCP.McpServer.Tools;

/// <summary>
/// XML Def 列出工具。
/// </summary>
[McpServerToolType]
public static class DefTools
{
    /// <summary>
    /// 列出某类 XML Def（按 Def 类型过滤，例如 ThingDef、RecipeDef）。
    /// 每个 Def 返回其签名、名字、所在文件与起始行。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static ListDefsResultDto ListDefs(
        [Description("Def 类型名，例如 'ThingDef'、'RecipeDef'、'JobDef'（首字母大写）")] string defType,
        [Description("返回条数上限，默认 50")] int maxResults = 50)
    {
        try
        {
            var index = AppRuntime.Get<IndexService>();
            string t = (defType ?? string.Empty).Trim();
            if (t.Length == 0)
                return new ListDefsResultDto { Count = 0, DefType = t, Results = new List<DefHitDto>() };

            int cap = Math.Clamp(maxResults, 1, 1000);
            var hits = new List<DefHitDto>();
            for (int i = 0; i < index.Symbols.Count; i++)
            {
                var e = index.EntryOf(i);
                if (e is null || e.Kind != SymbolKind.XmlDef) continue;
                if (!e.Namespace.Equals(t, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(new DefHitDto
                {
                    Id = e.Id,
                    Name = e.ShortName,
                    Signature = e.Signature,
                    Parent = e.ParentRef ?? string.Empty,
                    Label = e.DefLabel ?? string.Empty,
                    File = e.FileRelPath,
                    LineStart = e.LineStart,
                });
                if (hits.Count >= cap) break;
            }

            return new ListDefsResultDto { Count = hits.Count, DefType = t, Results = hits };
        }
        catch (Exception ex)
        {
            return new ListDefsResultDto { Count = 0, DefType = defType ?? string.Empty, Results = new List<DefHitDto>(), Error = ex.Message };
        }
    }
}

/// <summary>ListDefs 的单条 Def。</summary>
public sealed record DefHitDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int LineStart { get; set; }
}

/// <summary>ListDefs 响应体。</summary>
public sealed record ListDefsResultDto
{
    public int Count { get; set; }
    public string DefType { get; set; } = string.Empty;
    public List<DefHitDto> Results { get; set; } = new();
    public string? Error { get; set; }
}