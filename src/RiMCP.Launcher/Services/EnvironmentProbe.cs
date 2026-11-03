using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RiMCP.Core.Indexing;
using RiMCP.Core.Paths;
using RiMCP.Core.Storage;
using RiMCP.Launcher.ViewModels;

namespace RiMCP.Launcher.Services;

/// <summary>顶部环境检测的一项。</summary>
public sealed record ProbeItem(string Title, bool Ok, string Detail, string? OpenPath, string? Hint);

/// <summary>一次环境探测的结果。</summary>
public sealed class EnvironmentStatus
{
    public required IReadOnlyList<ProbeItem> Items { get; init; }
    public string? Root { get; init; }
    public string? RimWorldDataDir { get; init; }
    public string? ModelDir { get; init; }

    /// <summary>当前选中的索引目录（绝对路径）。</summary>
    public string? IndexDir { get; init; }

    /// <summary>当前选中的索引目录名（如 index_17），用于传给子进程 --index-dir。</summary>
    public string? IndexDirName { get; init; }

    public required string Provider { get; init; }
    public required bool CanBuild { get; init; }
    public required bool HasModel { get; init; }
    public required bool HasData { get; init; }
    public required bool HasIndex { get; init; }
}

/// <summary>分片状态表的一次刷新结果。</summary>
public sealed class ShardStatusResult
{
    public required IReadOnlyList<ShardRow> Rows { get; init; }
    public required bool GraphOnlyAllowed { get; init; }
    public required string GraphOnlyReason { get; init; }

    /// <summary>一键诊断建议；无法诊断（环境错误）时为 null。</summary>
    public UpdateAdvice? Advice { get; init; }

