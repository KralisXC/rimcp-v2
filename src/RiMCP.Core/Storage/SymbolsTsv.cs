using System.Text;
using RiMCP.Core.Models;

namespace RiMCP.Core.Storage;

/// <summary>
/// symbols.tsv 的读写与字段转义。一行一条，首列为 Id（升序写出）。
/// 转义规则：物理 \t \n \r \ → 反斜杠 + t/n/r/\\；空字符串 = 分隔符之间无内容。
/// </summary>
public static class SymbolsTsv
{
    private const string Header =
        "id\tkind\tsource\tshortName\tns\tsignature\tfileRel\tlineStart\tlineEnd\tisLeaf\tdefLabel\tparentRef\tpreview";

    /// <summary>从文件全量加载为按 Id 排序的条目列表。</summary>
    public static List<SymbolEntry> Load(string path)
    {
        var entries = new List<SymbolEntry>();
        bool headerSkipped = false;
        foreach (var rawLine in File.ReadLines(path))
        {
            if (rawLine.Length == 0) continue;
            if (!headerSkipped && rawLine.StartsWith("id\t", StringComparison.Ordinal))
            {
                headerSkipped = true;
                continue;
            }
            headerSkipped = true;

            var parts = rawLine.Split('\t');
            var e = new SymbolEntry
            {
                Id = ParseInt(Get(parts, 0), "id"),
                Kind = Enum.TryParse<SymbolKind>(Get(parts, 1), out var k) ? k : SymbolKind.CsClass,
                Source = Enum.TryParse<SourceType>(Get(parts, 2), out var s) ? s : SourceType.CSharp,
                ShortName = Unescape(Get(parts, 3)),
                Namespace = Unescape(Get(parts, 4)),
                Signature = Unescape(Get(parts, 5)),
                FileRelPath = Unescape(Get(parts, 6)),
                LineStart = ParseInt(Get(parts, 7), "lineStart"),
                LineEnd = ParseInt(Get(parts, 8), "lineEnd"),
                IsLeaf = ParseBool(Get(parts, 9)),
                DefLabel = EmptyToNull(Unescape(Get(parts, 10))),
                ParentRef = EmptyToNull(Unescape(Get(parts, 11))),
                Preview = Unescape(Get(parts, 12)),
            };
            entries.Add(e);
        }
        return entries;
    }

    /// <summary>将条目列表写为 TSV，含表头行；Id 由调用方通过 IdAllocator 保证已排定。</summary>
    public static void Save(string path, IReadOnlyList<SymbolEntry> entries)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.WriteLine(Header);

        foreach (var e in entries)
        {
            var sb = new StringBuilder(512);
            sb.Append(e.Id).Append('\t');
            sb.Append(e.Kind).Append('\t');
            sb.Append(e.Source).Append('\t');
            EscapeField(sb, e.ShortName); sb.Append('\t');
            EscapeField(sb, e.Namespace); sb.Append('\t');
            EscapeField(sb, e.Signature); sb.Append('\t');
            EscapeField(sb, e.FileRelPath); sb.Append('\t');
            sb.Append(e.LineStart).Append('\t');
            sb.Append(e.LineEnd).Append('\t');
            sb.Append(e.IsLeaf ? "1" : "0").Append('\t');
            EscapeField(sb, e.DefLabel); sb.Append('\t');
            EscapeField(sb, e.ParentRef); sb.Append('\t');
            EscapeField(sb, e.Preview);
            writer.WriteLine(sb.ToString());
        }
    }

    private static string Get(string[] parts, int i) => i < parts.Length ? parts[i] : string.Empty;

    private static int ParseInt(string s, string name)
        => int.TryParse(s, out var v)
            ? v
            : throw new InvalidDataException($"symbols.tsv 解析失败：字段 {name} 值“{s}”非法。");

    private static bool ParseBool(string s) => s is "1" or "true" or "True" or "TRUE";

    private static string? EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>对单个字段做转义并追加到 builder。</summary>
    private static void EscapeField(StringBuilder sb, string? value)
    {
        if (value == null) return;
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    /// <summary>反转义为原字符串。</summary>
    private static string Unescape(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                char n = raw[i + 1];
                switch (n)
                {
                    case 't': sb.Append('\t'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    default: sb.Append(c); break; // 孤立反斜杠原样保留
                }
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}