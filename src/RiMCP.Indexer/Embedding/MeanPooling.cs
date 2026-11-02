// Portions of this file are based on RiMCP_hybrid
// Original: https://github.com/h7lu/RiMCP_hybrid
// Licensed under MIT License
namespace RiMCP.Indexer.Embedding;

/// <summary>
/// 对 last_hidden_state[batch, seq, dim] 做 mean pooling（按 attention_mask 忽略 padding），
/// 再 L2 归一化。最终返回 float16 向量数组，供写入 vectors.bin。
/// </summary>
public static class MeanPooling
{
    /// <summary>
    /// 将 ONNX 输出池化为归一化的 Half 向量。
    /// </summary>
    /// <param name="hidden">展平的 last_hidden_state，长度 = batch × seqLen × dim。</param>
    /// <param name="batch">首维尺寸。</param>
    /// <param name="seqLen">序列长度。</param>
    /// <param name="dim">隐藏维（e5 = 768）。</param>
    /// <param name="attentionMask">展平 attention_mask，长度 = batch × seqLen，值为 0/1。</param>
    public static Half[][] PoolAndNormalize(float[] hidden, int batch, int seqLen, int dim, int[] attentionMask)
    {
        var result = new Half[batch][];

        for (int b = 0; b < batch; b++)
        {
            int count = 0;
            for (int s = 0; s < seqLen; s++)
                count += attentionMask[b * seqLen + s];

            count = Math.Max(1, count); // 防除零

            var pooled = new float[dim];
            int hiddenBase = b * seqLen * dim;
            int maskBase = b * seqLen;

            for (int s = 0; s < seqLen; s++)
            {
                int m = attentionMask[maskBase + s];
                if (m == 0) continue;
                int rowBase = hiddenBase + s * dim;
                for (int d = 0; d < dim; d++)
                    pooled[d] += hidden[rowBase + d];
            }

            // 均值 + L2 归一化
            float normSq = 0f;
            for (int d = 0; d < dim; d++)
            {
                pooled[d] /= count;
                normSq += pooled[d] * pooled[d];
            }

            float norm = MathF.Sqrt(normSq);
            if (norm < float.Epsilon) norm = 1f;

            var half = new Half[dim];
            for (int d = 0; d < dim; d++)
                half[d] = (Half)(pooled[d] / norm);

            result[b] = half;
        }

        return result;
    }
}