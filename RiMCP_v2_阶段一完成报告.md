# RiMCP_v2 阶段一完成报告

> 范围：把索引改造为“分片 + 增量”模式的第一阶段（分片目录 + manifest）。
> 未进入阶段二（增量构建）和阶段三（WPF Launcher）。

---

## 一、阶段一目标（回顾）

1. `index/` 拆成 `core/`、`dlc_*/` 子目录，每个分片独立存放 `symbols.tsv`、`vectors.bin`、`lucene/`、`graph.*`。
2. 每个分片一份 `manifest.json`，记录游戏版本、来源哈希、构建者、provider、精度、模型以及五层（defs/symbols/graph/vectors/lucene）的 hash 与时间。
3. 顶层 `build.json` 只汇总分片 manifest 指针，保留 schema 与格式版本。
4. 构建按分片写产物；启动时逐分片比对 manifest，只打印诊断，不自动跳过。
5. 共享约束：判定逻辑必须集中在 `RiMCP.Core`，Indexer / MCP（未来 Launcher）复用同一套实现。

---

## 二、已经完成并落地的内容

### 1. Core（共享层）

| 文件 | 作用 |
|---|---|
| `src/RiMCP.Core/Indexing/ShardLayout.cs` | 分片命名唯一来源：`core` / `dlc_<小写>` |
| `src/RiMCP.Core/Storage/ShardManifest.cs` | 分片 manifest 模型与读写、文件 SHA-256 |
| `src/RiMCP.Core/Indexing/ShardCatalog.cs` | 分片发现、来源哈希、层哈希推导、游戏版本检测 |
| `src/RiMCP.Core/Indexing/ManifestComparer.cs` | 唯一“是否需要重建”判定逻辑，输出状态与不匹配层 |
| `src/RiMCP.Core/Storage/BuildJson.cs` | 格式版本升到 2，新增 `shards` 指针，旧格式明确提示全量重建 |

关键细节：

- 分片发现规则：`core` = `Data/Core` + `Data/*.xml` 顶层；DLC = `Data` 下除 `Core` 的子目录，按目录名排序编号。
- 层哈希是逻辑哈希，不是文件哈希：
  - `defs = H(defs|sourceHash)`
  - `symbols = H(symbols|defs|parserRevision)`
  - `graph = H(graph|symbols|graphRevision)`
  - `vectors = H(vectors|symbols|model|dim|precision|prepRevision)`
  - `lucene = H(lucene|symbols|luceneRevision)`
- 来源哈希只覆盖 XML + 程序集/反编译源码，不包含 `Assets.zip` 等大资源。
- `ManifestComparer` 比对 schema、formatVersion、分片名/序号、`truncated`、gameVersion、embeddingModel、precision、sourceHash 与五层 hash；`onnxProvider` 不同只提示、不判定重建。
- 旧索引迁移：`BuildJson.Validate` 遇到旧 formatVersion 或缺 `shards` 时抛出“旧格式索引不兼容，请执行全量重建”。

### 2. Indexer

| 文件 | 改动 |
|---|---|
| `src/RiMCP.Indexer/Building/ShardBuilder.cs` | 新增：单分片全量构建 + 仅图构建 |
| `src/RiMCP.Indexer/Program.cs` | 重写：分片循环、全局 BaseId、构建前诊断、顶层 build.json 写指针 |
| `src/RiMCP.Indexer/Parsing/XmlParser.cs` | 新增 `ParseFiles(rootAbs, files, parallelism)`，按显式文件集解析 |
| `src/RiMCP.Indexer/Graph/GraphBuilder.cs` | 改为按分片目录构图，支持 `includeCSharp` 开关 |
| `src/RiMCP.Indexer/Lucene/LuceneIndexBuilder.cs` | 新增 `Add(entry, globalId)`，文档 id 写全局 id |

行为要点：

- 全局符号 id = 分片 `BaseId` + 本地 id，保证 0..N-1 连续，MCP 现有工具循环无需改动。
- Lucene 文档 id 在构建时即写全局 id，`EntryOf(id)` 与 `Vectors.GetVector(id)` 直接可用。
- `--max-entries` 保持“总量”语义：跨分片共享剩余预算；预算耗尽后的分片写空分片并标 `truncated=true`。
- `--graph-only` 从已有 `symbols.tsv` 构图，不加载 ONNX，不改 `sourceHash`，只更新 graph 层时间与 build.json 边数/指针哈希。
- `--dry-run` 只解析统计，不加载模型、不落盘。

### 3. McpServer（只改加载逻辑，工具签名与返回结构零改动）

| 文件 | 改动 |
|---|---|
| `src/RiMCP.McpServer/Services/IndexService.cs` | 按 build.json 指针逐分片加载；本地 id → 全局 id；`MultiReader` 合并 Lucene；启动打印 `[manifest]` 诊断 |
| `src/RiMCP.McpServer/Services/ShardedVectors.cs` | 新增：多分片向量门面，保持 `GetVector(globalId)` 语义 |
| `src/RiMCP.McpServer/Services/GraphService.cs` | 按分片加载 CSR/CSC/PageRank，全局 id ↔ 分片本地 id 双向映射 |

