using RiMCP.Core.Models;
using RiMCP.Core.Paths;
using RiMCP.Core.Storage;

namespace RiMCP.Indexer.Verification;

/// <summary>
/// P8 简化版自检：对账 counts、抽验证文件存在、校验向量维度。返回错误列表，全部为空视为通过。
/// </summary>
public static class SelfCheck
{
    /// <summary>
    /// 执行自检，返回错误信息列表；为空表示通过。
    /// </summary>
    public static List<string> Run(
        string rootAbs, string outputDir, string vectorsPath, string tsvPath,
        List<SymbolEntry> entries, int dim, int sampleCount = 3)
    {
        var errors = new List<string>();

        // A. counts 对账：vectors 记录数 == symbols.tsv 行数 == 内存 entries
        using (var vec = VectorsBin.Open(vectorsPath))
        {
            if (vec.RecordCount != entries.Count)
                errors.Add($"vectors.bin.RecordCount({vec.RecordCount}) != entries.Count({entries.Count})");
        }

        var tsvLoaded = SymbolsTsv.Load(tsvPath);
        if (tsvLoaded.Count != entries.Count)
            errors.Add($"symbols.tsv 行数({tsvLoaded.Count}) != entries.Count({entries.Count})");

        // B. 随机抽 sampleCount 条，验证 FileRelPath 能拼出真实存在的文件
        var rng = new Random(1234);
        int n = Math.Min(sampleCount, entries.Count);
        for (int k = 0; k < n; k++)
        {
            int idx = rng.Next(entries.Count);
            var e = entries[idx];
            if (string.IsNullOrWhiteSpace(e.FileRelPath))
            {
                errors.Add($"条目[{idx}] FileRelPath 为空：{e.Signature}");
                continue;
            }
            string full = PathUtil.Resolve(rootAbs, e.FileRelPath);
            if (!File.Exists(full))
                errors.Add($"FileRelPath 指向的文件不存在：{e.FileRelPath}（条目 {e.Signature}）");
        }

        // C. 直接读 vectors.bin 第 0 条，验证维度 == dim
        if (entries.Count > 0)
        {
            using var vec = VectorsBin.Open(vectorsPath);
            var v0 = vec.GetVector(0);
            if (v0.Length != dim)
                errors.Add($"vectors.bin 第 0 条维度({v0.Length}) != dim({dim})");
        }

        return errors;
    }
}