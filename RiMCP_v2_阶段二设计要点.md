# RiMCP_v2 阶段二设计要点（开工前确认版）

> 本文件只记录阶段二的**已定方案**，不包含实现代码。
> 阶段一代码已提交：commit `572dfd3`。

---

## A. 跨分片边：全局符号表合并 + 统一构图

### 数据流

1. **分片解析**（按分片并行）
   - 每分片产出本地 `SymbolEntry` 列表，此阶段只分配**临时 localSeq**，不写全局 id。
2. **全局符号表合并**（单次，集中执行）
   - 全局 id 分配顺序：`core` 占 `0..Nc-1`，DLC 按 `shardIndex` 依次追加 —— 与阶段一一致，保证 MCP 已有 id 稳定。
   - 产出 `signature → globalId` 映射。
   - **同名 XML Def 覆盖**：高 `shardIndex` 的分片覆盖低序分片的映射项；覆盖只影响“端点解析”和“查询 overlay”，**不删除** core 条目（`symbols.tsv` 仍保留，搜索结果可列出两条）。
   - 产出 `(shard, localSeq) → globalId` 映射，供构图与 Lucene 写入。
3. **统一构图**（单次，不再按分片构图）
   - 节点 = 全部符号（全局 id 连续 0..N-1）。
   - 边源通过 `(shard, localSeq)` 映射定位；边端点全部经全局映射解析，**允许跨分片**（DLC Def 继承 core Def、DLC Def 绑定 core C# 类等）。
   - 产物迁移到顶层全局图目录：

     ```
     index/graph/graph.csr.bin
     index/graph/graph.csc.bin
     index/graph/graph.nodes.tsv
     index/graph/graph.pagerank.tsv
     index/graph/graph.meta.json
     ```

   - 阶段一的 `index/<shard>/graph.*` **弃用**；分片目录只保留 `symbols.tsv`、`vectors.bin`、`lucene/`、`manifest.json`。
4. **graph 层 hash 必须覆盖跨分片边**
   - `globalSymbolsHash = H( sorted(join(shard|symbolsHash)) )`
   - `graph = H("graph|" + globalSymbolsHash + "|graph=" + GraphRevision + "|cross-shard=1")`
   - 任一分片符号变化都会让全部 manifest 的 graph 层失配；跨分片边参与哈希输入，不会出现“分片 hash 都没变但图其实变了”的漏判。
5. **manifest 与 build.json**
   - 每个分片 `layers.graph` 记录**同一个**全局图 hash 与统一 `builtAt`；
   - 顶层 `build.json` 的 `paths` 增加 `graph: "index/graph"`，并记录全局图节点/边数。
6. **MCP 侧**
   - `GraphService` 改为加载单一全局图，节点 id 即全局 id，移除 `BaseId` 偏移与分片 PageRank 映射；PageRank 改为全局计算（顺带修复阶段一分片 PageRank 的近似误差）。
   - 工具签名与返回结构不变。
7. **兼容与迁移**
   - 格式版本 `build.json.formatVersion` 由 2 升到 **3**；`ShardManifest.formatVersion` 由 1 升到 **2**。
   - 检测到 v2 索引（存在 `index/<shard>/graph.*` 且无 `index/graph/`）→ 打印“旧格式索引不兼容，请执行全量重建”，不尝试自动迁移。

---

## B. vectors 增量判定：只用符号级内容哈希

### 硬约束

- **禁止**用分片级 `vectors` hash 决定是否跳过嵌入。
- 分片级 `vectors` hash 只写入 manifest 作诊断记录，不参与任何跳过判断。
- 跳过判定唯一依据：符号级内容哈希是否命中旧映射表。

### 内容哈希

```
contentHash = SHA256(
    "prep=<EmbeddingPreprocessingRevision>" +
    "|model=<embeddingModel>" +
    "|dim=<dimensions>" +
    "|precision=<precision>" +
    "|text=<normalizedText>"
)
```

- `normalizedText`：
  - C#：`signature + "\n" + preview`，统一行尾为 `\n`、去掉行尾空白、连续空行折叠。
  - XML Def：规范化后的 Def 文本，去掉缩进差异与注释，属性/元素顺序保持原样（顺序影响语义）。
