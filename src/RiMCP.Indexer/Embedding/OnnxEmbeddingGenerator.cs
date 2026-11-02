using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RiMCP.Core.Support;

namespace RiMCP.Indexer.Embedding;

/// <summary>
/// ONNX 批量嵌入。加载 e5-base-v2 model.onnx，按批次编码 → 池化 → 归一化 → Half 向量。
/// 运行时用 <see cref="InferenceSession.InputMetadata"/> 探测输入，适配可选 token_type_ids。
/// </summary>
public sealed class OnnxEmbeddingGenerator : IDisposable
{
    /// <summary>e5 检索场景要求文本以 "passage: "（索引侧）/ "query: "（查询侧）前缀。</summary>
    private const string PassagePrefix = "passage: ";

    private readonly InferenceSession? _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly int _batchSize;
    private readonly int _maxLength;
    private readonly int _dim;
    private readonly bool _needsTokenTypeIds;

    /// <summary>输出张量是 float32（ONNX 输出），池化后转 Half。</summary>
    private const string OutputName = "last_hidden_state";

    public OnnxEmbeddingGenerator(string modelFile, string tokenizerPath, int dim, int batchSize = 32, int maxLength = 256, string provider = "cpu")
    {
        _tokenizer = new WordPieceTokenizer(tokenizerPath);
        _batchSize = batchSize;
        _maxLength = maxLength;
        _dim = dim;

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        // 解析最终后端：显式参数 > RIMCP_ONNX_PROVIDER > cuda；再挂载执行提供方
        var resolved = OnnxProviderResolver.Resolve(provider);
        ApplyProvider(options, resolved);
        Console.WriteLine($"[ONNX] provider = {resolved}");

        _session = new InferenceSession(modelFile, options);

        // 探测输入名，决定是否需 token_type_ids（全 0）
        var inputNames = _session.InputMetadata.Keys.ToList();
        _needsTokenTypeIds = inputNames.Contains("token_type_ids", StringComparer.Ordinal);
        MainInputName = inputNames.First(n =>
            n.Equals("input_ids", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("input_ids", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>主输入名（通常 input_ids）。</summary>
    public string MainInputName { get; }

    public int BatchSize => _batchSize;
    public int MaxLength => _maxLength;
    public int Dim => _dim;

    /// <summary>按后端挂载 ONNX 执行提供方。DirectML 仅在该后端编译时（DIRECTML 符号）可用。</summary>
    private static void ApplyProvider(SessionOptions options, string provider)
    {
        try
        {
            switch (provider)
            {
                case "cuda":
                    options.AppendExecutionProvider_CUDA(0);
                    break;
#if DIRECTML
                case "directml":
                    options.AppendExecutionProvider_DML(0);
                    break;
#endif
                case "cpu":
                default:
                    // 默认 CPU，无需附加执行提供方
                    break;
            }
        }
        catch (Exception ex)
        {
            // 后端不可用：打印后继续（CPU 作为兜底，由 ORT 自动选择）
            Console.Error.WriteLine($"[ONNX] 无法启用 {provider}，将回退 CPU：{ex.Message}");
        }
    }

    /// <summary>批文本 → Half 向量。文本会统一加 "passage: " 前缀。</summary>
    public Half[][] Embed(IReadOnlyList<string> texts)
    {
        GuardSession();
        var result = new Half[texts.Count][];

        for (int b = 0; b < texts.Count; b += _batchSize)
        {
            var slice = texts.Skip(b).Take(_batchSize).ToArray();
            var half = EmbedBatch(slice);
            Array.Copy(half, 0, result, b, half.Length);
        }

        return result;
    }

    /// <summary>单批嵌入。构造 DenseTensor 输入并运行会话。</summary>
    private Half[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        GuardSession();
        int batch = texts.Count;
        var inputIds = new DenseTensor<long>(new long[batch * _maxLength], new[] { batch, _maxLength });
        var attentionMask = new DenseTensor<long>(new long[batch * _maxLength], new[] { batch, _maxLength });

        for (int b = 0; b < batch; b++)
        {
            // 索引侧统一 passage 前缀去检索；查询侧由查询方自行加 query 前缀。
            var tokenized = _tokenizer.Encode(PassagePrefix + texts[b], _maxLength);
            for (int s = 0; s < _maxLength; s++)
            {
                inputIds[b, s] = tokenized.InputIds[s];
                attentionMask[b, s] = tokenized.AttentionMask[s];
            }
        }

        // e5 onnx 输入为 int64
        var namedInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor<long>(MainInputName, inputIds),
            NamedOnnxValue.CreateFromTensor<long>("attention_mask", attentionMask),
        };
        if (_needsTokenTypeIds)
        {
            var tti = new DenseTensor<long>(new long[batch * _maxLength], new[] { batch, _maxLength });
            namedInputs.Add(NamedOnnxValue.CreateFromTensor<long>("token_type_ids", tti));
        }

        using var results = _session!.Run(namedInputs);
        var outputTensor = results.First(o => string.Equals(o.Name, OutputName, StringComparison.OrdinalIgnoreCase))
                                 .AsTensor<float>();
        var dense = outputTensor.ToDenseTensor();

        var hidden = dense.Buffer.ToArray();
        var maskFlat = new int[batch * _maxLength];
        for (int b = 0; b < batch; b++)
            for (int s = 0; s < _maxLength; s++)
                maskFlat[b * _maxLength + s] = (int)attentionMask[b, s];

        return MeanPooling.PoolAndNormalize(hidden, batch, _maxLength, _dim, maskFlat);
    }

    private void GuardSession()
    {
        if (_session is null)
            throw new ObjectDisposedException(nameof(OnnxEmbeddingGenerator));
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}