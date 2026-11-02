using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using RiMCP.Core.Models;
using RiMCP.Core.Paths;
using RiMCP.Core.Storage;

namespace RiMCP.Core.Indexing;

/// <summary>变化类型。Overall 取全部诊断中的最高优先级：
/// Incompatible &gt; Missing &gt; LayoutChanged &gt; ContentChanged &gt; TextOnly &gt; None。</summary>
public enum ChangeKind
{
    None,
    TextOnly,
    ContentChanged,
    LayoutChanged,
    Missing,
    Incompatible,
}

/// <summary>单个分片的诊断结果。</summary>
public sealed record ShardDiagnosis(
    string Shard,
    ChangeKind Kind,
    IReadOnlyList<string> ChangedLayers,
    string Reason);

/// <summary>一次诊断的汇总建议。Launcher 只消费本记录，不自行判断。</summary>
public sealed record UpdateAdvice(
    ChangeKind Overall,
    IReadOnlyList<ShardDiagnosis> Shards,
    string RecommendedAction,
    string Summary);

/// <summary>从 XML 复刻推导出的单条内容（用于哈希集合与符号序列比对）。</summary>
public sealed record XmlContentEntry(
    string Signature,
    SymbolKind Kind,
    string Namespace,
    string ShortName,
    string FileRelPath,
    int LineStart,
    string ContentHash);

/// <summary>
/// 一键诊断：比较当前 RimWorldData 与旧索引，给出“无需构建 / 仅图谱 / 增量构建 / 全量重建”。
/// <para>
/// 判定顺序：全局不兼容 → 分片布局 → 逐分片内容（defs/symbols/vectors/lucene/graph）。
/// 文本是否变化复用 <see cref="ContentHash"/> 的语义（与 Indexer <c>--graph-only</c> 的闸门一致）；
/// 额外要求符号序列（签名/文件/行号）未变化，避免建议一个会被 Indexer 拒绝的仅图谱。
/// </para>
/// </summary>
public static class UpdateAdvisor
{
    /// <summary>Indexer 当前固定的向量精度；manifest 记录其它值时视为不兼容。</summary>
    private const string CurrentPrecision = "fp16";

