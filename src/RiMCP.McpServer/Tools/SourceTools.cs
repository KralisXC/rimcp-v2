using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RiMCP.Core.Models;
using RiMCP.McpServer.Services;

namespace RiMCP.McpServer.Tools;

/// <summary>
/// 源码获取工具：按符号名定位并读取对应源文件片段。
/// </summary>
[McpServerToolType]
public static class SourceTools
{
    /// <summary>
    /// 按符号（支持 #id / 完整签名 / 短名）读取其对应的源文件片段。
    /// 返回 JSON，字段含 source/file/lineStart/content 等；content 为从符号起始行开始读取的源码。
    /// </summary>
    [McpServerTool(ReadOnly = true)]
    public static SourceResultDto GetRimWorldSource(
        [Description("符号引用：'#123'（id）、完整签名（如 'Verse.IntVec3.Rotated(?)'）或短名（如 'Need_Food'）")] string symbol,
        [Description("读取的最大行数，默认 200")] int maxLines = 200)
    {
        try
        {
            var index = AppRuntime.Get<IndexService>();
            var entry = index.ResolveSymbol(symbol);
            if (entry is null)
            {
                // fuzzy 回退：去掉 xml: 前缀再试；仍不行则用签名末尾短名 + kind=XmlDef 匹配
                entry = index.ResolveSymbol(stripPrefix(symbol))
                     ?? FindByTailName(index, symbol);
            }
            if (entry is null)
            {
                return new SourceResultDto
                {
                    Count = 0,
                    Symbol = symbol,
                    Error = $"未找到符号 '{symbol}'。请先用 SearchRimWorldCode 或 SearchSymbol 确认名称/签名。",
                };
            }

            string path = index.SourcePath(entry);
            if (path.Length == 0 || !File.Exists(path))
            {
                return new SourceResultDto
                {
                    Count = 0,
                    Symbol = entry.Signature,
                    Error = $"源码文件不存在：{path}（可能已在本地移除）。",
                };
            }

            int cap = Math.Clamp(maxLines, 1, 10000);
            int from = Math.Max(1, entry.LineStart); // 1-based
            int to = entry.LineEnd > 0 ? Math.Min(entry.LineEnd, from + cap - 1) : from + cap - 1;
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (to > lines.Length) to = lines.Length;

            var sb = new StringBuilder();
            int fetched = 0;
            for (int ln = from; ln <= to; ln++, fetched++)
                sb.Append(lines[ln - 1]).Append('\n');

            bool truncated = fetched < lines.Length - from + 1;
            string content = CleanSource(sb.ToString());

            return new SourceResultDto
            {
                Count = fetched,
                Symbol = entry.Signature,
                Name = entry.ShortName,
                Kind = entry.Kind.ToString(),
                Source = entry.Source == SourceType.CSharp ? "cs" : "xml",
                File = entry.FileRelPath,
                LineStart = from,
                TotalLines = lines.Length,
                Content = content,
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            return new SourceResultDto { Count = 0, Symbol = symbol, Error = ex.Message };
        }
    }

    /// <summary>去掉 "xml:" 前缀（仅当存在）。</summary>
    private static string stripPrefix(string symbol)
        => symbol.StartsWith("xml:", StringComparison.OrdinalIgnoreCase) ? symbol[4..].Trim() : symbol;

    /// <summary>
    /// 清洗反编译源码：剔除 dnSpy 注释行，统一换行、tab→2 空格、压缩连续空行。
    /// 仅处理传入的文本（按行），不感知文件类型。
    /// </summary>
    private static string CleanSource(string text)
    {
        if (text.Length == 0) return string.Empty;

        var sb = new StringBuilder(text.Length);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool prevBlank = true; // 避免开头空行
        foreach (var raw in lines)
        {
            string line = raw.Replace("\t", "  ");
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("// Token:", StringComparison.Ordinal) ||
                trimmed.StartsWith("// (get) Token:", StringComparison.Ordinal) ||
                trimmed.StartsWith("// (set) Token:", StringComparison.Ordinal) ||
                trimmed.StartsWith("// File Offset:", StringComparison.Ordinal))
                continue;

            bool blank = line.Trim().Length == 0;
            if (blank)
            {
                if (prevBlank) continue;
                prevBlank = true;
                sb.Append('\n');
                continue;
            }
            prevBlank = false;
            sb.Append(line.TrimEnd()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 用签名末尾短名匹配：取输入最后一个 ':' 之后的片段，找 ShortName 等于它且 kind=XmlDef 的条目。
    /// 例如 "ThingDef:Door" → 找 name="Door" 且 XmlDef。
    /// </summary>
    private static SymbolEntry? FindByTailName(IndexService index, string symbol)
    {
        string raw = stripPrefix(symbol);
        int colon = raw.LastIndexOf(':');
        if (colon < 0 || colon == raw.Length - 1) return null;
        string tail = raw[(colon + 1)..].Trim();
        if (tail.Length == 0) return null;

        for (int i = 0; i < index.Symbols.Count; i++)
        {
            var e = index.EntryOf(i);
            if (e is null) continue;
            if (e.Kind != SymbolKind.XmlDef) continue;
            if (e.ShortName.Equals(tail, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }
}

/// <summary>GetRimWorldSource 响应体。</summary>
public sealed record SourceResultDto
{
    public int Count { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int LineStart { get; set; }
    public int TotalLines { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}