    /// <summary>本次检测的 [probe] 摘要（命中缓存 / 深度检测 + 耗时）。</summary>
    public string? ProbeLog { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// 环境检测与分片状态加载。比对逻辑一律复用 Core 的
/// <see cref="ShardCatalog.BuildEnvironments"/> 与 <see cref="ManifestComparer"/>，本类只做映射。
/// </summary>
public static class EnvironmentProbe
{
    private const string DefaultEmbeddingModel = "e5-base-v2-onnx";
    private const int DefaultDim = 768;

    /// <summary>探测五项环境（不做 BuildEnvironments，避免启动过重）。</summary>
    public static EnvironmentStatus Probe(LauncherConfig config)
    {
        var items = new List<ProbeItem>();
        string provider = config.ResolveProvider();

        // ---- .NET 8 桌面运行时 ----
        string framework = RuntimeInformation.FrameworkDescription;
        bool dotnetOk = Environment.Version.Major >= 8;
        items.Add(new ProbeItem(
            ".NET 8 桌面运行时", dotnetOk,
            framework, null,
            dotnetOk ? null : "请安装 .NET 8 Desktop Runtime。"));

        // ---- ONNX provider ----
        var providerDetail = ProbeProviders(provider, out bool providerOk);
        items.Add(new ProbeItem(
            "ONNX provider", providerOk, providerDetail, null,
            providerOk ? null : $"所选 provider {provider} 不可用，可切换到 cpu 或使用 DirectML 构建。"));

        string? root = config.ResolveRoot();
        if (root is null)
        {
            items.Add(new ProbeItem("RimWorldData", false, "未配置 RIMCP_ROOT", null,
                "请设置环境变量 RIMCP_ROOT，或在 appsettings.json 的 lastRoot 填写 RiMCP_v2 根目录。"));
            items.Add(new ProbeItem("模型目录", false, "未配置根目录，无法解析模型路径", null, null));
            items.Add(new ProbeItem("当前索引目录", false, "未配置根目录，无法解析索引路径", null, null));
            items.Add(new ProbeItem("index 目录", false, "未配置根目录，无法解析索引路径", null, null));
            return new EnvironmentStatus
            {
                Items = items,
                Root = null,
                Provider = provider,
                CanBuild = false,
                HasModel = false,
                HasData = false,
                HasIndex = false,
            };
        }

        string rootAbs = Path.GetFullPath(root);
        string dataDir = Path.Combine(rootAbs, "RimWorldData");
        string dataDefs = Path.Combine(dataDir, "Data");

        string modelDir = Environment.GetEnvironmentVariable("RIMCP_MODEL_PATH") is { Length: > 0 } modelEnv
            ? Path.GetFullPath(modelEnv)
            : Path.Combine(rootAbs, "models", "e5-base-v2-onnx");

        // 索引目录由 Launcher 选择 / RIMCP_INDEX_DIR / 默认 index 解析，禁止硬编码。
        string? indexDirRef = config.ResolveIndexDirName();
        string indexDir = PathUtil.GetIndexDirectory(rootAbs, indexDirRef);
        string indexDirName = Path.GetFileName(
            indexDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool hasData = Directory.Exists(dataDefs);
        bool hasModel = File.Exists(Path.Combine(modelDir, "model.onnx"))
                        && File.Exists(Path.Combine(modelDir, "tokenizer.json"));
        bool hasIndex = File.Exists(Path.Combine(indexDir, "build.json"));

        items.Add(new ProbeItem("RimWorldData", hasData,
            hasData ? dataDefs : $"缺少 {dataDefs}", hasData ? dataDir : rootAbs,
            hasData ? null : "确认 RIMCP_ROOT 指向 RiMCP_v2 根目录，且 RimWorldData/Data 存在。"));
        items.Add(new ProbeItem("模型目录", hasModel,
            hasModel ? modelDir : $"缺少 model.onnx 或 tokenizer.json（{modelDir}）",
            hasModel ? modelDir : rootAbs,
            hasModel ? null : "设置 RIMCP_MODEL_PATH，或把 e5-base-v2-onnx 放到 <root>/models/ 下。"));
        bool indexDirExists = Directory.Exists(indexDir);
        items.Add(new ProbeItem("当前索引目录", indexDirExists,
            indexDirName, indexDirExists ? indexDir : rootAbs,
            $"完整路径：{indexDir}"));
        items.Add(new ProbeItem("index 目录", hasIndex,
            hasIndex ? Path.Combine(indexDir, "build.json") : $"缺少 {Path.Combine(indexDir, "build.json")}",
            indexDirExists ? indexDir : rootAbs,
            hasIndex ? null : "请先执行一次全量重建。"));

        return new EnvironmentStatus
        {
            Items = items,
            Root = rootAbs,
            RimWorldDataDir = dataDir,
            ModelDir = modelDir,
            IndexDir = indexDir,
            // 传给子进程的引用：选择/环境变量原样传递（可能是目录名或绝对路径），否则用目录名。
            IndexDirName = string.IsNullOrWhiteSpace(indexDirRef) ? indexDirName : indexDirRef.Trim(),
            Provider = provider,
            CanBuild = hasModel && hasData,
            HasModel = hasModel,
            HasData = hasData,
            HasIndex = hasIndex,
        };
    }

    /// <summary>
    /// 重跑分片比对：ShardCatalog.BuildEnvironments + ManifestComparer.Compare，
    /// 结果只映射到 <see cref="ShardRow"/>，Launcher 不自行判断层是否匹配。
    /// </summary>
    /// <param name="forceDeepCheck">true 时忽略 mtime 预检缓存，全量重新哈希（“强制深度检测”）。</param>
    public static ShardStatusResult LoadShardStatus(EnvironmentStatus env, bool forceDeepCheck = false)
    {
        var stopwatch = Stopwatch.StartNew();

        if (env.Root is null || env.RimWorldDataDir is null || env.IndexDir is null)
        {
            return new ShardStatusResult
            {
                Rows = Array.Empty<ShardRow>(),
                GraphOnlyAllowed = false,
                GraphOnlyReason = "未配置 RIMCP_ROOT，无法比对分片状态。",
                Error = "未配置 RIMCP_ROOT。",
            };
        }

        string v2Root = env.Root;
        string dataDir = env.RimWorldDataDir;
        string indexDir = env.IndexDir;

        BuildJson.BuildInfo? build = null;
        string buildPath = Path.Combine(indexDir, "build.json");
        if (File.Exists(buildPath))
        {
            try { build = BuildJson.Load(buildPath); }
            catch (Exception ex)
            {
                return new ShardStatusResult
                {
                    Rows = Array.Empty<ShardRow>(),
                    GraphOnlyAllowed = false,
                    GraphOnlyReason = "build.json 读取失败，请执行全量重建。",
                    Error = $"build.json 读取失败：{ex.Message}",
                };
            }
        }

        IReadOnlyList<ShardSource> sources;
        try
        {
            sources = ShardCatalog.Discover(dataDir, ShardCatalog.DefaultCSharpSubdir);
        }
        catch (Exception ex)
        {
            return new ShardStatusResult
            {
                Rows = Array.Empty<ShardRow>(),
                GraphOnlyAllowed = false,
                GraphOnlyReason = "RimWorldData 不可用，无法比对分片状态。",
                Error = $"分片发现失败：{ex.Message}",
            };
        }

        string gameVersion = build?.GameVersion is { Length: > 0 } gv
            ? gv
            : ShardCatalog.DetectGameVersion(dataDir);
        string model = build?.Model?.Name is { Length: > 0 } mn ? mn : DefaultEmbeddingModel;
        int dim = build?.Dim is > 0 ? build.Dim : DefaultDim;
        string builderCommit = build?.BuilderCommit is { Length: > 0 } bc ? bc : "unknown";

        // precision 取第一个已存在 manifest 的值（Indexer 侧当前固定 fp16）。
        string precision = "fp16";
        if (build is not null)
        {
            foreach (var pointer in build.Shards)
            {
                string manifestPath = PathUtilResolve(v2Root, pointer.ManifestPath);
                if (!File.Exists(manifestPath)) continue;
                try { precision = ShardManifest.Load(manifestPath).Precision; } catch { }
                break;
            }
        }

        IReadOnlyList<ShardCatalog.ShardEnvironment> envs;
        var probeCache = ProbeCache.Load(indexDir, ignoreCache: forceDeepCheck);
        try
        {
            envs = ShardCatalog.BuildEnvironments(
                sources, dataDir, gameVersion, env.Provider, precision, model, dim, builderCommit,
                probeCache);
        }
        catch (Exception ex)
        {
            return new ShardStatusResult
            {
                Rows = Array.Empty<ShardRow>(),
                GraphOnlyAllowed = false,
                GraphOnlyReason = "环境哈希计算失败。",
                Error = $"环境哈希计算失败：{ex.Message}",
            };
        }

        var rows = new List<ShardRow>();
        var pointerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 诊断只做一次：状态表与顶部建议条共用同一份结果。
        var advice = UpdateAdvisor.Diagnose(v2Root, dataDir, indexDir, sources, envs, build);

        stopwatch.Stop();
        probeCache.Save();
        string probeLog = forceDeepCheck || probeCache.RecomputedShards.Count > 0
            ? $"[probe] 执行深度检测 (耗时 {stopwatch.Elapsed.TotalSeconds:F2} s)"
            : $"[probe] 快速检测命中缓存 (耗时 {stopwatch.Elapsed.TotalSeconds:F2} s)";

        var diagnosisByName = advice.Shards
            .GroupBy(d => d.Shard, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        if (build is not null)
        {
            foreach (var pointer in build.Shards.OrderBy(p => p.Index))
            {
                pointerNames.Add(pointer.Name);
                var shardEnv = envs.FirstOrDefault(e =>
                    e.ShardName.Equals(pointer.Name, StringComparison.Ordinal));
                rows.Add(BuildRow(
                    pointer.Name, build.FormatVersion, v2Root, pointer.ManifestPath, shardEnv,
                    diagnosisByName.GetValueOrDefault(pointer.Name)));
            }
        }

        // 当前 RimWorldData 中新增、但 build.json 尚未收录的分片。
        foreach (var shard in sources.OrderBy(s => s.Index))
        {
            if (pointerNames.Contains(shard.Name)) continue;
            var shardEnv = envs.FirstOrDefault(e => e.ShardName.Equals(shard.Name, StringComparison.Ordinal));
            var diagnosis = diagnosisByName.GetValueOrDefault(shard.Name);
            rows.Add(new ShardRow
            {
                Shard = shard.Name,
                IndexVersion = build is null ? "—" : build.FormatVersion.ToString(),
                GameVersion = shardEnv?.GameVersion ?? gameVersion,
                DefsSymbols = "—",
                Vectors = "—",
                Lucene = "—",
                Graph = "—",
                LastBuiltAt = "—",
                State = "缺失",
                Detail = "build.json 未收录该分片，请执行增量构建。",
                ChangeKindText = DiagnosisText(diagnosis),
                ChangeReason = diagnosis?.Reason ?? "build.json 未收录该分片。",
            });
        }

        bool allDefsSymbolsMatch = rows.Count > 0 && rows.All(r => r.DefsSymbols == "✔");
        bool allManifestsV2 = true;
        string manifestReason = string.Empty;
        if (build is not null)
        {
            foreach (var pointer in build.Shards)
            {
                string manifestPath = PathUtilResolve(v2Root, pointer.ManifestPath);
                if (!File.Exists(manifestPath))
                {
                    allManifestsV2 = false;
                    manifestReason = $"分片 {pointer.Name} 的 manifest 缺失，请执行全量重建。";
                    break;
                }
                try
                {
                    if (ShardManifest.Load(manifestPath).FormatVersion != ShardManifest.CurrentFormatVersion)
                    {
                        allManifestsV2 = false;
                        manifestReason = "旧索引不兼容（manifest 版本过低），请执行全量重建。";
                        break;
                    }
                }
                catch
                {
                    allManifestsV2 = false;
                    manifestReason = $"分片 {pointer.Name} 的 manifest 无法解析，请执行全量重建。";
                    break;
                }
            }
        }

        bool cacheExists = File.Exists(Path.Combine(indexDir, EmbeddingCache.FileName));
        bool graphFilesExist = GraphFilesExist(Path.Combine(indexDir, "graph"));

        string reason;
        bool graphOnlyAllowed;
        if (build is null)
        {
            graphOnlyAllowed = false;
            reason = "缺少 build.json，请先执行全量重建。";
        }
        else if (build.FormatVersion != BuildJson.CurrentFormatVersion)
        {
            graphOnlyAllowed = false;
            reason = $"索引格式版本 {build.FormatVersion} 不受支持，请执行全量重建。";
        }
        else if (!allManifestsV2)
        {
            graphOnlyAllowed = false;
            reason = manifestReason;
        }
        else if (!cacheExists)
        {
            graphOnlyAllowed = false;
            reason = "缺少嵌入缓存，无法判断文本是否变化，请执行增量构建。";
        }
        else if (!allDefsSymbolsMatch)
        {
            graphOnlyAllowed = false;
            reason = "Def/源码内容已变化，需要重新嵌入（请先执行增量构建）。";
        }
        else if (!graphFilesExist)
        {
            graphOnlyAllowed = false;
            reason = "缺少 graph 产物，请先执行增量构建。";
        }
        else
        {
            graphOnlyAllowed = true;
            reason = "文本内容未变化，可直接更新全局图。";
        }

        return new ShardStatusResult
        {
            Rows = rows,
            GraphOnlyAllowed = graphOnlyAllowed,
            GraphOnlyReason = reason,
            Advice = advice,
            ProbeLog = probeLog,
        };
    }

    private static ShardRow BuildRow(
        string shard, int formatVersion, string v2Root, string manifestRel,
        ShardCatalog.ShardEnvironment? shardEnv, ShardDiagnosis? diagnosis)
    {
        string manifestPath = PathUtilResolve(v2Root, manifestRel);
        ShardManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            try { manifest = ShardManifest.Load(manifestPath); } catch { manifest = null; }
        }

        string state;
        string detail;
        ShardComparison? comparison = null;

        if (manifest is null)
        {
            state = "缺失";
            detail = shardEnv is null
                ? "索引与当前 RimWorldData 均无该分片。"
                : "manifest 缺失，需重建。";
        }
        else
        {
            comparison = shardEnv is null ? null : ManifestComparer.Compare(manifest, shardEnv);
            if (manifest.Truncated)
            {
                state = "截断";
                detail = "该分片为 --max-entries 截断样本，需全量重建。";
            }
            else if (comparison is null)
            {
                state = "缺失";
                detail = "当前 RimWorldData 中找不到该分片来源，无法比对。";
            }
            else
            {
                state = comparison.IsReady
                    ? "就绪"
                    : comparison.State == ShardState.Missing ? "缺失" : "需重建";
                detail = ManifestComparer.Describe(comparison);
            }
        }

        return new ShardRow
        {
            Shard = shard,
            IndexVersion = formatVersion.ToString(),
            GameVersion = shardEnv?.GameVersion ?? manifest?.GameVersion ?? "unknown",
            DefsSymbols = LayerMark(comparison?.DefsMatch == true && comparison?.SymbolsMatch == true),
            Vectors = LayerMark(comparison?.VectorsMatch == true),
            Lucene = LayerMark(comparison?.LuceneMatch == true),
            Graph = LayerMark(comparison?.GraphMatch == true),
            LastBuiltAt = FormatBuiltAt(manifest?.BuiltAt),
            State = state,
            Detail = detail,
            ChangeKindText = DiagnosisText(diagnosis),
            ChangeReason = diagnosis?.Reason ?? string.Empty,
        };
    }

    /// <summary>ChangeKind → 表格展示文案（仅展示映射，判定在 UpdateAdvisor）。</summary>
    private static string DiagnosisText(ShardDiagnosis? diagnosis) => diagnosis?.Kind switch
    {
        ChangeKind.None => "无变化",
        ChangeKind.TextOnly => "仅文本",
        ChangeKind.ContentChanged => "内容变化",
        ChangeKind.LayoutChanged => "布局变化",
        ChangeKind.Missing => "缺失",
        ChangeKind.Incompatible => "不兼容",
        _ => "—",
    };

    /// <summary>层标记：与当前环境匹配显示 “✔”，否则 “—”（数据来源 ManifestComparer）。</summary>
    private static string LayerMark(bool matched) => matched ? "✔" : "—";

    private static string FormatBuiltAt(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        try
        {
            var dt = DateTimeOffset.Parse(iso).ToLocalTime();
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return iso;
        }
    }

    private static string PathUtilResolve(string root, string rel)
    {
        try { return RiMCP.Core.Paths.PathUtil.Resolve(root, rel); }
        catch { return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)); }
    }

    /// <summary>全局图五件套是否齐全。</summary>
    public static bool GraphFilesExist(string graphDir)
    {
        string[] names = { "graph.csr.bin", "graph.csc.bin", "graph.nodes.tsv", "graph.pagerank.tsv", "graph.meta.json" };
        return names.All(n => File.Exists(Path.Combine(graphDir, n)));
    }

    /// <summary>一次 CUDA 探测结果：驱动是否可用、CUDA 12 运行库是否存在、驱动报告的 CUDA 版本。</summary>
    private sealed record CudaRuntime(bool DriverAvailable, bool Has12, string? DriverCudaVersion);

    /// <summary>
    /// 探测 provider 可用性：cpu 恒可用；cuda 需 NVIDIA 驱动 + CUDA 12.8 运行库；
    /// directml 需 DirectML.dll。
    /// </summary>
    private static string ProbeProviders(string selected, out bool ok)
    {
        var cuda = ProbeCudaRuntime();
        bool cudaUsable = cuda.DriverAvailable && cuda.Has12;
        bool directml = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "DirectML.dll"));
        ok = selected switch
        {
            "cuda" => cudaUsable,
            "directml" => directml,
            _ => true,
        };

