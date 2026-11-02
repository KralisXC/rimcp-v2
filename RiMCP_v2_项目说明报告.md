# RiMCP_v2 项目说明报告

> 版本 0.1.0 · .NET 8 · MIT License  
> 项目根目录：`E:\KMODS\RiMCP_v2_3`

## 1. 项目是什么

RiMCP_v2 是一个 **RimWorld 代码索引 + MCP 服务器**，用于 AI 辅助 Mod 开发。

它把 RimWorld 的反编译 C# 源码和 XML Def 扫描成一套可检索的本地索引，再通过 **Model Context Protocol（MCP）** 把这些能力提供给 Trae、Claude Desktop 等 AI 客户端。AI 不需要直接读几 GB 源码，就能按名称、语义、引用关系快速找到目标代码和 Def。

## 2. 解决什么问题

RimWorld Mod 开发大量依赖 `Verse` / `RimWorld` 的类、方法、字段以及各种 `*Def` XML 结构。源码体积大、类名相似、继承和引用关系复杂，AI 直接全文搜索既慢又容易找错。

本项目把源码切分为约 15 万个“符号”（类、方法、属性、字段、枚举值、XML Def 等），同时建立四类索引：

- 符号表：按 id / 签名 / 短名精确反查
- 语义向量：自然语言描述也能搜到相关代码
- Lucene 全文索引：关键词粗筛
- 知识图谱：谁引用谁、继承关系、XML Def 与 C# 类的绑定

## 3. 目录与详细位置

| 路径 | 作用 |
|---|---|
| `README.md` | 项目总说明、安装步骤、工具列表 |
| `scripts/install.bat` | 一键安装入口（调用 `install.ps1`） |
| `scripts/install.ps1` | 检测环境、准备模型、准备 RimWorldData、编译、建索引、生成 MCP 配置 |
| `scripts/build-and-index.ps1` | 单独构建索引，支持 `-BatchSize`、`-ForceClean`、`-OnnxProvider` |
| `scripts/start-mcp.bat` | 手动前台启动 MCP 服务器，便于看日志调试 |
| `scripts/update-index.bat` | 清空并重建索引 |
| `src/RiMCP.slnx` | 解决方案文件，包含 4 个项目 |
| `src/RiMCP.Core` | 共享数据层：模型、存储格式、路径工具 |
| `src/RiMCP.Indexer` | 索引构建流水线 |
| `src/RiMCP.McpServer` | MCP stdio 服务器与 8 个工具 |
| `src/RiMCP.CoreTests` | 核心数据层测试（控制台测试程序） |
| `models/e5-base-v2-onnx/` | ONNX 嵌入模型与分词器，`model.onnx` 约 416 MB |
| `RimWorldData/Data/` | RimWorld XML Def，当前约 1,676 个 XML |
| `RimWorldData/RimWorld_Decompiled/Assembly-CSharp/` | 反编译 C# 源码，当前约 9,262 个 `.cs` |
| `index/` | 索引输出目录（运行 Indexer 后生成，当前尚未生成） |

### 核心源码位置

