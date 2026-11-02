using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RiMCP.Core.Models;

namespace RiMCP.Core.Indexing;

/// <summary>
/// 符号级内容哈希：嵌入增量的唯一判定依据（禁止用分片级 vectors hash 做跳过判断）。
/// <para>
/// contentHash = SHA256("prep=&lt;rev&gt;|model=&lt;model&gt;|dim=&lt;dim&gt;|precision=&lt;precision&gt;|text=&lt;normalizedText&gt;")
/// </para>
/// </summary>
public static class ContentHash
{
    /// <summary>C# 文本规范化：统一换行、去行尾空白、折叠连续空行。</summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length);
        bool prevBlank = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                if (!prevBlank) sb.Append('\n');
                prevBlank = true;
                continue;
            }
            prevBlank = false;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    /// <summary>计算单个符号的 contentHash；<see cref="SymbolEntry.EmbedText"/> 为空时回退 Preview。</summary>
    public static string ForEntry(
        SymbolEntry entry, string embeddingModel, int dim, string precision, int prepRevision)
    {
        string body = entry.EmbedText ?? entry.Preview;
        string text = NormalizeText($"{entry.Signature}\n{body}");
        return Compute(text, embeddingModel, dim, precision, prepRevision);
    }

    /// <summary>按固定前缀 + 规范化文本计算 SHA-256（小写 hex）。</summary>
    public static string Compute(string normalizedText, string embeddingModel, int dim,
        string precision, int prepRevision)
    {
        string payload =
            $"prep={prepRevision}|model={embeddingModel}|dim={dim}|precision={precision}|text={normalizedText}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    /// <summary>
    /// XML Def 规范化：元素顺序保留，属性按名称排序，去掉缩进差异与注释；
    /// 文本节点折叠连续空白。用于 XML Def / DefField 的嵌入正文。
    /// </summary>
    public static string CanonicalizeXml(XElement element)
    {
        var sb = new StringBuilder(512);
        WriteElement(sb, element);
        return sb.ToString();
    }

    private static void WriteElement(StringBuilder sb, XElement element)
    {
        string name = element.Name.LocalName;
        sb.Append('<').Append(name);

        var attributes = element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .ThenBy(a => a.Name.NamespaceName, StringComparer.Ordinal);
        foreach (var attr in attributes)
        {
            sb.Append(' ').Append(attr.Name.LocalName).Append("=\"")
              .Append(EscapeXml(attr.Value.Trim())).Append('"');
        }

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            string value = Regex.Replace(element.Value ?? string.Empty, @"\s+", " ").Trim();
            if (value.Length > 0)
            {
                sb.Append('>').Append(EscapeXml(value)).Append("</").Append(name).Append('>');
            }
            else
            {
                sb.Append(" />");
            }
            return;
        }

        sb.Append('>');
        // 混合内容中的纯文本也保留（折叠空白）；注释节点不在 Elements() 中，自然被丢弃。
        foreach (var node in element.Nodes())
        {
            if (node is XElement child) WriteElement(sb, child);
            else if (node is XText text)
            {
                string value = Regex.Replace(text.Value, @"\s+", " ").Trim();
                if (value.Length > 0) sb.Append(EscapeXml(value));
            }
        }
        sb.Append("</").Append(name).Append('>');
    }

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