        string cudaState = cudaUsable
            ? cuda.DriverCudaVersion is null
                ? "cuda 可用（CUDA 12.x）"
                : $"cuda 可用（CUDA 12.x，驱动最高支持 {cuda.DriverCudaVersion}）"
            : cuda.DriverAvailable
                ? "cuda 不可用（缺少 cublasLt64_12.dll，需 CUDA 12.8+）"
                : "cuda 不可用（未检测到 NVIDIA 驱动）";

        string available = $"{cudaState} / directml {(directml ? "可用" : "不可用")} / cpu 可用";
        return $"当前 {selected}；{available}";
    }

    private static CudaRuntime ProbeCudaRuntime()
    {
        bool driver = false;
        string? driverVersion = null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                },
            };
            if (process.Start() && process.WaitForExit(3000))
            {
                driver = process.ExitCode == 0;
                if (driver)
                {
                    var match = Regex.Match(
                        process.StandardOutput.ReadToEnd(), @"CUDA Version:\s*(\d+\.\d+)");
                    if (match.Success) driverVersion = match.Groups[1].Value;
                }
            }
            else
            {
                try { process.Kill(true); } catch { }
            }
        }
        catch
        {
            // 未安装驱动或 nvidia-smi 不可用
        }

        return new CudaRuntime(driver, HasCudaRuntime("12"), driverVersion);
    }

    /// <summary>检查 PATH 或标准 CUDA 安装目录下是否存在 CUDA 12 的 cublasLt64 DLL。</summary>
    private static bool HasCudaRuntime(string major)
    {
        string dll = $"cublasLt64_{major}.dll";
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator))
        {
            string dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                if (File.Exists(Path.Combine(dir, dll))) return true;
            }
            catch
            {
                // 非法 PATH 项直接跳过
            }
        }

        string toolkitRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA GPU Computing Toolkit", "CUDA");
        try
        {
            foreach (var dir in Directory.GetDirectories(toolkitRoot, $"v{major}*"))
                if (File.Exists(Path.Combine(dir, "bin", dll))) return true;
        }
        catch
        {
            // 未安装 CUDA Toolkit
        }
        return false;
    }

    /// <summary>扫描 RiMCP_v2 根下所有以 “index” 开头的目录（供索引目录下拉框）。</summary>
    public static IReadOnlyList<string> ScanIndexDirectories(string rootAbs)
    {
        try
        {
            return Directory.EnumerateDirectories(rootAbs)
                .Select(p => Path.GetFileName(p))
                .Where(n => n.StartsWith(PathUtil.DefaultIndexDirName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>从 Launcher 可执行文件向上查找源码根（含 src/RiMCP.Indexer/RiMCP.Indexer.csproj）。</summary>
    public static string? LocateSourceRoot()
    {
        var env = Environment.GetEnvironmentVariable("RIMCP_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return Path.GetFullPath(env);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "RiMCP.Indexer", "RiMCP.Indexer.csproj")))
                return dir.FullName;
        }
        return null;
    }
}
