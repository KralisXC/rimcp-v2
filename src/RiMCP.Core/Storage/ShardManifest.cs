using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RiMCP.Core.Storage;

/// <summary>
/// 单个分片的 manifest。记录来源指纹、构建环境与各层逻辑哈希，
/// Indexer 写出、MCP 与 Launcher 读取并比对。
/// </summary>
public sealed class ShardManifest
{
    /// <summary>manifest schema 标识。</summary>
    public const string SchemaId = "rimcp-shard";

    /// <summary>manifest 格式版本（阶段二全局图为 2）。</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>层名常量（顺序稳定，写入与比对共用）。</summary>
    public static readonly IReadOnlyList<string> LayerNames = new[]
    {
        "defs", "symbols", "graph", "vectors", "lucene",
    };

    public string Schema { get; set; } = SchemaId;
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>分片名，如 core / dlc_royalty。</summary>
    public string Shard { get; set; } = string.Empty;

    /// <summary>分片序号（0 = core，其余按 DLC 名排序递增）。</summary>
    public int ShardIndex { get; set; }

    /// <summary>该分片在全局符号 id 空间中的起始 id。</summary>
    public long BaseId { get; set; }

    /// <summary>该分片符号数。</summary>
    public long SymbolCount { get; set; }

    /// <summary>是否为 --max-entries 产生的截断样本（样本不可用于正式查询）。</summary>
    public bool Truncated { get; set; }

    /// <summary>游戏版本（从 Data/&lt;DLC&gt;/About/About.xml 读取）。</summary>
    public string GameVersion { get; set; } = string.Empty;

    /// <summary>该分片 Def + 程序集的来源内容哈希。</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>构建者提交号；无 git 或未知时为 unknown。</summary>
    public string BuilderCommit { get; set; } = "unknown";

    /// <summary>ONNX provider（cuda / directml / cpu）。</summary>
    public string OnnxProvider { get; set; } = string.Empty;

    /// <summary>向量精度（当前固定 fp16）。</summary>
    public string Precision { get; set; } = "fp16";

    /// <summary>嵌入模型标识。</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>嵌入预处理修订号（内容哈希前缀，变化即全部向量失效）。</summary>
    public int PreprocessingRevision { get; set; }

    /// <summary>向量维度。</summary>
    public int Dim { get; set; }

    /// <summary>各层逻辑哈希与构建时间。</summary>
    public Dictionary<string, LayerInfo> Layers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>整份 manifest 写出时间（ISO 8601）。</summary>
    public string BuiltAt { get; set; } = string.Empty;

    /// <summary>确保五层都存在，便于读取方直接索引。</summary>
    public void EnsureLayers()
    {
        foreach (var name in LayerNames)
            Layers.TryAdd(name, new LayerInfo());
    }

    /// <summary>读取 manifest。</summary>
    public static ShardManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ShardManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException($"manifest 内容为空：{path}");
        manifest.EnsureLayers();
        return manifest;
    }

    /// <summary>写出 manifest（缩进 JSON）。</summary>
    public void Save(string path)
    {
        EnsureLayers();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>计算文件内容 SHA-256（小写 hex），用于顶层 build.json 的 manifest 指针。</summary>
    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

/// <summary>单层哈希与构建时间。</summary>
public sealed class LayerInfo
{
    public string Hash { get; set; } = string.Empty;
    public string BuiltAt { get; set; } = string.Empty;
}
