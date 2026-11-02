# models

本目录用于存放索引构建与 MCP 查询所需的 **ONNX 嵌入模型**。模型文件体积较大，且受各自许可约束，因此整个目录的内容已被 `.gitignore` 排除，不会上传到 Git。

请将 `e5-base-v2-onnx` 模型文件夹放置于此目录下，最终结构如下：

```text
models/
└─ e5-base-v2-onnx/
   ├─ model.onnx
   └─ tokenizer.json
```

## 必备文件

| 文件 | 说明 |
| --- | --- |
| `model.onnx` | `intfloat/e5-base-v2` 导出的 ONNX 模型，固定输出 768 维向量 |
| `tokenizer.json` | 与模型配套的 HuggingFace WordPiece 分词器 |

缺少任一文件时，Launcher 顶部的“模型目录”会亮红灯，Indexer 与 McpServer 也会在启动时直接报错。

## 获取方式

推荐用 `optimum-cli` 从 HuggingFace 导出：

```bash
pip install optimum[onnxruntime]
optimum-cli export onnx --model intfloat/e5-base-v2 models/e5-base-v2-onnx
```

导出后请确认目录中存在 `model.onnx` 与 `tokenizer.json`。若分词器文件名不同，请重命名为 `tokenizer.json`，或参考源码中的 `WordPieceTokenizer` 调整实现。

## 路径约定

- 默认路径：`<RiMCP_v2 根>/models/e5-base-v2-onnx`
- Indexer：可用 `--model-dir <目录>` 覆盖
- McpServer / Launcher：可用环境变量 `RIMCP_MODEL_PATH` 覆盖
- 索引侧与查询侧必须使用**同一份**模型，否则向量空间不一致，检索结果会失准
