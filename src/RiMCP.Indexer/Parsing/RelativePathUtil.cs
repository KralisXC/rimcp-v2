using RiMCP.Core.Paths;

namespace RiMCP.Indexer.Parsing;

/// <summary>
/// 绝对路径 → 相对 RimWorldData 根 的规约工具。扩散自文件扫描，统一返回正斜杠、无 ".." 的相对路径。
/// </summary>
public static class RelativePathUtil
{
    /// <summary>
    /// 将绝对路径转为相对 root 的规范相对路径（正斜杠、无 ".."、无前导 "/"）。
    /// 若无法相对（越出 root），抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static string ToRel(string rootAbs, string fileAbs)
    {
        var root = Path.GetFullPath(rootAbs);
        var file = Path.GetFullPath(fileAbs);

        string rel = Path.GetRelativePath(root, file);
        // GetRelativePath 可能产出 "..\\..."；这里不允许越界
        var segments = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains("..", StringComparer.Ordinal))
            throw new InvalidDataException($"文件 {fileAbs} 越出根目录 {rootAbs}，无法转相对路径。");

        return string.Join('/', segments);
    }
}