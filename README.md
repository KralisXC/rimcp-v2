# RiMCP_v2

为 RimWorld Mod 开发打造的本地语义检索 + 全局知识图谱 MCP 服务。

通过 MCP（Model Context Protocol）把 Def、C# 源码、符号关系与跨分片知识图谱提供给 AI 客户端，让 AI 能准确回答“这个字段怎么用”“这个类被谁继承”“改这个值会影响什么”等问题，而不是靠猜。

## 推荐运行环境

| 组件 | 推荐版本 | 说明 |
| --- | --- | --- |
| 操作系统 | Windows 10 / 11 x64 | Launcher 为 WPF 应用 |
| .NET | **.NET 8 SDK** | 构建与运行必需 |
| NVIDIA 驱动 | 最新 Game Ready / Studio 驱动 | 使用 CUDA 时必需 |
| CUDA Toolkit | **12.4** | 与 cuDNN 9.x 配套 |
| cuDNN | **9.x** | 放置到系统 PATH 或 CUDA 目录 |
| 显卡 | 兼容新老架构 | 实测覆盖 RTX 4050 与 GTX 1080Ti |

没有 NVIDIA 显卡时，可在 Launcher 中切换执行提供器：

- **DirectML**：Windows 上通用 GPU 加速，兼容 AMD / Intel 核显与独显。
- **CPU**：纯 CPU 回退，速度较慢但一定能跑。

ONNX 模型默认放在 `models/e5-base-v2-onnx/`，包含 `model.onnx` 与 tokenizer 文件。

## 快速开始

1. 准备目录（大文件不入库，需自行准备）：

```text
RiMCP_v2/
├─ RimWorldData/          # RimWorld 游戏数据与反编译源码
├─ models/                # e5-base-v2-onnx 模型
├─ index/                 # 默认索引产物（构建生成）
├─ src/                   # 源代码
├─ app/                   # Launcher 发布产物（build.bat 生成）
└─ build.bat
```

2. 首次使用：双击根目录下的 **build.bat**，等待编译完成（约 10 秒）。

3. 日常启动：直接双击 `app\RiMCP.Launcher.exe` 即可秒开图形界面，无需任何黑框。

4. 如果你更新了代码，只需再次运行 **build.bat** 重新编译即可。

5. Launcher 启动后会自动检测环境：

   - 顶部环境条会检查 `RimWorldData/`、`models/`、索引目录与 build.json。
   - Launcher 会自动把 `app\` 的上一级目录识别为 `RIMCP_ROOT`，并在根目录生成 `appsettings.json` 保存界面偏好。
   - 检测通过后，点击 **全量重建** 生成第一份索引；之后的日常更新交给顶部“一键诊断”建议条。
   - 首次运行请确认 `models/` 与 `RimWorldData/` 已准备就绪，否则环境条会亮红灯并提示缺失项。

6. 在 AI 客户端中配置 MCP（见下一节），即可开始查询。

## 核心功能

- **傻瓜式诊断中枢（Launcher）**：自动比对 Def/符号/文本哈希和 C# 修改时间，直接告诉你该“无需构建 / 仅图谱 / 增量构建 / 全量重建”，并支持 ⚡ 一键执行，避免凭感觉点按钮。
- **分片增量构建**：按符号级内容哈希复用向量，只重算真正变化的条目。改几行代码通常只需几秒，不必再等 40 分钟全量。
- **多版本隔离（`RIMCP_INDEX_DIR`）**：同一套 `RimWorldData/` 与 `models/` 下可维护 1.6、1.7 等多份索引（如 `index_16/`、`index_17/`），构建产物、`embedding-cache.tsv` 与检查点互不污染。
- **双态加载模式（`RIMCP_LOAD_MODE`）**：`mmap` 按需映射，内存占用低；`full` 全量载入内存，查询延迟最低。低配电脑也能秒出结果。
- **全局知识图谱**：跨分片解析继承、引用与调用关系，构建全局节点表并计算 PageRank，支持跨 DLC 的关联查询。

## MCP 客户端配置

在 Trae / Claude Desktop 等客户端的 `mcp.json` 中加入：

```json
{
  "mcpServers": {
    "rimcp": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "E:\\KMODS\\RiMCP_v2_3\\src\\RiMCP.McpServer\\RiMCP.McpServer.csproj",
        "-c",
        "Release"
      ],
      "env": {
        "RIMCP_ROOT": "E:\\KMODS\\RiMCP_v2_3",
        "RIMCP_LOAD_MODE": "full",
        "RIMCP_INDEX_DIR": "index_17"
      }
    }
  }
}
```

说明：

- `RIMCP_ROOT`：RiMCP_v2 根目录（其下必须有 `RimWorldData/` 与 `models/`）。
- `RIMCP_LOAD_MODE`：`full` 全量内存加载（推荐，查询最快）；`mmap` 按需映射（低内存机器）。
- `RIMCP_INDEX_DIR`：索引目录名或绝对路径。改成 `index_16` 即切换到 1.6 索引，改成 `index_17` 即使用 1.7 索引；删除该变量则使用默认 `index/`。
- JSON 标准不支持 `//` 注释，因此多版本切换通过修改 `RIMCP_INDEX_DIR` 的值完成。
- 如果不想每次让 `dotnet run` 检查项目，可先构建，再把 `command` 指向 `src/RiMCP.McpServer/bin/Release/net8.0/RiMCP.McpServer.exe`。

也可以直接给 McpServer 传命令行参数（优先级高于环境变量）：

```powershell
dotnet run --project src\RiMCP.McpServer\RiMCP.McpServer.csproj -c Release -- --index-dir index_17
```

## 常用命令

```powershell
# 构建整个解决方案
dotnet build src\RiMCP.slnx -c Release

# 运行核心测试
dotnet run --project src\RiMCP.CoreTests -c Release

# 全量构建索引（默认写入 index/）
dotnet run --project src\RiMCP.Indexer -c Release -- --force-clean

# 指定多版本索引目录
dotnet run --project src\RiMCP.Indexer -c Release -- --index-dir index_17 --force-clean
```

## 目录结构

- `src/RiMCP.Core`：路径解析、存储格式、哈希、诊断逻辑（UpdateAdvisor）。
- `src/RiMCP.Indexer`：XML/C# 解析、分片增量构建、ONNX 向量、Lucene 与图谱。
- `src/RiMCP.McpServer`：MCP 工具与检索/图谱服务。
- `src/RiMCP.Launcher`：WPF 启动器、环境检测与一键诊断。

## License

本项目采用 [MIT License](LICENSE)。

## 致谢

- **RiMCP_hybrid**：本项目的架构思路与分片检索设计来源。
- **Microsoft Research**：`e5-base-v2` 文本嵌入模型。
- **Lucene.Net**：分片全文索引。
- **ONNX Runtime / DirectML**：跨 CPU / GPU 推理。
- **RimWorld 与 Ludeon Studios**：游戏数据与 Mod 生态。本项目与 Ludeon Studios 无隶属关系。

RimWorld 游戏数据、反编译源码与模型文件受各自版权约束，未包含在本仓库中，请自行合法获取。
