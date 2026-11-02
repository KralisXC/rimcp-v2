namespace RiMCP.Core.Paths;

/// <summary>
/// 根目录自发现与相对路径解析。所有路径统一正斜杠处理、反斜杠归一化，
/// 并校验相对路径不含 <c>..</c>、不得越出根目录。
/// </summary>
public static class PathUtil
{
    private const string RimWorldDataDir = "RimWorldData";
    private const string ModelsDir = "models";
    private const int MaxDepth = 64;

    /// <summary>将反斜杠归一化为正斜杠（不做大小写转换，保留原始大小写）。</summary>
    public static string Normalize(string path)
        => (path ?? string.Empty).Replace('\\', '/').Trim();

    /// <summary>
    /// 从 index 目录向上查找同时包含 <c>RimWorldData</c> 与 <c>models</c> 的根目录。
    /// 从 indexDir 自身开始逐级上溯，至多 <see cref="MaxDepth"/> 层；未找到抛出
    /// <see cref="InvalidOperationException"/>。
    /// </summary>
    public static string LocateRoot(string indexDir)
    {
        var dir = Path.GetFullPath(indexDir);
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (Directory.Exists(Path.Combine(dir, RimWorldDataDir))
                && Directory.Exists(Path.Combine(dir, ModelsDir)))
                return dir;

            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        throw new InvalidOperationException(
            $"在 {indexDir} 向上 {MaxDepth} 层内未找到同时含 {RimWorldDataDir} 与 {ModelsDir} 的根目录。");
    }

    /// <summary>
    /// 将相对根目录的路径解析为绝对路径；校验不含 <c>..</c>、不以 <c>/</c> 开头，
    /// 且最终落在根目录之内（否则视为损坏/越界）。
    /// </summary>
    public static string Resolve(string root, string rel)
    {
        if (string.IsNullOrEmpty(rel)) return Path.GetFullPath(root);

        var rootFull = Path.GetFullPath(root);
        var relNorm = Normalize(rel).TrimStart('/');

        if (relNorm.Split('/').Contains("..", StringComparer.Ordinal))
            throw new InvalidDataException($"相对路径 “{rel}” 包含 ..，已拒绝。");

        var combined = Path.GetFullPath(Path.Combine(rootFull, relNorm));
        if (!IsWithin(rootFull, combined))
            throw new InvalidDataException($"相对路径 “{rel}” 越出根目录 {rootFull}，已拒绝。");
        return combined;
    }

    /// <summary>判断 <paramref name="candidate"/> 是否位于 <paramref name="root"/> 之内（含等于根）。</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var candFull = Path.GetFullPath(candidate);
        return candFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
               || candFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>将语义化的相对路径转为能被 <see cref="Resolve"/> 接受的规范形式（去 ./ 空段、正斜杠）。</summary>
    public static string ToRelForm(string path)
    {
        var norm = Normalize(path).Trim('/');
        var segments = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments);
    }
}