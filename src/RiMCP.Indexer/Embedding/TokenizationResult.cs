// Portions of this file are based on RiMCP_hybrid
// Original: https://github.com/h7lu/RiMCP_hybrid
// Licensed under MIT License
namespace RiMCP.Indexer.Embedding;

/// <summary>
/// 分词结果：input_ids、attention_mask 与有效 token 数（含 CLS/SEP）。用于填充 ONNX 输入。
/// </summary>
internal readonly record struct TokenizationResult(int[] InputIds, int[] AttentionMask, int ActualLength);