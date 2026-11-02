using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RiMCP.Core.Models;
// 消除 Microsoft.CodeAnalysis.SymbolKind 与 RiMCP.Core.Models.SymbolKind 的歧义
using SymbolKind = RiMCP.Core.Models.SymbolKind;

namespace RiMCP.Indexer.Parsing;

/// <summary>
/// 用 Roslyn 解析 C# 源码，提取 Type/Method/Constructor/Property/const Field 并产出一条
/// <see cref="SymbolEntry"/>（Id 先设 0，由 P2 的 IdAllocator 统一赋定）。粒度沿用旧版
/// Chunker：仅 const 字段，不含普通字段。
/// </summary>
public static class CSharpParser
{
    /// <summary>
    /// 解析 <paramref name="csharpSubdir"/>（相对 RimWorldData 根，如 RimWorld_Decompiled/Assembly-CSharp）下的
    /// 所有 .cs 文件。过滤用「去掉 subdir 前缀后的相对路径」套白名单；根目录下无 "/" 的文件始终保留。
    /// FileRelPath 仍存「相对 RimWorldData 根的完整相对路径」。
    /// </summary>
    public static List<SymbolEntry> ParseDirectory(
        string rootAbs, string csharpSubdir, int maxParallelism, IReadOnlyList<string> includeRoots)
    {
        string scanDir = Path.GetFullPath(Path.Combine(rootAbs, csharpSubdir.Replace('\\', '/')));
        if (!Directory.Exists(scanDir))
            throw new DirectoryNotFoundException($"C# 源码目录不存在：{scanDir}");

        var allFiles = Directory
            .EnumerateFiles(scanDir, "*.cs", SearchOption.AllDirectories)
            .ToArray();

        var roots = new HashSet<string>(includeRoots, StringComparer.Ordinal);
        var kept = new List<string>(allFiles.Length);
        int skipped = 0;

        foreach (var file in allFiles)
        {
            // 相对扫描目录：第一段即顶级目录（白名单判据）
            string relToScan = RelativePathUtil.ToRel(scanDir, file);
            int slash = relToScan.IndexOf('/');
            if (slash < 0)
            {
                kept.Add(file);          // 源码根目录文件：保留
            }
            else if (roots.Contains(relToScan[..slash]))
            {
                kept.Add(file);          // 白名单顶级目录：保留
            }
            else
            {
                skipped++;
            }
        }

        Console.WriteLine($"[C#] 扫到 {allFiles.Length} 个 .cs，白名单过滤后 {kept.Count} 个进入解析（跳过 {skipped}）");

        var bag = new ConcurrentBag<SymbolEntry>();
        Parallel.ForEach(kept, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, file =>
        {
            try
            {
                foreach (var entry in ParseFile(file, rootAbs))
                    bag.Add(entry);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[C#] 解析失败 {file}：{ex.Message}");
            }
        });

        return bag.ToList();
    }

    /// <summary>
    /// 解析单个 .cs 文件为若干 <see cref="SymbolEntry"/>。FileRelPath 为相对 RimWorldData 根
    /// （保留 subdir 前缀），保证 McpServer 可据此定位原始文件。
    /// </summary>
    public static IEnumerable<SymbolEntry> ParseFile(string fileAbs, string rootAbs)
    {
        var text = File.ReadAllText(fileAbs);
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetCompilationUnitRoot();
        var sourceText = tree.GetText();
        var relPath = RelativePathUtil.ToRel(rootAbs, fileAbs);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case TypeDeclarationSyntax t:
                    yield return BuildType(t, sourceText, relPath);
                    break;
                case EnumDeclarationSyntax e:
                    yield return BuildType(e, sourceText, relPath);
                    break;
                case MethodDeclarationSyntax m:
                    var me = BuildMember(m, m.Identifier.Text, relPath, SymbolKind.CsMethod, sourceText, m);
                    if (me != null) yield return me;
                    break;
                case ConstructorDeclarationSyntax c:
                    var ce = BuildMember(c, c.Identifier.Text, relPath, SymbolKind.CsMethod, sourceText, c);
                    if (ce != null) yield return ce;
                    break;
                case PropertyDeclarationSyntax p:
                    var pe = BuildMember(p, p.Identifier.Text, relPath, SymbolKind.CsProperty, sourceText, p);
                    if (pe != null) yield return pe;
                    break;
                case EnumMemberDeclarationSyntax em:
                    var env = BuildEnumMember(em, sourceText, relPath);
                    if (env != null) yield return env;
                    break;
                case EventDeclarationSyntax ed:
                    var eve = BuildMember(ed, ed.Identifier.Text, relPath, SymbolKind.CsEvent, sourceText, ed);
                    if (eve != null) yield return eve;
                    break;
                case EventFieldDeclarationSyntax efd:
                    foreach (var evnt in BuildEventField(efd, relPath, sourceText))
                        yield return evnt;
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var field in BuildFields(f, relPath, sourceText))
                        yield return field;
                    break;
            }
        }
    }

    /// <summary>编译器生成的字段跳过：名字含 "&lt;"、含 "k__BackingField"、或以 "__" 开头。</summary>
    private static bool IsCompilerGeneratedField(string name)
        => name.Contains('<') || name.Contains("k__BackingField") || name.StartsWith("__", StringComparison.Ordinal);

    private static SymbolEntry BuildType(BaseTypeDeclarationSyntax type, SourceText sourceText, string relPath)
    {
        var ns = GetNamespace(type);
        var typeName = SafeTypeName(type, relPath);
        var kind = type switch
        {
            ClassDeclarationSyntax => SymbolKind.CsClass,
            StructDeclarationSyntax => SymbolKind.CsStruct,
            InterfaceDeclarationSyntax => SymbolKind.CsInterface,
            EnumDeclarationSyntax => SymbolKind.CsEnum,
            _ => SymbolKind.CsClass,
        };
        string signature = FullType(ns, typeName);
        var (ls, le) = GetLines(sourceText, type.Span);
        return new SymbolEntry
        {
            Id = 0,
            Kind = kind,
            Source = SourceType.CSharp,
            ShortName = typeName,
            Namespace = ns,
            Signature = signature,
            FileRelPath = relPath,
            LineStart = ls,
            LineEnd = le,
            IsLeaf = false,
            ParentRef = null,
            Preview = BuildPreview(type.ToFullString()),
        };
    }

    private static SymbolEntry? BuildMember(
        MemberDeclarationSyntax node, string name, string relPath, SymbolKind kind, SourceText sourceText, SyntaxNode bodyNode)
    {
        var typeDecl = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null) return null; // 顶层成员无容器，跳过

        var ns = GetNamespace(typeDecl);
        var parentSig = FullType(ns, SafeTypeName(typeDecl, relPath));
        var signature = BuildDeclaration(node, ns, parentSig);
        var (ls, le) = GetLines(sourceText, bodyNode.Span);
        return new SymbolEntry
        {
            Id = 0,
            Kind = kind,
            Source = SourceType.CSharp,
            ShortName = name,
            Namespace = ns,
            Signature = signature,
            FileRelPath = relPath,
            LineStart = ls,
            LineEnd = le,
            IsLeaf = true,
            ParentRef = parentSig,
            Preview = BuildPreview(bodyNode.ToFullString()),
        };
    }

    /// <summary>字段：循环每个变量产出 SymbolEntry，跳过编译器生成字段。</summary>
    private static IEnumerable<SymbolEntry> BuildFields(FieldDeclarationSyntax field, string relPath, SourceText sourceText)
    {
        var typeDecl = field.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null || field.Declaration is null) yield break;

        var ns = GetNamespace(typeDecl);
        var parentSig = FullType(ns, typeDecl.Identifier.Text);
        var (ls, le) = GetLines(sourceText, field.Span);
        foreach (var variable in field.Declaration.Variables)
        {
            var name = variable.Identifier.Text;
            if (IsCompilerGeneratedField(name)) continue;
            yield return new SymbolEntry
            {
                Id = 0,
                Kind = SymbolKind.CsField,
                Source = SourceType.CSharp,
                ShortName = name,
                Namespace = ns,
                Signature = BuildFieldSignature(field, parentSig, variable),
                FileRelPath = relPath,
                LineStart = ls,
                LineEnd = le,
                IsLeaf = true,
                ParentRef = parentSig,
                Preview = BuildPreview(field.ToFullString()),
            };
        }
    }

    /// <summary>事件字段（如 <c>event Action X;</c>）：多条变量各产一条。</summary>
    private static IEnumerable<SymbolEntry> BuildEventField(EventFieldDeclarationSyntax evt, string relPath, SourceText sourceText)
    {
        var typeDecl = evt.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null || evt.Declaration is null) yield break;

        var ns = GetNamespace(typeDecl);
        var parentSig = FullType(ns, typeDecl.Identifier.Text);
        var (ls, le) = GetLines(sourceText, evt.Span);
        foreach (var variable in evt.Declaration.Variables)
        {
            var name = variable.Identifier.Text;
            var signature = $"{parentSig} event {evt.Declaration.Type} {name}".Trim();
            yield return new SymbolEntry
            {
                Id = 0,
                Kind = SymbolKind.CsEvent,
                Source = SourceType.CSharp,
                ShortName = name,
                Namespace = ns,
                Signature = signature,
                FileRelPath = relPath,
                LineStart = ls,
                LineEnd = le,
                IsLeaf = true,
                ParentRef = parentSig,
                Preview = BuildPreview(evt.ToFullString()),
            };
        }
    }

    /// <summary>枚举成员：SymbolKind.CsEnumValue，ParentRef = 所属枚举签名。</summary>
    private static SymbolEntry? BuildEnumMember(EnumMemberDeclarationSyntax member, SourceText sourceText, string relPath)
    {
        var enumDecl = member.Ancestors().OfType<EnumDeclarationSyntax>().FirstOrDefault();
        if (enumDecl == null) return null;
        var ns = GetNamespace(enumDecl);
        var parentSig = FullType(ns, enumDecl.Identifier.Text);
        var name = member.Identifier.Text;
        var (ls, le) = GetLines(sourceText, member.Span);
        return new SymbolEntry
        {
            Id = 0,
            Kind = SymbolKind.CsEnumValue,
            Source = SourceType.CSharp,
            ShortName = name,
            Namespace = ns,
            Signature = $"{parentSig}.{name}",
            FileRelPath = relPath,
            LineStart = ls,
            LineEnd = le,
            IsLeaf = true,
            ParentRef = parentSig,
            Preview = BuildPreview(member.ToFullString()),
        };
    }

    private static string GetNamespace(SyntaxNode node)
        => node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;

    private static string FullType(string ns, string typeName)
        => string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

    /// <summary>
    /// 取类型名；若 Roslyn 解析失败（如 dnSpy 合成的 "$Bur..." 类名带 '$' 无法作标识符，
    /// Identifier 缺失为空），回退用文件基名填充，保证 signature/shortName 非空。
    /// </summary>
    private static string SafeTypeName(BaseTypeDeclarationSyntax type, string relPath)
    {
        var name = type.Identifier.Text;
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return Path.GetFileNameWithoutExtension(relPath.Replace('\\', '/'));
    }

    /// <summary>产出一条可区分重载的成员签名（namespace.type 前缀 + 声明）。</summary>
    private static string BuildDeclaration(MemberDeclarationSyntax node, string ns, string parentSig)
    {
        var declaration = node switch
        {
            MethodDeclarationSyntax m => Decl(m.Modifiers, m.ReturnType, m.Identifier.Text, m.ParameterList),
            ConstructorDeclarationSyntax c => Decl(c.Modifiers, null, c.Identifier.Text, c.ParameterList),
            PropertyDeclarationSyntax p => Decl(p.Modifiers, p.Type, p.Identifier.Text, null),
            EventDeclarationSyntax ev => Decl(ev.Modifiers, ev.Type, ev.Identifier.Text, null),
            _ => string.Empty,
        };
        return $"{parentSig}.{declaration}".Trim();
    }

    private static string Decl(SyntaxTokenList mods, TypeSyntax? ret, string name, ParameterListSyntax? paramList)
    {
        var modsStr = string.Join(" ", mods.Select(m => m.Text));
        var retStr = ret?.ToString() ?? string.Empty;
        string sig = paramList is null
            ? name
            : paramList.Parameters.Count == 0
                ? $"{name}()"
                : $"{name}({string.Join(", ", paramList.Parameters.Select(p => $"{p.Type} {p.Identifier.Text}"))})";
        return $"{(modsStr.Length > 0 ? modsStr + " " : "")}{retStr}{(retStr.Length > 0 ? " " : "")}{sig}".Trim();
    }

    private static string BuildFieldSignature(FieldDeclarationSyntax field, string parentSig, VariableDeclaratorSyntax variable)
    {
        var mods = string.Join(" ", field.Modifiers.Select(m => m.Text));
        var init = variable.Initializer?.ToFullString() ?? string.Empty;
        return $"{parentSig} {mods} {field.Declaration!.Type} {variable.Identifier.Text}{init}".Trim();
    }

    private static (int Start, int End) GetLines(SourceText sourceText, TextSpan span)
    {
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        return (lineSpan.Start.Line + 1, lineSpan.End.Line + 1);
    }

    /// <summary>沿用旧 TextUtilities.BuildPreview：清洗 + Rune 安全截断至 200 字符。</summary>
    private static string BuildPreview(string text, int maxLength = SymbolEntry.MaxPreviewLength)
        => SourcePreview.Make(text, maxLength);
}