using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;

namespace RiMCP.Indexer.Parsing;

/// <summary>
/// 用 XDocument 解析 RimWorldData/Data 下 XML Defs。
/// 产出 XmlDef 节点，并对每个 Def 下有值文本的叶子直接子元素产出 XmlDefField。
/// ParentRef 取 ParentName 指向的父 Def 签名。
/// </summary>
public static class XmlParser
{
    /// <summary>扫描 root 下 Data 目录（或整体 root）的全部 .xml 并解析 Def 节点。</summary>
    public static List<SymbolEntry> ParseDirectory(string rootAbs, int maxParallelism)
    {
        var dataRoot = Path.Combine(rootAbs, RiMCP.Indexer.Config.IndexerConfig.XmlSubDir);
        if (!Directory.Exists(dataRoot))
            throw new DirectoryNotFoundException($"XML Def 目录不存在：{dataRoot}");

        var files = Directory.EnumerateFiles(dataRoot, "*.xml", SearchOption.AllDirectories).ToArray();
        return ParseFiles(rootAbs, files, maxParallelism);
    }

    /// <summary>解析指定的 XML 文件集合（分片构建使用，避免递归扫描到其它分片）。</summary>
    public static List<SymbolEntry> ParseFiles(string rootAbs, IReadOnlyList<string> files, int maxParallelism)
    {
        var bag = new ConcurrentBag<SymbolEntry>();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, file =>
        {
            try
            {
                foreach (var entry in ParseFile(file, rootAbs))
                    bag.Add(entry);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[XML] 解析失败 {file}：{ex.Message}");
            }
        });

