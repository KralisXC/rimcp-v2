using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RiMCP.McpServer.Services;

/// <summary>
/// 查询侧嵌入：把用户查询文本编码为与索引侧同分布的 Half 向量。
/// 复用 <see cref="IndexService"/> 已加载的 ONNX 会话与 tokenizer，
/// 仅添加 e5 要求的 "query: " 前缀后做一次 forward + mean pooling + L2 归一化。
/// </summary>
internal sealed class QueryEmbedding
{
    /// <summary>e5 检索场景要求查询侧统一加此前缀（索引侧为 "passage: "）。</summary>
    private const string QueryPrefix = "query: ";

    /// <summary>ONNX 输出张量名。</summary>
    private const string OutputName = "last_hidden_state";

    private readonly IndexService _index;

    public QueryEmbedding(IndexService index) => _index = index;

    /// <summary>编码单条查询为归一化 Half 向量（长度 = Dim）。</summary>
    public Half[] Embed(string query)
    {
        int dim = _index.Dim;
        int maxLen = IndexService.MaxLength;

        // 单条查询，构造 1×maxLen 输入
        var tokenized = _index.Tokenizer.Encode(QueryPrefix + (query ?? string.Empty), maxLen);

        var inputIds = new DenseTensor<long>(new long[1 * maxLen], new[] { 1, maxLen });
        var attentionMask = new DenseTensor<long>(new long[1 * maxLen], new[] { 1, maxLen });
        for (int s = 0; s < maxLen; s++)
        {
            inputIds[0, s] = tokenized.InputIds[s];
            attentionMask[0, s] = tokenized.AttentionMask[s];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor<long>(_index.MainInputName, inputIds),
            NamedOnnxValue.CreateFromTensor<long>("attention_mask", attentionMask),
        };
        if (_index.NeedsTokenTypeIds)
        {
            var tti = new DenseTensor<long>(new long[1 * maxLen], new[] { 1, maxLen });
            inputs.Add(NamedOnnxValue.CreateFromTensor<long>("token_type_ids", tti));
        }

        using var results = _index.Onnx.Run(inputs);
        var output = results.First(o => string.Equals(o.Name, OutputName, StringComparison.OrdinalIgnoreCase))
                           .AsTensor<float>()
                           .ToDenseTensor();
        var hidden = output.Buffer.ToArray();
        var maskFlat = new int[1 * maxLen];
        for (int s = 0; s < maxLen; s++) maskFlat[s] = tokenized.AttentionMask[s];

        var pooled = MeanPooling.PoolAndNormalize(hidden, 1, maxLen, dim, maskFlat);
        return pooled[0];
    }
}