using RiMCP.Core.Storage;

namespace RiMCP.Core.Indexing;

/// <summary>分片状态。</summary>
public enum ShardState
{
    /// <summary>manifest 缺失。</summary>
    Missing,
    /// <summary>存在但与当前环境不匹配，需要重建。</summary>
    NeedsRebuild,
    /// <summary>与当前环境匹配。</summary>
    Ready,
}

/// <summary>单个分片的比对结果，Launcher 与 Indexer/MCP 诊断共用。</summary>
public sealed record ShardComparison(
    string Shard,
    ShardState State,
    bool DefsMatch,
    bool SymbolsMatch,
    bool GraphMatch,
    bool VectorsMatch,
    bool LuceneMatch,
    IReadOnlyList<string> MismatchedLayers,
    IReadOnlyList<string> Details)
{
    public bool IsReady => State == ShardState.Ready;
}

/// <summary>
/// manifest 与当前环境的比对逻辑。禁止在 Launcher 或 Indexer 中重写。
/// </summary>
public static class ManifestComparer
{
    public static ShardComparison Compare(ShardManifest? manifest, ShardCatalog.ShardEnvironment env)
    {
        if (manifest is null)
            return Missing(env.ShardName, "manifest.json 缺失");

        var details = new List<string>();
        var mismatched = new List<string>();
        bool needsRebuild = false;

        if (!manifest.Schema.Equals(ShardManifest.SchemaId, StringComparison.Ordinal))
        {
            details.Add($"manifest schema 不匹配：{manifest.Schema}");
            needsRebuild = true;
        }
        if (manifest.FormatVersion != ShardManifest.CurrentFormatVersion)
        {
            details.Add($"manifest 版本 {manifest.FormatVersion} 不受支持，需要重建");
            needsRebuild = true;
        }
        if (!manifest.Shard.Equals(env.ShardName, StringComparison.Ordinal))
        {
            details.Add($"分片名不符：manifest={manifest.Shard}，当前={env.ShardName}");
            needsRebuild = true;
        }
        if (manifest.ShardIndex != env.ShardIndex)
        {
            details.Add($"分片序号不符：manifest={manifest.ShardIndex}，当前={env.ShardIndex}");
            needsRebuild = true;
        }
        if (manifest.Truncated)
        {
            details.Add("manifest 标记为截断样本（--max-entries），需全量重建");
            needsRebuild = true;
        }
        if (!manifest.GameVersion.Equals(env.GameVersion, StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"游戏版本不符：manifest={manifest.GameVersion}，当前={env.GameVersion}");
            needsRebuild = true;
        }
        if (!manifest.EmbeddingModel.Equals(env.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"嵌入模型不符：manifest={manifest.EmbeddingModel}，当前={env.EmbeddingModel}");
            needsRebuild = true;
        }
        if (!manifest.Precision.Equals(env.Precision, StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"向量精度不符：manifest={manifest.Precision}，当前={env.Precision}");
            needsRebuild = true;
        }
        if (manifest.Dim != 0 && manifest.Dim != env.Dim)
        {
            details.Add($"向量维度不符：manifest={manifest.Dim}，当前={env.Dim}");
            needsRebuild = true;
        }
        if (manifest.PreprocessingRevision != 0 &&
            manifest.PreprocessingRevision != ShardCatalog.EmbeddingPreprocessingRevision)
        {
            details.Add(
                $"嵌入预处理 revision 不符：manifest={manifest.PreprocessingRevision}，当前={ShardCatalog.EmbeddingPreprocessingRevision}");
            needsRebuild = true;
        }
        if (!manifest.OnnxProvider.Equals(env.OnnxProvider, StringComparison.OrdinalIgnoreCase))
            details.Add($"provider 不同：manifest={manifest.OnnxProvider}，当前={env.OnnxProvider}（不影响向量复用）");
        if (!manifest.SourceHash.Equals(env.SourceHash, StringComparison.Ordinal))
        {
            details.Add("来源内容哈希不匹配");
            needsRebuild = true;
        }

        // 五层逐一比对 manifest 中记录的逻辑哈希与当前环境期望值。
        var layerMatches = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var layer in ShardManifest.LayerNames)
        {
            string recorded = manifest.Layers.TryGetValue(layer, out var info) ? info.Hash : string.Empty;
            string expected = env.LayerHashes.TryGetValue(layer, out var h) ? h : string.Empty;
            bool match = recorded.Length > 0 && expected.Length > 0
                         && recorded.Equals(expected, StringComparison.Ordinal);
            layerMatches[layer] = match;
            if (!match)
            {
                mismatched.Add(layer);
                needsRebuild = true;
                details.Add(recorded.Length == 0
                    ? $"{layer} 层缺少 hash"
                    : $"{layer} 层 hash 不匹配");
            }
        }

        var state = needsRebuild ? ShardState.NeedsRebuild : ShardState.Ready;

        return new ShardComparison(
            env.ShardName,
            state,
            layerMatches[ShardCatalog.LayerHashes.Defs],
            layerMatches[ShardCatalog.LayerHashes.Symbols],
            layerMatches[ShardCatalog.LayerHashes.Graph],
            layerMatches[ShardCatalog.LayerHashes.Vectors],
            layerMatches[ShardCatalog.LayerHashes.Lucene],
            mismatched,
            details);
    }

    /// <summary>便于日志输出的单行摘要。</summary>
    public static string Describe(ShardComparison c)
    {
        string state = c.State switch
        {
            ShardState.Ready => "就绪",
            ShardState.Missing => "缺失",
            _ => "需重建",
        };
        if (c.IsReady) return $"{c.Shard}: {state}";
        var parts = new List<string>();
        if (c.MismatchedLayers.Count > 0)
            parts.Add("层=" + string.Join(",", c.MismatchedLayers));
        if (c.Details.Count > 0)
            parts.Add(string.Join("；", c.Details));
        return parts.Count == 0 ? $"{c.Shard}: {state}" : $"{c.Shard}: {state}（{string.Join("；", parts)}）";
    }

    private static ShardComparison Missing(string shard, string detail)
        => new(shard, ShardState.Missing, false, false, false, false, false,
            Array.Empty<string>(), new[] { detail });
}
