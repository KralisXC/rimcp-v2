using System.Text.Json;
using RiMCP.Core.Paths;

namespace RiMCP.Core.Storage;

/// <summary>
/// build.json 的自描述清单：版本/维度/计数/模型指纹/相对路径。
/// 所有 <c>paths</c> 一律相对 RiMCP_v2 根目录。
/// </summary>
public static class BuildJson
{
    /// <summary>当前索引格式版本，布局变更时必须递增并要求全量重建。</summary>
    public const int CurrentFormatVersion = 3;

    /// <summary>build.json 的 schema 标识。</summary>
    public const string Schema = "rix-index";

    /// <summary>生成器信息（写入方填写）。</summary>
    public sealed class GeneratorInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }

    /// <summary>嵌入模型信息。</summary>
    public sealed class ModelInfo
    {
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;

        /// <summary>ONNX 模型文件 + 归一化参数的 SHA256 前 16 位 hex。</summary>
        public string Hash { get; set; } = string.Empty;
    }

    /// <summary>计数摘要。</summary>
    public sealed class CountsInfo
    {
        public long Symbols { get; set; }
        public long Nodes { get; set; }
        public long Edges { get; set; }
    }

    /// <summary>Lucene 状态。</summary>
    public sealed class LuceneInfo
    {
        public bool Ok { get; set; }
        public string Version { get; set; } = string.Empty;
    }

    /// <summary>单个分片的指针（顶层 build.json 只做汇总，不复制分片内容）。</summary>
    public sealed class ShardPointer
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public long BaseId { get; set; }
        public long SymbolCount { get; set; }
        public long EdgeCount { get; set; }

        /// <summary>manifest.json 相对 RiMCP_v2 根的路径。</summary>
        public string ManifestPath { get; set; } = string.Empty;

        /// <summary>manifest.json 内容 SHA-256。</summary>
        public string ManifestHash { get; set; } = string.Empty;
    }

    /// <summary>全局图指针（所有分片共用同一份统一构图产物）。</summary>
    public sealed class GlobalGraphInfo
    {
        /// <summary>图目录相对 RiMCP_v2 根的路径，如 index/graph。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>全局图逻辑哈希（各分片 manifest 的 graph 层使用同一个值）。</summary>
        public string Hash { get; set; } = string.Empty;

        public long NodeCount { get; set; }
        public long EdgeCount { get; set; }
        public string BuiltAt { get; set; } = string.Empty;

        /// <summary>是否包含跨分片边（阶段二固定为 true）。</summary>
        public bool CrossShard { get; set; } = true;
    }

    /// <summary>build.json 根对象。</summary>
    public sealed class BuildInfo
    {
        public string Schema { get; set; } = BuildJson.Schema;
        public int FormatVersion { get; set; } = BuildJson.CurrentFormatVersion;
        public GeneratorInfo? Generator { get; set; }
        public int Dim { get; set; }
        public ModelInfo? Model { get; set; }
        public CountsInfo? Counts { get; set; }

        /// <summary>游戏版本（各分片 manifest 的公共值）。</summary>
        public string GameVersion { get; set; } = string.Empty;

        /// <summary>构建者提交号。</summary>
        public string BuilderCommit { get; set; } = "unknown";

        /// <summary>分片 manifest 指针列表（顺序即全局 id 顺序）。</summary>
        public List<ShardPointer> Shards { get; set; } = new();

        /// <summary>本次构建默认启用的分片；MCP 未显式配置时读取此字段。</summary>
        public List<string> EnabledShards { get; set; } = new();

        /// <summary>全局图指针与节点/边数。</summary>
        public GlobalGraphInfo? Graph { get; set; }

        /// <summary>相对路径映射：键为逻辑名，值为相对 RiMCP_v2 根的路径。</summary>
        public Dictionary<string, string> Paths { get; set; } = new(StringComparer.Ordinal);

        public string Granularity { get; set; } = string.Empty;
        public LuceneInfo? Lucene { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>从文件加载并反序列化 build.json。</summary>
    public static BuildInfo Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BuildInfo>(json, Options)
               ?? throw new InvalidDataException("build.json 内容为空或反序列化失败。");
    }

    /// <summary>将 build.json 序列化并写出。</summary>
    public static void Save(string path, BuildInfo info)
    {
        var json = JsonSerializer.Serialize(info, Options);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// 校验构建信息与实际索引一致（schema/格式版本/维度/符号计数）。
    /// 任一不符抛出 <see cref="InvalidDataException"/>，确保不会误读旧/错配索引。
    /// </summary>
    public static void Validate(BuildInfo loaded, int actualDim, long actualCount)
    {
        if (loaded.Schema != Schema)
            throw new InvalidDataException($"build.json schema 不匹配：{loaded.Schema}（期望 {Schema}）。");
        if (loaded.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $"索引格式版本 {loaded.FormatVersion} 与当前 {CurrentFormatVersion} 不符。旧格式索引不兼容，请执行全量重建（Indexer 或 scripts/build-and-index.ps1）。");
        if (loaded.Dim != actualDim)
            throw new InvalidDataException($"build.json 维度 {loaded.Dim} 与实际 {actualDim} 不符。");
        if (loaded.Counts is { } c && c.Symbols != actualCount)
            throw new InvalidDataException($"build.json 符号数 {c.Symbols} 与实际 {actualCount} 不符。");
        if (loaded.Shards is null || loaded.Shards.Count == 0)
            throw new InvalidDataException("build.json 缺少分片指针（shards），旧索引不兼容，请执行全量重建。");
    }

    /// <summary>
    /// 校验构建信息，并只对 <paramref name="enabledShards"/> 内的分片检查 manifest 文件存在性。
    /// 未启用分片只校验 build.json 指针（Name/ManifestPath 非空），不要求文件存在。
    /// </summary>
    public static void Validate(
        BuildInfo loaded, int actualDim, long actualCount,
        IReadOnlyCollection<string> enabledShards, string v2RootAbs)
    {
        Validate(loaded, actualDim, actualCount);

        long pointerSum = 0;
        foreach (var pointer in loaded.Shards)
        {
            if (string.IsNullOrWhiteSpace(pointer.Name) || string.IsNullOrWhiteSpace(pointer.ManifestPath))
                throw new InvalidDataException("build.json 存在缺少 name/manifestPath 的分片指针，请执行全量重建。");
            pointerSum += pointer.SymbolCount;
        }
        if (loaded.Counts is { } counts && counts.Symbols != pointerSum)
            throw new InvalidDataException(
                $"build.json 符号总数 {counts.Symbols} 与分片指针合计 {pointerSum} 不符，请执行全量重建。");

        var available = loaded.Shards.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in enabledShards)
        {
            if (!available.Contains(name))
                throw new InvalidDataException(
                    $"启用的分片 '{name}' 不在 build.json.shards 中。可用分片：{string.Join(", ", available)}。");

            var pointer = loaded.Shards.First(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string manifestAbs = PathUtil.Resolve(v2RootAbs, pointer.ManifestPath);
            if (!File.Exists(manifestAbs))
                throw new InvalidDataException(
                    $"启用分片 {pointer.Name} 的 manifest 不存在：{manifestAbs}（请重新执行 Indexer）。");
        }
    }
}