    private static readonly HashSet<string> MetaFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "defName", "label", "class", "classname", "parentname",
    };

    /// <summary>
    /// 诊断当前环境。不做任何写入；所有比对均为只读。
    /// </summary>
    /// <param name="v2Root">RiMCP_v2 根目录（build.json 内 manifestPath 的相对基准）。</param>
    /// <param name="dataRoot">RimWorldData 根目录（用于还原 XML 相对路径）。</param>
    /// <param name="indexDir">索引目录（&lt;v2Root&gt;/index）。</param>
    /// <param name="sources">当前 RimWorldData 的分片来源。</param>
    /// <param name="environments">当前环境的期望哈希。</param>
    /// <param name="build">build.json（缺失/读取失败时为 null）。</param>
    public static UpdateAdvice Diagnose(
        string v2Root,
        string dataRoot,
        string indexDir,
        IReadOnlyList<ShardSource> sources,
        IReadOnlyList<ShardCatalog.ShardEnvironment> environments,
        BuildJson.BuildInfo? build)
    {
        string model = build?.Model?.Name is { Length: > 0 } mn
            ? mn
            : environments.FirstOrDefault()?.EmbeddingModel ?? "e5-base-v2-onnx";
        int dim = build?.Dim is > 0 ? build.Dim : environments.FirstOrDefault()?.Dim ?? 768;

        var blockers = new List<string>();
        var pointers = new Dictionary<string, BuildJson.ShardPointer>(StringComparer.Ordinal);
        var manifests = new Dictionary<string, ShardManifest?>(StringComparer.Ordinal);

        if (build is null)
        {
            blockers.Add("缺少 build.json（未构建或旧索引）");
        }
        else
        {
            if (!build.Schema.Equals(BuildJson.Schema, StringComparison.Ordinal))
                blockers.Add($"build.json schema 不匹配（{build.Schema}）");
            if (build.FormatVersion != BuildJson.CurrentFormatVersion)
                blockers.Add($"索引格式版本 {build.FormatVersion} 不受支持（当前 {BuildJson.CurrentFormatVersion}）");
            if (build.Shards.Count == 0)
                blockers.Add("build.json 缺少分片指针");
            if (build.Model?.Name is not { Length: > 0 })
                blockers.Add("build.json 缺少嵌入模型名");
            if (build.Dim <= 0)
                blockers.Add("build.json 缺少有效向量维度");

            foreach (var pointer in build.Shards)
            {
                pointers[pointer.Name] = pointer;
                var manifest = LoadManifest(v2Root, pointer);
                manifests[pointer.Name] = manifest;
                if (manifest is null)
                {
                    blockers.Add($"分片 {pointer.Name} 的 manifest 缺失或无法解析");
                    continue;
                }
                if (manifest.FormatVersion != ShardManifest.CurrentFormatVersion)
                    blockers.Add($"分片 {pointer.Name} 的 manifest 版本 {manifest.FormatVersion} 不受支持");
                if (manifest.Truncated)
                    blockers.Add($"分片 {pointer.Name} 的 manifest 标记为截断样本");
                if (manifest.EmbeddingModel is { Length: > 0 } mm
                    && !mm.Equals(model, StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add($"分片 {pointer.Name} 的嵌入模型 {mm} 与当前 {model} 不一致");
                }
                if (manifest.Dim != 0 && manifest.Dim != dim)
                    blockers.Add($"分片 {pointer.Name} 的向量维度 {manifest.Dim} 与当前 {dim} 不一致");
                if (manifest.Precision is { Length: > 0 } mp
                    && !mp.Equals(CurrentPrecision, StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add($"分片 {pointer.Name} 的向量精度 {mp} 与当前 {CurrentPrecision} 不一致");
                }
                if (manifest.PreprocessingRevision != 0
                    && manifest.PreprocessingRevision != ShardCatalog.EmbeddingPreprocessingRevision)
                {
                    blockers.Add(
                        $"分片 {pointer.Name} 的预处理 revision {manifest.PreprocessingRevision} 与当前 " +
                        $"{ShardCatalog.EmbeddingPreprocessingRevision} 不一致");
                }
            }
        }

        var cache = EmbeddingCache.Load(Path.Combine(indexDir, EmbeddingCache.FileName));
        if (!cache.Exists)
            blockers.Add("缺少 embedding-cache.tsv，无法复用向量");

        var diagnoses = new List<ShardDiagnosis>();
        var current = sources.OrderBy(s => s.Index).ToList();

        if (blockers.Count > 0)
        {
            string reason = string.Join("；", blockers.Distinct(StringComparer.Ordinal));
            if (current.Count == 0)
            {
                diagnoses.Add(new ShardDiagnosis("index", ChangeKind.Incompatible, Array.Empty<string>(), reason));
            }
            else
            {
                foreach (var shard in current)
                    diagnoses.Add(new ShardDiagnosis(
                        shard.Name, ChangeKind.Incompatible, Array.Empty<string>(), reason));
            }
            return BuildAdvice(diagnoses, providerChanged: false);
        }

        var buildInfo = build!;

        // ---- 分片布局：新增 / 删除 / 序号变化 ----
        var currentNames = current.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var layoutNames = new HashSet<string>(StringComparer.Ordinal);
        var newShards = new List<string>();
        var goneOrShifted = new List<string>();

        foreach (var shard in current)
        {
            var pointer = pointers.GetValueOrDefault(shard.Name);
            if (pointer is null)
            {
                newShards.Add(shard.Name);
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name, ChangeKind.Missing, Array.Empty<string>(),
                    $"当前 RimWorldData 新增分片 {shard.Name}，build.json 未收录，需要增量构建以重建全局 id 布局"));
                continue;
            }
            if (pointer.Index != shard.Index)
            {
                layoutNames.Add(shard.Name);
                goneOrShifted.Add($"{shard.Name}（序号 {pointer.Index}→{shard.Index}）");
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name, ChangeKind.LayoutChanged, Array.Empty<string>(),
                    $"分片序号变化（旧 {pointer.Index} → 当前 {shard.Index}），全局 id 布局变化"));
            }
        }

        foreach (var pointer in buildInfo.Shards.OrderBy(p => p.Index))
        {
            if (currentNames.Contains(pointer.Name)) continue;
            goneOrShifted.Add(pointer.Name);
            diagnoses.Add(new ShardDiagnosis(
                pointer.Name, ChangeKind.LayoutChanged, Array.Empty<string>(),
                $"分片 {pointer.Name} 已从当前 RimWorldData 移除，需要增量构建以清理旧索引"));
        }

        // ---- 逐分片内容诊断（布局变化的分片不再做层比对）----
        bool providerChanged = false;
        bool graphArtifacts = GraphArtifactsExist(indexDir);

        foreach (var shard in current)
        {
            if (layoutNames.Contains(shard.Name)) continue;

            var env = environments.FirstOrDefault(e =>
                e.ShardName.Equals(shard.Name, StringComparison.Ordinal));
            var manifest = manifests.GetValueOrDefault(shard.Name);
            if (env is null || manifest is null)
            {
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name, ChangeKind.Missing, Array.Empty<string>(),
                    "当前环境缺少该分片的期望哈希或旧 manifest，无法比对"));
                continue;
            }

            bool providerDiff = manifest.OnnxProvider is { Length: > 0 }
                                && env.OnnxProvider is { Length: > 0 }
                                && !manifest.OnnxProvider.Equals(env.OnnxProvider, StringComparison.OrdinalIgnoreCase);
            providerChanged |= providerDiff;

            var comparison = ManifestComparer.Compare(manifest, env);

            if (comparison.IsReady)
            {
                if (!graphArtifacts)
                {
                    diagnoses.Add(new ShardDiagnosis(
                        shard.Name, ChangeKind.TextOnly, comparison.MismatchedLayers,
                        "缺少全局图产物，需要重新生成图"));
                }
                else
                {
                    diagnoses.Add(new ShardDiagnosis(
                        shard.Name, ChangeKind.None, Array.Empty<string>(),
                        providerDiff ? "与当前环境一致（provider 不同但不影响向量复用）" : "与当前环境一致"));
                }
                continue;
            }

            var layers = comparison.MismatchedLayers;
            if (layers.Count == 0)
            {
                string detail = string.Join("；", comparison.Details.Take(3));
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name, ChangeKind.ContentChanged, layers,
                    $"索引元数据与当前环境不一致：{detail}"));
                continue;
            }

            bool defsOrSymbols = layers.Contains(ShardCatalog.LayerHashes.Defs)
                                 || layers.Contains(ShardCatalog.LayerHashes.Symbols);
            if (defsOrSymbols)
            {
                var text = CompareText(dataRoot, indexDir, shard, manifest, env, cache);
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name,
                    text.Changed ? ChangeKind.ContentChanged : ChangeKind.TextOnly,
                    layers,
                    text.Reason));
            }
            else if (layers.Count == 1 && layers.Contains(ShardCatalog.LayerHashes.Graph))
            {
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name, ChangeKind.TextOnly, layers,
                    "仅 graph 层随全局统一图变化，可只重建图"));
            }
            else
            {
                diagnoses.Add(new ShardDiagnosis(
                    shard.Name, ChangeKind.ContentChanged, layers,
                    "向量/Lucene 层与当前环境不匹配"));
            }
        }

        return BuildAdvice(diagnoses, providerChanged);
    }

    /// <summary>
    /// 计算当前 XML 文件集合的 contentHash 集合（与 Indexer 嵌入时使用的哈希完全一致）。
    /// <para>
    /// 注意：UpdateAdvisor 中有一套针对 XML 内容哈希的复刻实现用于环境诊断。
    /// 如果 XmlParser 中的 Def/DefField 提取规则发生修改，请务必同步更新本方法中的逻辑，
    /// 否则会把“仅属性换序”误判为内容变化（或反之）。
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> ComputeXmlContentHashes(
        ShardSource source, string embeddingModel, int dim, string precision, int prepRevision)
        => ComputeXmlContentEntries(source, null, embeddingModel, dim, precision, prepRevision)
            .Select(e => e.ContentHash)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 逐条目复刻 XmlParser 的 Def/DefField 提取，返回签名/位置/内容哈希。
    /// 注意：规则修改时必须同步更新 XmlParser 与本方法（见 XmlParser.BuildDefFields 顶部注释）。
    /// </summary>
    public static IReadOnlyList<XmlContentEntry> ComputeXmlContentEntries(
        ShardSource source, string? dataRoot,
        string embeddingModel, int dim, string precision, int prepRevision)
    {
        var entries = new List<XmlContentEntry>();
        foreach (var file in source.XmlFiles)
        {
            try
            {
                AddXmlFileEntries(file, dataRoot, entries, embeddingModel, dim, precision, prepRevision);
            }
            catch
            {
                // 与 XmlParser 一致：单个文件损坏时跳过，不阻塞诊断。
            }
        }
        return entries;
    }

    private static void AddXmlFileEntries(
        string fileAbs, string? dataRoot, List<XmlContentEntry> entries,
        string embeddingModel, int dim, string precision, int prepRevision)
    {
        using var stream = File.OpenRead(fileAbs);
        var doc = XDocument.Load(stream, LoadOptions.SetLineInfo);
        if (doc.Root is null) return;

        string relPath = dataRoot is null ? string.Empty : ToRelativePath(dataRoot, fileAbs);

        foreach (var element in doc.Root.Elements()
                     .Where(e => e.Name.LocalName.EndsWith("Def", StringComparison.OrdinalIgnoreCase)))
        {
            var defName = element.Element("defName")?.Value ?? element.Element("DefName")?.Value;
            if (string.IsNullOrWhiteSpace(defName)) continue;

            var defType = element.Name.LocalName;
            var parentName = element.Attribute("ParentName")?.Value
                             ?? element.Element("parentName")?.Value
                             ?? element.Element("ParentName")?.Value;
            var parentSuffix = string.IsNullOrWhiteSpace(parentName) ? string.Empty : $" <- {parentName}";
            var signature = $"{defType}:{defName}{parentSuffix}";
            int startLine = LineOf(element);

            entries.Add(new XmlContentEntry(
                signature, SymbolKind.XmlDef, defType, defName, relPath, startLine,
                EntryHash(signature, ContentHash.CanonicalizeXml(element),
                    embeddingModel, dim, precision, prepRevision)));

            // Def 直接子元素中的“有意义文本叶子” → XmlDefField（规则与 XmlParser 保持一致）。
            foreach (var child in element.Elements())
            {
                var childName = child.Name.LocalName;
                if (MetaFields.Contains(childName)) continue;
                if (childName == "li") continue;
                if (child.Elements().Any()) continue;

                var value = child.Value?.Trim() ?? string.Empty;
                if (value.Length < 1 || value.Length > SymbolEntry.MaxPreviewLength) continue;

                entries.Add(new XmlContentEntry(
                    $"{signature}.{childName}", SymbolKind.XmlDefField, defType, childName,
                    relPath, LineOf(child),
                    EntryHash($"{signature}.{childName}", ContentHash.CanonicalizeXml(child),
                        embeddingModel, dim, precision, prepRevision)));
            }
        }
    }

    /// <summary>与 Indexer 的 RelativePathUtil.ToRel 等价（Core 不能引用 Indexer）：正斜杠、无越界。</summary>
    private static string ToRelativePath(string rootAbs, string fileAbs)
    {
        var segments = Path.GetRelativePath(Path.GetFullPath(rootAbs), Path.GetFullPath(fileAbs))
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains("..", StringComparer.Ordinal))
            throw new InvalidDataException($"文件 {fileAbs} 越出根目录 {rootAbs}，无法转相对路径。");
        return string.Join('/', segments);
    }

    private static int LineOf(XElement element)
    {
        var info = (IXmlLineInfo)element;
        return info.HasLineInfo() ? Math.Max(1, info.LineNumber) : 1;
    }

    private static string EntryHash(
        string signature, string body,
        string embeddingModel, int dim, string precision, int prepRevision)
        => ContentHash.Compute(
            ContentHash.NormalizeText($"{signature}\n{body}"),
            embeddingModel, dim, precision, prepRevision);

    /// <summary>
    /// 文本是否变化的判定：当前 XML 哈希集合与符号序列，对比 symbols.tsv + embedding-cache.tsv 还原的旧快照。
    /// </summary>
    private static TextComparison CompareText(
        string dataRoot, string indexDir, ShardSource shard,
        ShardManifest manifest, ShardCatalog.ShardEnvironment env, EmbeddingCache cache)
    {
        var current = ComputeXmlContentEntries(
            shard, dataRoot, env.EmbeddingModel, env.Dim, env.Precision,
            ShardCatalog.EmbeddingPreprocessingRevision);
        var currentHashes = current.Select(e => e.ContentHash).ToHashSet(StringComparer.Ordinal);

        var old = LoadOldXmlSnapshot(indexDir, shard.Name, cache);
        if (old is null)
        {
            return new TextComparison(
                true, "无法从 symbols.tsv/embedding-cache.tsv 还原旧文本哈希，按内容变化处理");
        }

        if (!currentHashes.SetEquals(old.Hashes))
        {
            return new TextComparison(
                true, $"Def 文本内容发生变化（当前 {current.Count} 条，旧 {old.Entries.Count} 条）");
        }

        if (!SameXmlSequence(old.Entries, current))
        {
            return new TextComparison(
                true, "符号序列/行号发生变化，--graph-only 会被拒绝，需要增量构建");
        }

        if (shard.IncludeCSharp && CSharpFilesNewerThan(shard, manifest))
            return new TextComparison(true, "C# 源文件在构建后发生变化，需要重新嵌入");

        return new TextComparison(false, "仅 XML 属性顺序/注释/缩进变化，文本内容未变");
    }

    private sealed record OldXmlSnapshot(HashSet<string> Hashes, List<SymbolEntry> Entries);

    /// <summary>从 symbols.tsv + embedding-cache.tsv 还原旧 XML 快照；无法还原返回 null。</summary>
    private static OldXmlSnapshot? LoadOldXmlSnapshot(
        string indexDir, string shard, EmbeddingCache cache)
    {
        string symbolsPath = Path.Combine(indexDir, shard, "symbols.tsv");
        if (!File.Exists(symbolsPath)) return null;

        List<SymbolEntry> all;
        try
        {
            all = SymbolsTsv.Load(symbolsPath);
        }
        catch
        {
            return null;
        }

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<SymbolEntry>();
        foreach (var entry in all)
        {
            if (entry.Source != SourceType.Xml) continue;
            if (!cache.TryGetBySlot(shard, entry.Id, out var hash) || hash.Length == 0)
                return null;
            hashes.Add(hash);
            entries.Add(entry);
        }
        return new OldXmlSnapshot(hashes, entries);
    }

    /// <summary>
    /// 旧 XML 条目（已按 IdAllocator 顺序存放）与当前条目是否一一对应；
    /// 比较键与 Indexer 的 SameSymbolSequence 对齐（签名/类型/短名/文件/起始行）。
    /// </summary>
    private static bool SameXmlSequence(
        IReadOnlyList<SymbolEntry> oldEntries, IReadOnlyList<XmlContentEntry> currentEntries)
    {
        if (oldEntries.Count != currentEntries.Count) return false;

        var sorted = currentEntries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Namespace, StringComparer.Ordinal)
            .ThenBy(e => e.Signature, StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < oldEntries.Count; i++)
        {
            var a = oldEntries[i];
            var b = sorted[i];
            if (a.Signature != b.Signature
                || a.Kind != b.Kind
                || a.ShortName != b.ShortName
                || a.FileRelPath != b.FileRelPath
                || a.LineStart != b.LineStart)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>C# 文件是否在 manifest 构建时间之后被修改（保守判定，跨时区安全）。</summary>
    private static bool CSharpFilesNewerThan(ShardSource shard, ShardManifest manifest)
    {
        if (shard.AssemblyFiles.Count == 0) return false;

        DateTime builtAtUtc;
        if (string.IsNullOrWhiteSpace(manifest.BuiltAt)) return true;
        try
        {
            builtAtUtc = DateTimeOffset.Parse(
                manifest.BuiltAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime;
        }
        catch
        {
            return true;
        }

        var limit = builtAtUtc.AddSeconds(2);
        foreach (var file in shard.AssemblyFiles)
        {
            try
            {
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > limit) return true;
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    private sealed record TextComparison(bool Changed, string Reason);

    private static ShardManifest? LoadManifest(string v2Root, BuildJson.ShardPointer pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer.ManifestPath)) return null;
        try
        {
            string path = PathUtil.Resolve(v2Root, pointer.ManifestPath);
            if (!File.Exists(path)) return null;
            return ShardManifest.Load(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>全局图五件套是否齐全（与 Indexer 的 GraphFilesExist 同名同义）。</summary>
    private static bool GraphArtifactsExist(string indexDir)
    {
        string dir = Path.Combine(indexDir, "graph");
        string[] names =
        {
            "graph.csr.bin", "graph.csc.bin", "graph.nodes.tsv",
            "graph.pagerank.tsv", "graph.meta.json",
        };
        return names.All(n => File.Exists(Path.Combine(dir, n)));
    }

    private static int Priority(ChangeKind kind) => kind switch
    {
        ChangeKind.Incompatible => 5,
        ChangeKind.Missing => 4,
        ChangeKind.LayoutChanged => 3,
        ChangeKind.ContentChanged => 2,
        ChangeKind.TextOnly => 1,
        _ => 0,
    };

    private static UpdateAdvice BuildAdvice(
        List<ShardDiagnosis> diagnoses, bool providerChanged)
    {
        var overall = ChangeKind.None;
        foreach (var diagnosis in diagnoses)
            if (Priority(diagnosis.Kind) > Priority(overall))
                overall = diagnosis.Kind;

        string action = overall switch
        {
            ChangeKind.Incompatible => "全量重建",
            ChangeKind.Missing or ChangeKind.LayoutChanged or ChangeKind.ContentChanged => "增量构建",
            ChangeKind.TextOnly => "仅图谱",
            _ => "无需构建",
        };

        string summary = overall switch
        {
            ChangeKind.Incompatible => BuildIncompatibleSummary(diagnoses),
            ChangeKind.Missing or ChangeKind.LayoutChanged => BuildLayoutSummary(diagnoses),
            ChangeKind.ContentChanged => "建议：增量构建（检测到 Def/源码内容变化）。",
            ChangeKind.TextOnly => BuildTextOnlySummary(diagnoses),
            _ => providerChanged
                ? "索引与当前环境一致，无需构建。provider 变化不影响语义，无需重建。"
                : "索引与当前环境一致，无需构建。",
        };

        return new UpdateAdvice(overall, diagnoses, action, summary);
    }

    private static string BuildIncompatibleSummary(List<ShardDiagnosis> diagnoses)
    {
        var reasons = diagnoses
            .Where(d => d.Kind == ChangeKind.Incompatible)
            .Select(d => d.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        string reason = reasons.Count == 0 ? "旧索引与当前环境不兼容" : string.Join("；", reasons);
        return $"必须全量重建（{reason}）。";
    }

    private static string BuildLayoutSummary(List<ShardDiagnosis> diagnoses)
    {
        var added = diagnoses
            .Where(d => d.Kind == ChangeKind.Missing)
            .Select(d => d.Shard)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var shifted = diagnoses
            .Where(d => d.Kind == ChangeKind.LayoutChanged)
            .Select(d => d.Shard)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parts = new List<string>();
        if (added.Count > 0) parts.Add("新增 " + string.Join(", ", added));
        if (shifted.Count > 0) parts.Add("移除/移位 " + string.Join(", ", shifted));
        string detail = parts.Count == 0 ? "分片布局变化" : string.Join("；", parts);
        return $"建议：增量构建（分片布局发生变化：{detail}）。";
    }

    private static string BuildTextOnlySummary(List<ShardDiagnosis> diagnoses)
    {
        bool graphMissing = diagnoses.Any(d =>
            d.Kind == ChangeKind.TextOnly && d.Reason.Contains("缺少全局图产物", StringComparison.Ordinal));
        return graphMissing
            ? "建议：仅图谱（缺少全局图产物，需要重新生成图）。"
            : "建议：仅图谱（文本内容未变化，仅 XML 属性顺序/注释/缩进等差异）。";
    }
}
