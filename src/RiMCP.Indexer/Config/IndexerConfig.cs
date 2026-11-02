using RiMCP.Core.Paths;

namespace RiMCP.Indexer.Config;

/// <summary>
/// 由 CLI 参数解析出的索引器配置。所有路径在解析后均为绝对路径，避免硬编码。
/// </summary>
public sealed class IndexerConfig
{
    /// <summary>默认 C# 顶级目录白名单（RimWorld 自己的命名空间目录）。</summary>
    public static readonly IReadOnlyList<string> DefaultIncludeRoots = new[] { "RimWorld", "Verse", "LudeonTK" };

    /// <summary>RimWorldData 根目录（含 Assembly-CSharp 与 Data）。</summary>
    public required string Root { get; init; }

    /// <summary>索引输出目录（默认 &lt;root&gt;/../index）。</summary>
    public required string Output { get; init; }

    /// <summary>
    /// 允许进入索引的 C# 顶级目录白名单。相对路径第一段（含 "/" 时）不在名单内的文件被跳过；
    /// 根目录下无 "/" 的文件始终保留。默认仅 RimWorld 三巨头。
    /// </summary>
    public IReadOnlyList<string> SourceIncludeRoots { get; init; } = DefaultIncludeRoots;

    /// <summary>ONNX 模型目录。</summary>
    public required string ModelDir { get; init; }

    /// <summary>RiMCP_v2 根目录（同时含 RimWorldData 与 models），build.json 相对路径以此为基准。</summary>
    public required string V2Root { get; init; }

    /// <summary>model.onnx 完整路径。</summary>
    public string ModelFile => Path.Combine(ModelDir, "model.onnx");

    /// <summary>tokenizer.json 完整路径。</summary>
    public string TokenizerPath => Path.Combine(ModelDir, "tokenizer.json");

    /// <summary>ONNX 批量大小。</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>嵌入最大 token 长度。</summary>
    public int MaxLength { get; init; } = 256;

    /// <summary>ONNX 执行提供程序：cpu | cuda。</summary>
    public string OnnxProvider { get; init; } = "cpu";

    /// <summary>解析并行度（默认=处理器数）。</summary>
    public int MaxParallelism { get; init; }

    /// <summary>仅解析 + 打印统计，不加载模型 / 不分配 id / 不嵌入 / 不落盘。</summary>
    public bool DryRun { get; init; }

    /// <summary>仅从已有 symbols.tsv 生成图（graph.*），不重跑解析/嵌入/Lucene。</summary>
    public bool GraphOnly { get; init; }

    /// <summary>
    /// RimWorldData 下 C# 源码的物理目录（相对 RimWorldData 根，含子目录结构）。
    /// 扫描后相对路径会去掉此前缀再套白名单；存入 FileRelPath 的仍是完整相对路径。
    /// </summary>
    public required string CSharpSourceSubdir { get; init; } = DefaultCSharpSubdir;

    /// <summary>默认 C# 源码子目录。</summary>
    public const string DefaultCSharpSubdir = "RimWorld_Decompiled/Assembly-CSharp";

    /// <summary>RimWorldData 下存放 XML Def 的子目录。</summary>
    public const string XmlSubDir = "Data";

    /// <summary>默认输出目录相对 root 的路径。</summary>
    private const string DefaultOutputRel = "../index";

    /// <summary>嵌入向量的维度（e5-base-v2 固定 768）。</summary>
    public const int Dim = 768;

    /// <summary>
    /// 解析命令行参数并构造配置。
    /// </summary>
    public static IndexerConfig Parse(string[] args)
    {
        ArgMap map = new(args);

        // --root：显式给 RimWorldData 目录；否则向上自发现含 models/models 的根再拼 RimWorldData
        string? rootArg = null;
        map.TryGet("root", out rootArg);

        string v2Root = string.IsNullOrEmpty(rootArg)
            ? PathUtil.LocateRoot(Directory.GetCurrentDirectory())
            : PathUtil.LocateRoot(rootArg);

        string rootAbs = string.IsNullOrEmpty(rootArg)
            ? Path.GetFullPath(Path.Combine(v2Root, "RimWorldData"))
            : Path.GetFullPath(rootArg);

        if (!Directory.Exists(rootAbs))
            throw new InvalidOperationException($"RimWorldData 目录不存在：{rootAbs}");

        string outputAbs = map.TryGet("output", out var outArg)
            ? Path.GetFullPath(outArg)
            : Path.GetFullPath(Path.Combine(rootAbs, DefaultOutputRel));

        string modelDir = map.TryGet("model-dir", out var modelArg)
            ? Path.GetFullPath(modelArg)
            : Path.Combine(v2Root, "models", "e5-base-v2-onnx");

        // --dry-run：仅解析打印统计，不要求模型存在
        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        // --graph-only：仅生成图，不要求模型存在
        bool graphOnly = args.Contains("--graph-only", StringComparer.OrdinalIgnoreCase);

        if (!dryRun && !graphOnly)
        {
            if (!File.Exists(Path.Combine(modelDir, "model.onnx")))
                throw new InvalidOperationException($"model.onnx 不存在：{Path.Combine(modelDir, "model.onnx")}");
            if (!File.Exists(Path.Combine(modelDir, "tokenizer.json")))
                throw new InvalidOperationException($"tokenizer.json 不存在：{Path.Combine(modelDir, "tokenizer.json")}");
        }

        int batch = map.TryGet("batch-size", out var batchArg) && int.TryParse(batchArg, out var b) ? b : 32;
        int maxLen = map.TryGet("max-length", out var lenArg) && int.TryParse(lenArg, out var m) ? m : 256;
        string provider = map.TryGet("onnx-provider", out var provArg) ? provArg.ToLowerInvariant() : "cpu";
        if (provider != "cpu" && provider != "cuda")
            provider = "cpu";
        int parallelism = map.TryGet("max-parallel", out var parArg) && int.TryParse(parArg, out var p) ? p : Environment.ProcessorCount;

        // --include-roots RimWorld,Verse,LudeonTK（逗号分隔；未传则用默认三巨头）
        IReadOnlyList<string> includeRoots = DefaultIncludeRoots;
        if (map.TryGet("include-roots", out var rootsArg))
            includeRoots = rootsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // --csharp-subdir：C# 源码物理目录（相对 RimWorldData 根）
        string csharpSubdir = map.TryGet("csharp-subdir", out var subdirArg)
            ? subdirArg.Trim('\\', '/')
            : DefaultCSharpSubdir;

        return new IndexerConfig
        {
            Root = rootAbs,
            Output = outputAbs,
            ModelDir = Path.GetFullPath(modelDir),
            V2Root = v2Root,
            BatchSize = Math.Max(1, batch),
            MaxLength = Math.Max(8, maxLen),
            OnnxProvider = provider,
            MaxParallelism = Math.Max(1, parallelism),
            SourceIncludeRoots = includeRoots,
            CSharpSourceSubdir = csharpSubdir,
            DryRun = dryRun,
            GraphOnly = graphOnly,
        };
    }

    /// <summary>轻量 key=value 参数映射（支持 --name value 与 --name=value）。</summary>
    private ref struct ArgMap
    {
        private readonly Dictionary<string, string> _map;

        public ArgMap(string[] args)
        {
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
                var body = a[2..];
                var eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    _map[body[..eq]] = body[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    _map[body] = args[i + 1];
                    i++;
                }
            }
        }

        public bool TryGet(string key, out string value)
            => _map.TryGetValue(key, out value!);
    }
}