加载校验：manifest 分片名/序号、`BaseId` 连续性、符号条数、向量条数、维度与 build.json 一致，不一致直接报错并给出重建提示。

---

## 三、验证做了什么、结果如何

| 验证项 | 结果 |
|---|---|
| Release 编译 | `dotnet build src\RiMCP.slnx -c Release` 通过，0 警告 0 错误 |
| 分片发现 + dry-run | 6 分片，共 153,900 条符号 |
| 冒烟分片构建 | `--max-entries 200` 生成 6 个分片目录，产物齐全 |
| MCP 启动诊断 | 实际输出 6 行 `[manifest] ...`（本次为“截断样本，需全量重建”） |
| MCP 工具 | 8 个工具均可调用，返回结构未变 |
| 跨分片 Lucene + 向量 | 查询 `pawn` 返回 id 0/20/21，余弦分数 0.7944 / 0.7989 / 0.8181 |
| 全局 id 寻址 | `get_rim_world_source #0` 正确读到 `DistressCallUtility` 源码切片 |
| `--graph-only` | 5.47s 完成，未加载 ONNX；core 184 边；仅 graph 层与分片 `builtAt` 更新 |

dry-run 分片明细：

| 分片 | 符号数 |
|---|---:|
| core | 125,614 |
| dlc_anomaly | 6,192 |
| dlc_biotech | 5,463 |
| dlc_ideology | 7,268 |
| dlc_odyssey | 6,214 |
| dlc_royalty | 3,149 |
| 合计 | 153,900 |

`get_rim_world_stats` 实测返回：`symbols=200, nodes=200, edges=184, dim=768, formatVersion=2`。

---

## 四、明确没有做的事

### 阶段二（全部未开始）

- 未实现符号级内容哈希差分（规范化文本 SHA-256，含 `embeddingModel + dimensions + preprocessing revision`）。
- 未实现“内容哈希 → 向量 id”映射表（SQLite 或追加式 TSV）。
- 未实现增量嵌入：未变文本块没有复用旧 `vectors.bin` 记录，仍是整分片重算。
- 未实现删除/改名符号的旧向量淘汰。
- 未实现分层跳过（defs → symbols → graph → vectors → lucene 的逐层 short-circuit）。
- 未实现检查点 / 断点续跑。
- 未新增 `--incremental`（默认）与 `--force-clean`；当前每次构建都等同全量。
- 未实现 MCP 按“当前启用分片”加载、core + DLC 覆盖层、同名 Def 覆盖、跨分片符号引用。当前 MCP 固定加载 build.json 中的全部分片。
- 因此 `--graph-only` 尚未接入“文本未变则只走图”的自动化判断（现在需要人工显式指定）。

### 阶段三（全部未开始）

- 未创建 `src/RiMCP.Launcher` WPF 项目，没有任何 UI（环境检测条、分片状态表、操作区、日志区、服务区均无）。
- 未实现进程调度、取消、日志流、MCP 启停、provider 临时覆盖与记忆。

### 其它未完成项

- **未跑正式全量构建**。当前 `index/` 是 `--max-entries 200` 的截断样本（`truncated=true`），只能用于结构验证，不能当作可用索引。
- DLC 分片当前只生成分片内图边；跨分片继承 / Class 绑定边按计划留到阶段二。
- 未做“改造前 vs 改造后”同一查询结果的逐条比对（缺少改造前全量索引基线）。
- 未更新 `scripts/build-and-index.ps1` 等外部脚本（脚本未纳入本次阶段一改动范围）。
- 工作区不是 git 仓库，无法查看 diff 或回滚；冒烟构建已覆盖原 `index/`，且没有备份。

---

## 五、注意事项与已知现象

1. `index/` 现在是截断样本，MCP 会正常启动但只有 200 条符号；正式使用前必须全量重建。
2. Indexer 的 `--root` 参数语义是 **RimWorldData 目录**，不是仓库根；传错会得到 `XML Def 目录不存在：...\Data`。
3. 控制台里中文诊断显示为乱码，是 PowerShell 捕获 stderr 时的代码页问题；程序内部字符串为 UTF-8。真实 MCP 客户端的显示尚未实测。
4. `search_rim_world_code` 对驼峰命中的词（如 `utility`）可能返回 0，因为 StandardAnalyzer 不做驼峰切分；这是既有行为，非本次改动引入。
5. `onnxProvider` 变化只做提示、不触发重建，这是有意设计（同模型换 provider 不需要重算向量）。

---

## 六、建议的下一步

1. 先做阶段二：内容哈希差分 + 增量嵌入 + 分层跳过 + 检查点，再把 `--incremental` 设为默认、`--force-clean` 保留全量。
2. 阶段二完成后跑一次正式全量构建作为基线，再改一个 Def/方法验证“只处理变动符号”。
3. 最后做阶段三 Launcher，直接复用 `ShardCatalog` + `ManifestComparer`，不在 UI 侧重写判定规则。
