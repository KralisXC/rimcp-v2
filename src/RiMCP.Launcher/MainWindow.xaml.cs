using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RiMCP.Core.Indexing;
using RiMCP.Launcher.Services;
using RiMCP.Launcher.ViewModels;

namespace RiMCP.Launcher;

public partial class MainWindow : Window
{
    private static readonly string[] ProviderOptions =
    {
        LauncherConfig.FollowEnvironment, "cuda", "directml", "cpu",
    };

    private const int MaxLogLines = 3000;

    private readonly LauncherConfig _config = LauncherConfig.Load();
    private readonly ProcessRunner _buildRunner = new();
    private readonly ProcessRunner _mcpRunner = new();
    private readonly ObservableCollection<ShardRow> _rows = new();
    private readonly ObservableCollection<ProbeItem> _probeItems = new();
    private readonly StringBuilder _logBuffer = new();

    private Paragraph? _logParagraph;
    private int _logLineCount;
    private bool _logAtBottom = true;
    private bool _autoScrollPending;

    private EnvironmentStatus? _env;
    private ShardStatusResult? _status;
    private bool _buildRunning;
    private bool _providerReady;
    private bool _indexDirReady;
    private bool _mcpStopping;
    private Process? _mcpProcess;

    public MainWindow()
    {
        InitializeComponent();

        ShardGrid.ItemsSource = _rows;
        ShardList.ItemsSource = _rows;
        EnvItems.ItemsSource = _probeItems;

        LogBox.Document = new FlowDocument { PagePadding = new Thickness(4, 2, 4, 2) };
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogScrollChanged));

        ProviderCombo.ItemsSource = ProviderOptions;
        ProviderCombo.SelectedItem = ProviderOptions.Contains(_config.LastProvider)
            ? _config.LastProvider
            : LauncherConfig.FollowEnvironment;
        _providerReady = true;

        Loaded += async (_, _) =>
        {
            RefreshEnvironment();
            await RefreshStatusAsync();
        };
        Closing += MainWindow_Closing;
    }

    // ---------------- 环境与状态 ----------------

    /// <summary>
    /// 用当前 RIMCP_ROOT 下所有 index* 目录刷新索引目录下拉框，并选中已配置项。
    /// 目录不存在也保留选中值，便于用户先输入后构建。
    /// </summary>
    private void RefreshIndexDirOptions()
    {
        _indexDirReady = false;

        string? root = _config.ResolveRoot();
        string current = _config.ResolveIndexDirName() ?? RiMCP.Core.Paths.PathUtil.DefaultIndexDirName;

        var options = root is null
            ? new List<string>()
            : EnvironmentProbe.ScanIndexDirectories(root).ToList();
        if (!options.Any(o => o.Equals(current, StringComparison.OrdinalIgnoreCase)))
            options.Insert(0, current);

        IndexDirCombo.ItemsSource = options;
        string selected = options.FirstOrDefault(o => o.Equals(current, StringComparison.OrdinalIgnoreCase))
                          ?? current;
        IndexDirCombo.SelectedItem = selected;
        IndexDirCombo.Text = selected;
        _indexDirReady = true;
    }

    private async void IndexDir_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_indexDirReady) return;
        if (IndexDirCombo.SelectedItem is string name)
            await ApplyIndexDirAsync(name);
    }

    private async void IndexDir_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyIndexDirAsync(IndexDirCombo.Text);
    }

    /// <summary>切换索引目录：持久化 → 重新环境检测 → 重新诊断（状态表/建议条/按钮全部联动）。</summary>
    private async Task ApplyIndexDirAsync(string? dirName)
    {
        dirName = dirName?.Trim();
        if (string.IsNullOrEmpty(dirName)) return;
        if (dirName.Equals(_config.LastIndexDir, StringComparison.OrdinalIgnoreCase)) return;

        _config.LastIndexDir = dirName;
        _config.Save();
        AppendLog($"[launcher] 索引目录 = {dirName}");

        RefreshEnvironment();
        await RefreshStatusAsync();
    }

    private void RefreshEnvironment()
    {
        RefreshIndexDirOptions();
        _env = EnvironmentProbe.Probe(_config);
        _probeItems.Clear();
        foreach (var item in _env.Items) _probeItems.Add(item);

        StatusNote.Text = _env.Root is null
            ? "未配置 RIMCP_ROOT：请设置环境变量，或在 appsettings.json 的 lastRoot 填写 RiMCP_v2 根目录。"
            : string.Empty;

        if (_env.Root is null) ApplyAdvice(null);
        UpdateButtons();
    }

    private async Task RefreshStatusAsync()
    {
        if (_env is null) return;

        BtnRefresh.IsEnabled = false;
        StageText.Text = "正在比对分片状态…";

        var env = _env;
        bool forceDeep = ForceDeepCheck.IsChecked == true;
        ShardStatusResult result;
        try
        {
            result = await Task.Run(() => EnvironmentProbe.LoadShardStatus(env, forceDeep));
        }
        catch (Exception ex)
        {
            result = new ShardStatusResult
            {
                Rows = Array.Empty<ShardRow>(),
                GraphOnlyAllowed = false,
                GraphOnlyReason = "比对失败。",
                Error = ex.Message,
            };
        }

        _status = result;
        _rows.Clear();
        foreach (var row in result.Rows) _rows.Add(row);

        if (env.Root is not null && result.Error is not null)
            StatusNote.Text = result.Error;

        GraphOnlyNote.Text = result.Advice?.Overall switch
        {
            ChangeKind.None => "索引与当前环境一致，无需重建全局图。",
            ChangeKind.TextOnly => "按建议可仅重建全局图（文本内容未变化）。",
            ChangeKind.Incompatible => "索引不兼容，只能执行全量重建。",
            _ => result.GraphOnlyReason,
        };
        ApplyAdvice(result.Advice);
        StageText.Text = $"就绪（{_rows.Count} 个分片）";
        if (!string.IsNullOrEmpty(result.ProbeLog)) AppendLog(result.ProbeLog);
        BtnRefresh.IsEnabled = !_buildRunning;
        UpdateButtons();
    }

    /// <summary>展示 UpdateAdvisor 建议：只做颜色/文案映射，不做任何比对判断。</summary>
    private void ApplyAdvice(UpdateAdvice? advice)
    {
        if (advice is null)
        {
            AdviceBanner.Visibility = Visibility.Collapsed;
            BtnAdvice.IsEnabled = false;
            return;
        }

        var (background, foreground) = advice.Overall switch
        {
            ChangeKind.None => (Hex("#DCFCE7"), Hex("#166534")),
            ChangeKind.TextOnly => (Hex("#DBEAFE"), Hex("#1E3A8A")),
            ChangeKind.ContentChanged => (Hex("#FEF3C7"), Hex("#92400E")),
            ChangeKind.LayoutChanged or ChangeKind.Missing => (Hex("#FFEDD5"), Hex("#9A3412")),
            ChangeKind.Incompatible => (Hex("#FEE2E2"), Hex("#991B1B")),
            _ => (Hex("#F3F4F6"), Hex("#374151")),
        };

        AdviceBanner.Visibility = Visibility.Visible;
        AdviceBanner.Background = background;
        AdviceText.Foreground = foreground;
        AdviceText.Text = advice.Summary;
        BtnAdvice.ToolTip = $"将执行：{advice.RecommendedAction}";
    }

    private static SolidColorBrush Hex(string value)
        => new((Color)ColorConverter.ConvertFromString(value));

    private void UpdateButtons()
    {
        bool envReady = _env is { CanBuild: true };
        bool hasIndex = _env is { HasIndex: true };
        bool idle = !_buildRunning;

        var advice = _status?.Advice;
        var overall = advice?.Overall;

        // 建议门控：None 禁增量/仅图谱；TextOnly 只开仅图谱；其余布局/内容变化只开增量类；
        // Incompatible 只留全量；无建议时沿用原有门控。
        bool allowIncremental = overall is null
                                or ChangeKind.ContentChanged
                                or ChangeKind.LayoutChanged
                                or ChangeKind.Missing;
        bool allowGraphOnly = overall is null
            ? (_status?.GraphOnlyAllowed ?? false)
            : overall == ChangeKind.TextOnly;

        BtnFull.IsEnabled = envReady && idle;
        BtnIncremental.IsEnabled = envReady && hasIndex && idle && allowIncremental;
        BtnVectors.IsEnabled = envReady && hasIndex && idle && allowIncremental;
        BtnLucene.IsEnabled = envReady && hasIndex && idle && allowIncremental;
        BtnSelected.IsEnabled = envReady && hasIndex && idle && allowIncremental;
        BtnGraphOnly.IsEnabled = envReady && hasIndex && idle && allowGraphOnly;
        BtnAdvice.IsEnabled = envReady && hasIndex && idle
                              && advice is { Overall: not ChangeKind.None };

        Button? highlighted = overall switch
        {
            ChangeKind.TextOnly => BtnGraphOnly,
            ChangeKind.ContentChanged or ChangeKind.LayoutChanged or ChangeKind.Missing => BtnIncremental,
            ChangeKind.Incompatible => BtnFull,
            _ => null,
        };
        ApplyHighlight(highlighted);

        BtnCancel.IsEnabled = _buildRunning;
        BtnRefresh.IsEnabled = idle;
        ShardList.IsEnabled = idle;

        bool mcpRunning = _mcpRunner.IsRunning;
        BtnStartMcp.IsEnabled = envReady && idle && !mcpRunning;
        BtnStopMcp.IsEnabled = mcpRunning;
        BtnOpenMcpConfig.IsEnabled = envReady;
        BtnOpenIndex.IsEnabled = _env?.IndexDir is not null && Directory.Exists(_env.IndexDir);
    }

    /// <summary>推荐按钮高亮；其余按钮恢复默认主题样式。</summary>
    private void ApplyHighlight(Button? highlighted)
    {
        var style = (Style)FindResource("HighlightButtonStyle");
        foreach (var button in new[]
                 {
                     BtnFull, BtnIncremental, BtnGraphOnly, BtnVectors, BtnLucene, BtnSelected,
                 })
        {
            if (ReferenceEquals(button, highlighted)) button.Style = style;
            else button.ClearValue(FrameworkElement.StyleProperty);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshEnvironment();
        _ = RefreshStatusAsync();
    }

    private async void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_providerReady) return;
        _config.LastProvider = ProviderCombo.SelectedItem as string ?? LauncherConfig.FollowEnvironment;
        _config.Save();
        AppendLog($"[launcher] provider = {_config.ResolveProvider()}");
        RefreshEnvironment();
        await RefreshStatusAsync();
    }

    private void EnvItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProbeItem item }) return;

        if (!string.IsNullOrWhiteSpace(item.OpenPath) && (File.Exists(item.OpenPath) || Directory.Exists(item.OpenPath)))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.OpenPath}\"") { UseShellExecute = true });
                return;
            }
            catch
            {
                // 打不开资源管理器时退化为提示框。
            }
        }

        string text = item.Detail;
        if (!string.IsNullOrWhiteSpace(item.Hint)) text += "\n\n" + item.Hint;
        MessageBox.Show(this, text, item.Title, MessageBoxButton.OK,
            item.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ---------------- 构建 ----------------

    private async void Full_Click(object sender, RoutedEventArgs e)
    {
        if (_env is null) return;
        if (MessageBox.Show(this, "将清空所有缓存并重新嵌入，耗时可能 30 分钟以上。确定继续？",
                "全量重建确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunBuildAsync(BuildOperation.Full);
    }

    private async void Incremental_Click(object sender, RoutedEventArgs e)
        => await RunBuildAsync(BuildOperation.Incremental);

    private async void GraphOnly_Click(object sender, RoutedEventArgs e)
        => await RunBuildAsync(BuildOperation.GraphOnly);

    /// <summary>一键执行：建议 → 构建模式，直接触发。</summary>
    private async void Advice_Click(object sender, RoutedEventArgs e)
    {
        if (_status?.Advice is not { } advice || advice.Overall == ChangeKind.None) return;

        var operation = advice.Overall switch
        {
            ChangeKind.TextOnly => BuildOperation.GraphOnly,
            ChangeKind.Incompatible => BuildOperation.Full,
            _ => BuildOperation.Incremental,
        };

        AppendLog($"[launcher] 一键执行：{advice.RecommendedAction}（{advice.Overall}）");
        await RunBuildAsync(operation);
    }

    private async void VectorsOnly_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("[launcher] 仅嵌入：Indexer 无单层 CLI，按“勾选分片 + 增量”执行（未变分片自动跳过）。");
        await RunBuildAsync(BuildOperation.Incremental);
    }

    private async void LuceneOnly_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("[launcher] 仅 Lucene：Indexer 无单层 CLI，按“勾选分片 + 增量”执行（未变分片自动跳过）。");
        await RunBuildAsync(BuildOperation.Incremental);
    }

    private async void Selected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Shard).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先勾选至少一个分片。", "按分片构建", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunBuildAsync(BuildOperation.Incremental);
    }

    private async Task RunBuildAsync(BuildOperation operation)
    {
        if (_env?.Root is null || _env.IndexDir is null || _env.ModelDir is null
            || _env.IndexDirName is null) return;

        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Shard).ToList();
        var request = BuildRequest.Create(
            operation, _config, _env.Root, _env.IndexDirName, _env.ModelDir, selected);

        _buildRunning = true;
        BuildProgress.Visibility = Visibility.Visible;
        StageText.Text = "启动构建…";
        UpdateButtons();

        AppendLog($"=== {request.Describe()} ===");

        int exitCode = await _buildRunner.RunBuildAsync(request, (line, isError) =>
            Dispatcher.BeginInvoke(() => AppendLog(line, isError)));

        _buildRunning = false;
        BuildProgress.Visibility = Visibility.Collapsed;

        if (_buildRunner.WasCanceled)
        {
            AppendLog("[launcher] 已取消，检查点已保留，下次可续跑。", true);
            StageText.Text = "已取消";
        }
        else if (exitCode == 0)
        {
            AppendLog("[launcher] 构建完成（exit 0）。", false);
            StageText.Text = "构建完成";
            // 构建成功后强制刷新一次：顶部环境条与状态表立即翻转，无需用户手动点“刷新状态”。
            RefreshEnvironment();
        }
        else
        {
            AppendLog($"[launcher] 构建失败（exit {exitCode}），完整输出已保留。", true);
            StageText.Text = "构建失败";
        }

        UpdateButtons();
        await RefreshStatusAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _buildRunner.Cancel();
        AppendLog("[launcher] 正在终止构建进程树…", true);
    }

    // ---------------- MCP ----------------

    private void StartMcp_Click(object sender, RoutedEventArgs e)
    {
        if (_env is null || _env.Root is null) return;
        _mcpStopping = false;
        try
        {
            _mcpProcess = _mcpRunner.StartMcp(
                _env,
                (line, isError) =>
                {
                    if (isError)
                        Dispatcher.BeginInvoke(() => AppendLog(line, true));
                },
                code => Dispatcher.BeginInvoke(() => OnMcpExited(code)));

            McpStatus.Text = $"运行中 PID {_mcpProcess.Id}";
            AppendLog("[launcher] MCP 已启动（stdio）。");
        }
        catch (Exception ex)
        {
            McpStatus.Text = "启动失败";
            MessageBox.Show(this, ex.Message, "启动 MCP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateButtons();
    }

    private void StopMcp_Click(object sender, RoutedEventArgs e)
    {
        _mcpStopping = true;
        _mcpRunner.Cancel();
        AppendLog("[launcher] 正在停止 MCP 进程树…", true);
    }

    private void OnMcpExited(int exitCode)
    {
        _mcpProcess = null;
        McpStatus.Text = _mcpStopping || exitCode == 0 ? "已停止" : $"异常退出（exit {exitCode}）";
        AppendLog($"[launcher] MCP 已退出（exit {exitCode}）。", !_mcpStopping && exitCode != 0);
        _mcpStopping = false;
        UpdateButtons();
    }

    private void OpenMcpConfig_Click(object sender, RoutedEventArgs e)
        => OpenInExplorer(_env?.IndexDir);

    private void OpenIndex_Click(object sender, RoutedEventArgs e)
        => OpenInExplorer(_env?.IndexDir);

    private static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_buildRunner.IsRunning)
        {
            if (MessageBox.Show(this, "构建仍在进行，是否终止构建并退出？", "退出确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _buildRunner.Cancel();
        }

        if (_mcpRunner.IsRunning)
        {
            if (MessageBox.Show(this, "MCP 仍在运行，是否一并停止并退出？", "退出确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _mcpRunner.Cancel();
        }
    }

    // ---------------- 日志 ----------------

    private void AppendLog(string text, bool isError = false)
    {
        if (_logParagraph is null)
        {
            _logParagraph = new Paragraph { Margin = new Thickness(0) };
            LogBox.Document.Blocks.Add(_logParagraph);
        }

        _logParagraph.Inlines.Add(new Run(text)
        {
            Foreground = isError ? Brushes.Firebrick : Brushes.Black,
        });
        _logParagraph.Inlines.Add(new LineBreak());
        _logLineCount++;

        while (_logLineCount > MaxLogLines)
        {
            var first = _logParagraph.Inlines.FirstInline;
            if (first is null) break;
            _logParagraph.Inlines.Remove(first);
            if (_logParagraph.Inlines.FirstInline is LineBreak lineBreak)
                _logParagraph.Inlines.Remove(lineBreak);
            _logLineCount--;
        }

        _logBuffer.AppendLine(text);
        RequestAutoScroll();
        UpdateStage(text);
    }

    /// <summary>仅当“自动滚动”勾选且用户当前停在日志底部时才滚到底，避免打断向上翻阅。</summary>
    private void RequestAutoScroll()
    {
        if (AutoScrollCheck.IsChecked != true || !_logAtBottom || _autoScrollPending) return;

        _autoScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _autoScrollPending = false;
            LogBox.ScrollToEnd();
            _logAtBottom = true;
        }));
    }

    /// <summary>内容高度变化（追加文本）不算用户滚动；只有用户拖动时更新“是否在底部”。</summary>
    private void LogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
            _logAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4;
    }

    private void UpdateStage(string line)
    {
        if (line.Contains("[embed]") || line.Contains("新向量") || line.Contains("嵌入"))
            StageText.Text = "嵌入计算";
        else if (line.Contains("Lucene") || line.Contains("lucene"))
            StageText.Text = "Lucene";
        else if (line.Contains("[graph]") || line.Contains("统一构图"))
            StageText.Text = "统一构图";
        else if (line.Contains("解析") || line.Contains("符号"))
            StageText.Text = "解析 / 符号提取";
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_logBuffer.ToString());
        }
        catch
        {
            MessageBox.Show(this, "复制日志失败（剪贴板被占用）。", "复制日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
