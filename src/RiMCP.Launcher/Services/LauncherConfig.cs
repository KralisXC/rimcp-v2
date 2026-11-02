using System.IO;
using System.Text.Json;
using RiMCP.Core.Paths;

namespace RiMCP.Launcher.Services;

/// <summary>
/// Launcher 本地偏好（仅 UI 选择，不含分片/索引路径推导）。
/// 读取顺序：环境变量优先，其次 appsettings.json（存放在 RiMCP_v2 根目录）。
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>可选的 provider 下拉项：跟随环境变量 + 三种显式值。</summary>
    public const string FollowEnvironment = "跟随环境变量";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>上次使用的 RiMCP_v2 根目录（4.5 允许作为 RIMCP_ROOT 的兜底）。</summary>
    public string LastRoot { get; set; } = string.Empty;

    /// <summary>下拉选择的 provider（cuda / directml / cpu / 跟随环境变量）。</summary>
    public string LastProvider { get; set; } = "cuda";

    /// <summary>构建批大小。</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>
    /// 上次选中的索引目录名（如 <c>index_17</c>）。为空时回退环境变量
    /// <c>RIMCP_INDEX_DIR</c>，再回退默认 <c>index</c>。
    /// </summary>
    public string LastIndexDir { get; set; } = string.Empty;

    /// <summary>
    /// 识别 RiMCP_v2 根目录：发布版 exe 位于 <c>&lt;root&gt;/app</c> 时取其父目录；
    /// 开发运行时向上查找含 <c>RimWorldData</c> 与 <c>models</c> 的目录。未识别时返回 null。
    /// </summary>
    public static string? DetectDefaultRoot()
        => PathUtil.LocateRootFromAppDirectory(AppContext.BaseDirectory);

    /// <summary>
    /// 启动期补全 RIMCP_ROOT：仅在环境变量缺失时，把识别到的根目录写入当前进程，
    /// 使环境探针与 Launcher 启动的子进程（Indexer / McpServer）使用同一根路径。
    /// </summary>
    public static string? EnsureRootEnvironmentVariable()
    {
        var existing = Environment.GetEnvironmentVariable("RIMCP_ROOT");
        if (!string.IsNullOrWhiteSpace(existing)) return existing.Trim();

        var detected = DetectDefaultRoot();
        if (detected is not null)
            Environment.SetEnvironmentVariable("RIMCP_ROOT", detected);
        return detected;
    }

    /// <summary>appsettings.json 路径：RiMCP_v2 根目录（与发布目录 app/ 区分开）。</summary>
    public static string FilePath => Path.Combine(
        DetectDefaultRoot() ?? AppContext.BaseDirectory, "appsettings.json");

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
            else
            {
                // 首次运行：在根目录生成默认配置，便于用户直接编辑。
                new LauncherConfig().Save();
            }
        }
        catch
        {
            // 偏好文件损坏/根目录只读时使用默认值，不影响启动。
        }
        return new LauncherConfig();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 根目录可能只读；偏好保存失败不阻塞 UI。
        }
    }

    /// <summary>RiMCP_v2 根目录：RIMCP_ROOT → lastRoot → 自动识别；都没有返回 null。</summary>
    public string? ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("RIMCP_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (!string.IsNullOrWhiteSpace(LastRoot)) return LastRoot.Trim();
        return DetectDefaultRoot();
    }

    /// <summary>
    /// 当前选中的索引目录名：lastIndexDir → RIMCP_INDEX_DIR → null（由 PathUtil 回退默认值）。
    /// </summary>
    public string? ResolveIndexDirName()
    {
        if (!string.IsNullOrWhiteSpace(LastIndexDir)) return LastIndexDir.Trim();
        var env = Environment.GetEnvironmentVariable(RiMCP.Core.Paths.PathUtil.IndexDirEnvVar);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>实际生效的 provider：下拉值或环境变量（跟随环境变量时走 Core 的解析）。</summary>
    public string ResolveProvider()
    {
        if (LastProvider is FollowEnvironment or "")
            return RiMCP.Core.Support.OnnxProviderResolver.Resolve();
        return LastProvider.Trim().ToLowerInvariant();
    }

    /// <summary>构建/MCP 启动时传给子进程的值；跟随环境变量时返回 null（不覆盖）。</summary>
    public string? ProviderOverride()
        => LastProvider is FollowEnvironment or "" ? null : LastProvider.Trim().ToLowerInvariant();
}
