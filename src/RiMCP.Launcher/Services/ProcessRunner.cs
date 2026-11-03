using System.Diagnostics;
using System.IO;
using System.Text;
using RiMCP.Launcher.ViewModels;

namespace RiMCP.Launcher.Services;

/// <summary>
/// 子进程统一入口：构建走 <see cref="RunBuildAsync"/>，MCP 走 <see cref="StartMcp"/>。
/// 所有路径从源码根推导，不硬编码绝对路径。
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// 子进程在重定向 stdio 时按 UTF-8 写出中文，父进程必须用 UTF-8 解码；
    /// 若按系统 OEM 代码页（中文 Windows 为 936）解码，会得到“鐩爣绱㈠紩鐩綍”这类乱码。
    /// </summary>
    private static readonly Encoding ChildOutputEncoding = Encoding.UTF8;

    private readonly object _gate = new();
    private Process? _current;

    /// <summary>当前是否有受管子进程在运行。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _current is { HasExited: false }; }
    }

    /// <summary>最近一次构建是否被用户取消。</summary>
    public bool WasCanceled { get; private set; }

    /// <summary>最近一次受管进程的 PID（无则 null）。</summary>
    public int? CurrentPid
    {
        get { lock (_gate) return _current is { HasExited: false } ? _current.Id : null; }
    }

    /// <summary>启动一次 Indexer 构建并等待结束，返回退出码。</summary>
    public async Task<int> RunBuildAsync(BuildRequest request, Action<string, bool> onLine)
    {
        WasCanceled = false;

        string? sourceRoot = EnvironmentProbe.LocateSourceRoot();
        if (sourceRoot is null)
        {
            onLine("找不到源码根目录（src/RiMCP.Indexer）。请设置 RIMCP_SOURCE_ROOT 指向 RiMCP_v2_3 仓库根目录。", true);
            return -1;
        }

        var (fileName, args) = ResolveIndexer(sourceRoot, request.ToArguments());
        onLine($"[launcher] {fileName} {string.Join(' ', args)}", false);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = sourceRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ChildOutputEncoding,
            StandardErrorEncoding = ChildOutputEncoding,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["RIMCP_ROOT"] = request.RootDir;
        psi.Environment["RIMCP_MODEL_PATH"] = request.ModelDir;
        psi.Environment["RIMCP_ONNX_PROVIDER"] = request.Provider;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        AttachReaders(process, onLine);

        lock (_gate) _current = process;
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            onLine($"[launcher] 启动失败：{ex.Message}", true);
            return -1;
        }
        finally
        {
            lock (_gate) _current = null;
        }
    }

    /// <summary>
    /// 启动 MCP 服务器（stdio），不等待退出；返回 Process 供 UI 观察 Exited。
    /// </summary>
    public Process StartMcp(EnvironmentStatus env, Action<string, bool> onLine, Action<int> onExit)
    {
        string? sourceRoot = EnvironmentProbe.LocateSourceRoot();
        if (sourceRoot is null)
            throw new InvalidOperationException(
                "找不到源码根目录（src/RiMCP.McpServer）。请设置 RIMCP_SOURCE_ROOT。");
        if (env.Root is null || env.ModelDir is null)
            throw new InvalidOperationException("未配置 RIMCP_ROOT / 模型目录，无法启动 MCP。");

        var (fileName, args) = ResolveMcp(sourceRoot);
        if (!string.IsNullOrWhiteSpace(env.IndexDirName))
        {
            args.Add("--index-dir");
            args.Add(env.IndexDirName);
        }
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = sourceRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ChildOutputEncoding,
            StandardErrorEncoding = ChildOutputEncoding,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["RIMCP_ROOT"] = env.Root;
        psi.Environment["RIMCP_MODEL_PATH"] = env.ModelDir;
        psi.Environment["RIMCP_ONNX_PROVIDER"] = env.Provider;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        AttachReaders(process, onLine);
        process.Exited += (_, _) => onExit(process.ExitCode);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate) _current = process;
        return process;
    }

    /// <summary>终止当前受管进程树（构建或 MCP）。</summary>
    public void Cancel()
    {
        Process? process;
        lock (_gate) process = _current;
        if (process is null) return;

        WasCanceled = true;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已退出或无权结束；忽略。
        }
    }

    private static void AttachReaders(Process process, Action<string, bool> onLine)
    {
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data, false); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data, true); };
    }

    /// <summary>
    /// 优先用 dotnet run 按源码增量编译，避免旧目录里残留的 Release DLL（例如升级前的 GBK 输出）
    /// 被直接复用；仅在源码缺失时才回退到已编译 DLL。
    /// </summary>
    private static (string FileName, List<string> Args) ResolveIndexer(string sourceRoot, List<string> buildArgs)
    {
        string projectDir = Path.Combine(sourceRoot, "src", "RiMCP.Indexer");
        string csproj = Path.Combine(projectDir, "RiMCP.Indexer.csproj");
        if (File.Exists(csproj))
        {
            var args = new List<string> { "run", "-c", "Release", "--project", csproj, "--" };
            args.AddRange(buildArgs);
            return ("dotnet", args);
        }

        string dll = Path.Combine(projectDir, "bin", "Release", "net8.0", "RiMCP.Indexer.dll");
        return ("dotnet", new List<string> { dll }.Concat(buildArgs).ToList());
    }

    /// <summary>与 <see cref="ResolveIndexer"/> 同理，优先按源码增量编译，避免复用旧 MCP 二进制。</summary>
    private static (string FileName, List<string> Args) ResolveMcp(string sourceRoot)
    {
        string projectDir = Path.Combine(sourceRoot, "src", "RiMCP.McpServer");
        string csproj = Path.Combine(projectDir, "RiMCP.McpServer.csproj");
        if (File.Exists(csproj))
            return ("dotnet", new List<string> { "run", "-c", "Release", "--project", csproj });

        string dll = Path.Combine(projectDir, "bin", "Release", "net8.0", "RiMCP.McpServer.dll");
        return ("dotnet", new List<string> { dll });
    }
}
