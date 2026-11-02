using System.Text;
using RiMCP.Core.Models;

namespace RiMCP.Indexer.Parsing;

/// <summary>
/// 生成干净的 Preview 片段。去除 dnSpy 反编译注释、统一换行/缩进，
/// 并以 Rune 边界安全截断（避免切到 UTF-8/代理对多字节字符中间）。
/// </summary>
public static class SourcePreview
{
    /// <summary>生成清洗并截断后的预览。输入为原始源码/XML 文本。</summary>
    public static string Make(string text, int maxLength = SymbolEntry.MaxPreviewLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = CleanSource(text);
        if (cleaned.Length <= maxLength) return cleaned;

        return TruncateRuneSafe(cleaned, maxLength) + " …";
    }

    /// <summary>清洗：去注释行、统一换行、压缩空行、tab→2 空格。</summary>
    private static string CleanSource(string text)
    {
        // 统一换行符
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var sb = new StringBuilder(normalized.Length);
        bool prevEmpty = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            // 跳过 dnSpy 反编译注释行：// Token: ... / // File Offset: ... / // (get) Token: ...
            if (trimmed.StartsWith("//", StringComparison.Ordinal) &&
                (trimmed.Contains("Token:", StringComparison.Ordinal) ||
                 trimmed.Contains("File Offset:", StringComparison.Ordinal)))
                continue;

            if (trimmed.Length == 0)
            {
                // 连续空行只保留一个
                if (!prevEmpty) sb.Append('\n');
                prevEmpty = true;
                continue;
            }

            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            // tab → 2 空格
            sb.Append(line.Replace('\t', ' ').Replace("  ", "  ")); // 只用 2 空格即可，双空行不进一步压
            prevEmpty = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>在 Rune 边界截断到 maxLength 个 UTF-16 代码单元，不切到代理对中间。</summary>
    private static string TruncateRuneSafe(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        int index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int next = index + rune.Utf16SequenceLength;
            if (next > maxLength) break;
            index = next;
        }
        return text.AsSpan(0, index).ToString();
    }
}