| 模块 | 关键文件 |
|---|---|
| 符号模型 | `src/RiMCP.Core/Models/SymbolEntry.cs`、`SymbolKind.cs`、`SourceType.cs` |
| TSV 存储 | `src/RiMCP.Core/Storage/SymbolsTsv.cs` |
| 向量存储 | `src/RiMCP.Core/Storage/VectorsBin.cs`（mmap + float16） |
| 构建清单 | `src/RiMCP.Core/Storage/BuildJson.cs` |
| id 分配 / 内存索引 | `src/RiMCP.Core/Indexing/IdAllocator.cs`、`SymbolIndex.cs` |
| 路径解析 | `src/RiMCP.Core/Paths/PathUtil.cs` |
| ONNX 后端选择 | `src/RiMCP.Core/Support/OnnxProviderResolver.cs` |
| 索引主流程 | `src/RiMCP.Indexer/Program.cs` |
| 索引配置与 CLI | `src/RiMCP.Indexer/Config/IndexerConfig.cs` |
| C# 解析 | `src/RiMCP.Indexer/Parsing/CSharpParser.cs` |
| XML 解析 | `src/RiMCP.Indexer/Parsing/XmlParser.cs` |
| ONNX 嵌入 | `src/RiMCP.Indexer/Embedding/OnnxEmbeddingGenerator.cs`、`WordPieceTokenizer.cs`、`MeanPooling.cs` |
| Lucene 索引 | `src/RiMCP.Indexer/Lucene/LuceneIndexBuilder.cs` |
| 图构建 / PageRank | `src/RiMCP.Indexer/Graph/GraphBuilder.cs`、`GraphEdge.cs`、`PageRank.cs` |
| 构建后自检 | `src/RiMCP.Indexer/Verification/SelfCheck.cs` |
| MCP 启动与 DI | `src/RiMCP.McpServer/Program.cs` |
| 检索服务 | `src/RiMCP.McpServer/Services/IndexService.cs` |
| 图查询服务 | `src/RiMCP.McpServer/Services/GraphService.cs` |
| 查询向量 | `src/RiMCP.McpServer/Services/QueryEmbedding.cs` |
| 工具实现 | `src/RiMCP.McpServer/Tools/SearchTools.cs`、`SourceTools.cs`、`GraphTools.cs`、`DefTools.cs`、`StatsTools.cs` |

## 4. 工作流程

### 建索引（离线，一次）

1. **解析**：Roslyn 解析 C#，`XDocument` 解析 XML Def。C# 默认只扫描 `RimWorld`、`Verse`、`LudeonTK` 三个顶级命名空间目录。
2. **分配 id**：按 `(Source, Kind, Namespace, Signature)` 排序，统一分配 `0..N-1`，保证 `symbols.tsv`、`vectors.bin`、`graph.nodes.tsv` 行号一致。
3. **写符号表**：输出 `index/symbols.tsv`。
4. **语义嵌入**：e5-base-v2 ONNX，索引侧加 `passage: ` 前缀，mean pooling + L2 归一化，float16 写入 `index/vectors.bin`。
5. **Lucene 索引**：对 `signature`、`shortName`、`preview` 建全文索引到 `index/lucene/`。
6. **构建图**：生成 CSR/CSC 稀疏图、节点表、PageRank、元信息。
7. **写清单与自检**：生成 `build.json`，核对符号数、向量条数、维度、抽样文件是否存在。

### 查询（在线，毫秒级）

`SearchRimWorldCode` 先用 Lucene 粗筛候选，再用查询向量余弦相似度重排；其他工具直接走符号表或 CSR/CSC 图。

## 5. 索引产物（`index/`）

| 文件 | 内容 |
|---|---|
| `build.json` | schema、格式版本、维度、模型指纹、符号/节点/边计数、各类产物的相对路径 |
| `symbols.tsv` | 每行一个符号：id、kind、source、短名、命名空间、签名、文件相对路径、起止行、父引用、预览 |
| `vectors.bin` | 64 字节头 + 定长记录（id + 768 维 float16），支持 mmap 随机读取 |
| `lucene/` | Lucene 全文索引目录 |
| `graph.nodes.tsv` | 节点 id → 签名 |
| `graph.csr.bin` | 行优先出边（“它依赖了谁”） |
| `graph.csc.bin` | 列优先入边（“谁依赖了它”） |
| `graph.pagerank.tsv` | 每个符号的 PageRank 分数 |
| `graph.meta.json` | 节点数、边数、边类型列表 |

## 6. MCP 工具

| 工具 | 用途 |
|---|---|
| `search_rim_world_code` | Lucene 粗筛 + 向量重排，自然语言/关键词搜代码 |
| `search_symbol` | exact / prefix / fuzzy 按名称查符号 |
| `get_rim_world_source` | 按 `#id`、完整签名或短名读取源码，自动清理反编译注释 |
| `get_rim_world_stats` | 返回符号数、节点数、边数、维度、模型、Lucene 状态 |
| `get_rim_world_uses` | 下游依赖：某符号用了谁，按 PageRank 排序 |
| `get_rim_world_used_by` | 上游引用：谁用了某符号，用于评估改动影响范围 |
| `get_rim_world_inheritors` | 继承树：`down` 子类 / `up` 基类 |
| `list_defs` | 列出某类 XML Def，如 `ThingDef`、`RecipeDef` |

