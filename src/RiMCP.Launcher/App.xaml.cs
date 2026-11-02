using System.Windows;
using RiMCP.Launcher.Services;

namespace RiMCP.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 发布版 exe 在 <root>/app 下运行：启动即把父目录写入 RIMCP_ROOT，
        // 保证顶部环境探针与子进程都拿到同一份根路径。
        LauncherConfig.EnsureRootEnvironmentVariable();
        base.OnStartup(e);
    }
}