- 模型名、维度、精度、预处理 revision 任一变化 → 全部哈希失效 → 自动全量。

### 映射表

- 路径：`index/embedding-cache.tsv`
- 结构：`contentHash \t globalId`（幂等、可整体重写）
- 构建时：
  1. 读旧表 + 旧 `build.json`，把 `globalId` 解析为 `(旧分片, 旧本地 id)`，定位旧 `vectors.bin` 记录。
  2. 逐符号计算 `contentHash`：
     - 命中且旧记录可读 → 直接复制该向量（不跑 ONNX）。
     - 未命中 → 进入当前批次送 ONNX。
  3. 全部完成后**整体重写**新表（只保留本次仍存在的 hash）。
- **淘汰**：删除/改名符号的哈希不再出现在新表中，其旧向量也不会写入新 `vectors.bin`，即自然淘汰；不做墓碑记录。

### 回退规则

| 条件 | 行为 |
|---|---|
| 无旧 `manifest.json` | `--incremental` 自动回退全量，并打印原因 |
| 旧格式（formatVersion 不符） | 自动回退全量，并打印原因 |
| 模型/维度/精度/预处理 revision 变化 | 自动回退全量（哈希天然全部失效，仍打印原因） |
| 显式 `--force-clean` | 强制全量，忽略映射表 |

- `--incremental` 为默认行为；现有 CLI 参数语义保持不变。

### 检查点与续跑

- 目录：`index/.build/`
- 每完成一个批次，追加写 `checkpoint.tsv`：`batchNo \t contentHash1,contentHash2,... \t writtenIds`
- 恢复时：
  1. 读 `checkpoint.tsv`，得到已提交批次与期望已写记录数；
  2. 校验当前 `vectors.bin` 的 id 从 0 连续、记录数 = 已提交批次的累计条数；
  3. 连续则从下一批继续；不连续则丢弃该检查点，嵌入层整体重跑；
  4. 检查点在同一构建成功后删除。
- 已处理哈希不重复计算：恢复时把 checkpoint 中的哈希视为已完成。

### 分层触发

构建步骤固定为：

```
Def 解析 → 符号提取 → 全局符号合并 → 嵌入计算 → Lucene → 统一构图
```

- 每层读上游 hash 决定跳过；下游 hash 与上游 hash 有明确的依赖链。
- `--graph-only` 接入 manifest 判断：
  - `defs/symbols` hash 变了但**文本内容哈希集合**没变 → 自动只走图，不碰嵌入与 Lucene。
  - 文本哈希也变了 → 明确提示需要走嵌入，拒绝“仅图”。

---

## C. MCP 分片启用：只读配置，不推断

- `build.json` 新增 `enabledShards: ["core", "dlc_royalty", ...]`，由 Indexer 在构建结束时写入（默认 = 本次已构建的全部启用分片）。
- MCP 启动时的解析顺序：
  1. 环境变量 `RIMCP_ENABLED_SHARDS`（逗号分隔）优先；
  2. 否则读 `build.json.enabledShards`；
  3. 两者都没有 → **报错并要求显式配置**，不自行推断“全部加载”。
- 只加载启用分片；`BaseId` 区间仍按 build.json 的**全量布局**保留，未启用分片的 id 段在查询层表现为空洞（`EntryOf` 返回 null），保证镜像切换时 id 不漂移。
- 启用的分片名校验：不在 build.json `shards` 中的名字视为配置错误，启动即报错。
- 查询顺序：先 `core`，再按 `enabledShards` 顺序合并覆盖层；同名 Def 覆盖规则与 A.2 一致。

---

## D. 阶段二验收口径（与需求一致）

1. 改一个 Def 数值或一个方法后重跑：嵌入只处理变动符号，总耗时分钟级。
2. `--graph-only` 在文本未变时由 manifest 正确触发。
3. 现有 MCP 工具查询结果不变（签名/结构/字段名）。
4. 中断后续跑不重复计算已完成批次。
5. 缺旧 manifest / 旧格式时自动回退全量并有明确提示。
6. 跨分片边在 DLC 覆盖 core Def 的场景下可被 `get_rim_world_uses` / `get_rim_world_used_by` 正确查到。