图工具都支持 `maxResults`（默认 20，范围 1–100），返回中带 `totalCount`、`returnedCount`、`truncated`，便于判断是否被截断。

## 7. 图关系

边类型固定 9 种，编码顺序不可改：

`inherits`、`implements`、`calls`、`references`、`xml-inherits`、`xml-references`、`xml-binds-class`、`xml-uses-comp`、`csharp-used-by-def`。

当前实际生成的主要是 C# 继承/实现/调用/引用、XML `ParentName` 继承、XML `Class` 属性绑定 C# 类，以及对应的反向边。

## 8. 配置与使用

### MCP 客户端配置

```jsonc
{
  "mcpServers": {
    "rimcp": {
      "command": "dotnet",
      "args": ["run", "--project", "E:/KMODS/RiMCP_v2_3/src/RiMCP.McpServer"],
      "env": {
        "RIMCP_ROOT": "E:/KMODS/RiMCP_v2_3",
        "RIMCP_MODEL_PATH": "E:/KMODS/RiMCP_v2_3/models/e5-base-v2-onnx"
      }
    }
  }
}
```

### 环境变量

- `RIMCP_ROOT`：项目根目录，必须同时包含 `RimWorldData` 与 `models`
- `RIMCP_MODEL_PATH`：ONNX 模型目录
- `RIMCP_ONNX_PROVIDER`：`cuda` / `directml` / `cpu`

### Indexer 常用参数

`--root`、`--output`、`--model-dir`、`--batch-size`、`--max-length`、`--onnx-provider`、`--max-parallel`、`--include-roots`、`--csharp-subdir`、`--dry-run`、`--graph-only`、`--max-entries`。

其中：

- `--dry-run`：只解析并统计，不加载模型、不落盘
- `--max-entries N`：只取前 N 条，用于冒烟测试
- `--graph-only`：只根据已有 `symbols.tsv` 重建图，约 40 秒

## 9. 环境要求

- .NET 8 SDK
- NVIDIA 显卡建议安装 CUDA 13.0 + cuDNN 9（GPU 加速）
- 准备 `RimWorldData` 需要 RimWorld 游戏本体与 ILSpy
- 嵌入模型为 `intfloat/e5-base-v2` 转出的 ONNX

参考耗时（15 万符号）：

| 硬件 | 索引构建 |
|---|---|
| NVIDIA 6GB+ | 约 40 分钟–1 小时 |
| AMD / Intel 独显（DirectML，实验性） | 约 1–2 小时 |
| 纯 CPU | 约 2–5 小时 |

日常查询在所有硬件上都是毫秒级，只有首次建索引耗时不同。

## 10. 当前状态与注意事项

- 本目录中模型与 RimWorldData 已就绪，但 `index/` 尚未生成，需要先运行 Indexer 或 `scripts/build-and-index.ps1`。
- `src/RiMCP.McpServer/Program.cs` 中 `RIMCP_ROOT` 的默认值仍是作者机器路径，部署到其他机器时必须通过环境变量覆盖。
- RimWorld 源码与资源版权归 Ludeon Studios，项目只在本机读取，不应随项目分发。
- README 提到的 `LICENSE` 与 `THIRD_PARTY_LICENSES/` 在当前目录中未见，对外分发前建议补齐。
- 测试项目是控制台程序，运行方式为 `dotnet run --project src/RiMCP.CoreTests`，不是 xUnit 测试。

## 11. 许可与致谢

项目采用 MIT License。部分 C# 解析、图构建、Lucene 索引代码参考/移植自 [RiMCP_hybrid](https://github.com/h7lu/RiMCP_hybrid)；嵌入模型来自 Microsoft Research 的 e5-base-v2；同时使用 Lucene.Net、ONNX Runtime 和 Model Context Protocol。