        return bag.ToList();
    }

    // 注意：UpdateAdvisor 中有一套针对 XML 内容哈希的复刻实现用于环境诊断。
    // 如果本处的 Def 提取规则（*Def 元素、defName、ParentName、signature 拼装）发生修改，
    // 请务必同步更新 RiMCP.Core/Indexing/UpdateAdvisor.cs 中的 AddXmlFileEntries。
    /// <summary>解析单个 .xml 文件，每个 *Def 元素产出一条 <see cref="SymbolEntry"/>。</summary>
    public static IEnumerable<SymbolEntry> ParseFile(string fileAbs, string rootAbs)
    {
        XDocument doc;
        using (var stream = File.OpenRead(fileAbs))
            doc = XDocument.Load(stream, LoadOptions.SetLineInfo);

        if (doc.Root is null) yield break;

        var allLines = File.ReadAllLines(fileAbs);
        var relPath = RelativePathUtil.ToRel(rootAbs, fileAbs);

        foreach (var element in doc.Root.Elements().Where(e => e.Name.LocalName.EndsWith("Def", StringComparison.OrdinalIgnoreCase)))
        {
            var defName = element.Element("defName")?.Value ?? element.Element("DefName")?.Value;
            if (string.IsNullOrWhiteSpace(defName)) continue;

            var defType = element.Name.LocalName;
            var parentName = element.Attribute("ParentName")?.Value
                             ?? element.Element("parentName")?.Value
                             ?? element.Element("ParentName")?.Value;
            var label = element.Element("label")?.Value?.Trim();

            var parentSuffix = string.IsNullOrWhiteSpace(parentName) ? string.Empty : $" <- {parentName}";
            var signature = $"{defType}:{defName}{parentSuffix}";
            string? parentRef = string.IsNullOrWhiteSpace(parentName) ? null : $"{defType}:{parentName}";

            var (startLine, endLine) = GetLines(allLines, element);

            // Preview：类型 + 名字 + label 摘要 + 截断原始 XML
            var head = label is null ? $"{defType}: {defName}" : $"{defType} ({label}): {defName}";
            var raw = element.ToString(SaveOptions.DisableFormatting);
            var preview = BuildPreview($"{head}\n\n{raw}");

            yield return new SymbolEntry
            {
                Id = 0,
                Kind = SymbolKind.XmlDef,
                Source = SourceType.Xml,
                ShortName = defName,
                Namespace = defType,
                Signature = signature,
                FileRelPath = relPath,
                LineStart = startLine,
                LineEnd = endLine,
                IsLeaf = false,
                DefLabel = defName,
                ParentRef = parentRef,
                Preview = preview,
                EmbedText = ContentHash.CanonicalizeXml(element),
            };

            // Def 直接子元素中「有意义文本的叶子字段」→ XmlDefField
            foreach (var field in BuildDefFields(allLines, element, defType, defName, signature, relPath))
                yield return field;
        }
    }

    // 注意：UpdateAdvisor 中有一套针对 XML 内容哈希的复刻实现用于环境诊断。
    // 如果本处的 Def/DefField 提取规则发生修改，请务必同步更新
    // RiMCP.Core/Indexing/UpdateAdvisor.cs 中的 ComputeXmlContentEntries/AddXmlFileEntries，
    // 否则会把“仅属性换序”误判为内容变化（或反之）。
    /// <summary>
    /// 解析 Def 下的有值叶子子元素为 XmlDefField。规则：
    /// 仅直接子元素；叶子（无元素子节点）；文本 trim 后长度 ∈ [1,200]；跳过纯容器（statBases/comps/…）。
    /// 排除 <li>（列表项数量爆炸）以及已被当作 Def 元数据的 defName/label/class/parentName。
    /// </summary>
    private static IEnumerable<SymbolEntry> BuildDefFields(
        string[] allLines, XElement def, string defType, string defName, string defSignature, string relPath)
    {
        var meta = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "defName", "label", "class", "classname", "parentname",
        };

        foreach (var child in def.Elements())
        {
            var childName = child.Name.LocalName;
            if (meta.Contains(childName)) continue;      // 元数据字段已单独表达
            if (childName == "li") continue;             // 列表项：跳过
            if (child.Elements().Any()) continue;        // 纯容器：跳过

            var value = child.Value?.Trim() ?? string.Empty;
            if (value.Length < 1 || value.Length > SymbolEntry.MaxPreviewLength) continue;

            var signature = $"{defSignature}.{childName}";
            var (startLine, endLine) = GetLines(allLines, child);
            yield return new SymbolEntry
            {
                Id = 0,
                Kind = SymbolKind.XmlDefField,
                Source = SourceType.Xml,
                ShortName = childName,
                Namespace = defType,
                Signature = signature,
                FileRelPath = relPath,
                LineStart = startLine,
                LineEnd = endLine,
                IsLeaf = true,
                DefLabel = value,
                ParentRef = defSignature,
                Preview = BuildPreview($"{defType}:{defName}.{childName} = {value}"),
                EmbedText = ContentHash.CanonicalizeXml(child),
            };
        }
    }

    /// <summary>
    /// 计算元素在源文件中的起止行号。起始 = 元素打开标签的 LineInfo；结束 = 匹配的闭合标签
    /// <c>&lt;/LocalName&gt;</c> 所在行。扫描时跳过 XML 注释行（<c>&lt;!-- ... --&gt;</c>）。
    /// </summary>
    private static (int Start, int End) GetLines(string[] allLines, XElement element)
    {
        var info = (IXmlLineInfo)element;
        int start = info.HasLineInfo() ? info.LineNumber : 1;
        if (start < 1) start = 1;

        string closeTag = $"</{element.Name.LocalName}>";
        int end = start;
        for (int i = start; i <= allLines.Length; i++)
        {
            string line = allLines[i - 1];
            if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal)) continue; // 结束标签绝不在注释行
            if (line.Contains(closeTag, StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }
        if (end < start) end = start;
        return (start, end);
    }

    private static string BuildPreview(string text, int maxLength = SymbolEntry.MaxPreviewLength)
        => SourcePreview.Make(text, maxLength);
}