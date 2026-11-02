namespace RiMCP.McpServer.Services;

/// <summary>
/// 索引加载模式。mmap：向量与图按需读盘（默认，启动快、常驻内存小）；
/// full：向量与 Lucene 整份读入堆内存，牺牲启动时间与 RSS 换取查询零页中断。
/// </summary>
internal static class LoadModes
{
    public const string Mmap = "mmap";
    public const string Full = "full";

    /// <summary>
    /// 解析加载模式。优先级：CLI <c>--load-mode</c>（支持 <c>=值</c> 形式）&gt;
    /// 环境变量 <c>RIMCP_LOAD_MODE</c> &gt; 默认 <c>mmap</c>。
    /// </summary>
    public static string Resolve(string[] args)
    {
        string? cli = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--load-mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) cli = args[i + 1];
            }
            else if (a.StartsWith("--load-mode=", StringComparison.OrdinalIgnoreCase))
            {
                cli = a["--load-mode=".Length..];
            }
        }

        string raw = cli ?? Environment.GetEnvironmentVariable("RIMCP_LOAD_MODE") ?? Mmap;
        return Normalize(raw);
    }

    /// <summary>规范化取值；空值视为默认；非法值抛出并附可选项。</summary>
    public static string Normalize(string? value)
    {
        string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (mode.Length == 0) return Mmap;
        if (mode == Mmap || mode == Full) return mode;
        throw new ArgumentException($"无效的 load-mode “{value}”，可选 {Mmap} 或 {Full}。");
    }
}
