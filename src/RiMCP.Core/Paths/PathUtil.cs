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

    /// <summary>默认索引目录名（相对 RiMCP_v2 根）。</summary>
    public const string DefaultIndexDirName = "index";

    /// <summary>覆盖索引目录名的环境变量。</summary>
    public const string IndexDirEnvVar = "RIMCP_INDEX_DIR";

    /// <summary>Launcher 独立版发布目录名（位于 RiMCP_v2 根目录下）。</summary>
    public const string PublishedAppDirName = "app";

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
    /// 从 Launcher 所在目录推断 RiMCP_v2 根目录：
    /// <para>1) 目录名为 <see cref="PublishedAppDirName"/>（发布版 exe 位于 <c>&lt;root&gt;/app</c>）时，
    /// 直接取其父目录作为根；</para>
    /// <para>2) 否则从该目录向上查找同时含 <c>RimWorldData</c> 与 <c>models</c> 的目录
    /// （覆盖 <c>dotnet run</c> 的 bin/Release/net8.0-windows 等开发场景）。</para>
    /// 未识别时返回 null，由调用方决定兜底，不抛异常。
    /// </summary>
    public static string? LocateRootFromAppDirectory(string appDir)
    {
        var dir = Path.GetFullPath(appDir).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (dir.Length == 0) return null;

        if (string.Equals(Path.GetFileName(dir), PublishedAppDirName, StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(dir)?.FullName;

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (Directory.Exists(Path.Combine(dir, RimWorldDataDir))
                && Directory.Exists(Path.Combine(dir, ModelsDir)))
                return dir;

            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    /// <summary>
    /// 解析索引目录的绝对路径。优先级：<paramref name="cliDirName"/> &gt;
    /// 环境变量 <see cref="IndexDirEnvVar"/> &gt; <see cref="DefaultIndexDirName"/>。
    /// <para>绝对路径直接返回；相对路径拼接到 <paramref name="rootAbs"/>（RiMCP_v2 根）。
    /// 所有索引产物（build.json、分片目录、embedding-cache.tsv、.build/ 检查点）都必须
    /// 基于本方法的返回值拼接，严禁硬编码 <c>"index"</c>。</para>
    /// </summary>
    public static string GetIndexDirectory(string rootAbs, string? cliDirName = null)
    {
        string? dirName = string.IsNullOrWhiteSpace(cliDirName)
            ? Environment.GetEnvironmentVariable(IndexDirEnvVar)
            : cliDirName;
        if (string.IsNullOrWhiteSpace(dirName)) dirName = DefaultIndexDirName;

        dirName = dirName.Trim();
        return Path.IsPathRooted(dirName)
            ? Path.GetFullPath(dirName)
            : Path.GetFullPath(Path.Combine(Path.GetFullPath(rootAbs), dirName));
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