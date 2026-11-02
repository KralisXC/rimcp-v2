namespace RiMCP.Indexer.Graph;

/// <summary>CSR 上的经典 PageRank 幂迭代（含随机跳转与悬挂节点重分配）。</summary>
internal static class PageRank
{
    /// <summary>
    /// 计算 pagerank。输入 CSR（行优先）的行指针/列索引。
    /// 返回按节点序号索引的得分数组。
    /// </summary>
    public static double[] Calculate(int nodeCount, int[] rowPointers, int[] columnIndices,
        int maxIterations = 100, double damping = 0.85, double tolerance = 1e-8)
    {
        var scores = new double[nodeCount];
        var next = new double[nodeCount];
        if (nodeCount == 0) return scores;

        double init = 1.0 / nodeCount;
        Array.Fill(scores, init);

        // 该行是否有出边（决定是否是悬挂节点）
        var hasOut = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            hasOut[i] = rowPointers[i + 1] > rowPointers[i];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double danglingScore = 0.0;
            for (int i = 0; i < nodeCount; i++)
                if (!hasOut[i]) danglingScore += scores[i];

            double addBase = (danglingScore * damping) / nodeCount;      // 悬挂节点均摊
            double baseScore = (1.0 - damping) / nodeCount;

            Array.Clear(next);
            for (int i = 0; i < nodeCount; i++)
            {
                if (!hasOut[i]) continue;
                int start = rowPointers[i];
                int end = rowPointers[i + 1];
                if (start == end) continue;
                double share = damping * scores[i] / (end - start);
                for (int k = start; k < end; k++)
                    next[columnIndices[k]] += share;
            }

            for (int i = 0; i < nodeCount; i++)
                next[i] += baseScore + addBase;

            // 归一化范数并判断收敛
            double norm = 0.0;
            for (int i = 0; i < nodeCount; i++)
            {
                double d = next[i] - scores[i];
                norm += d * d;
            }
            (scores, next) = (next, scores);
            if (norm < tolerance) break;
        }

        return scores;
    }
}