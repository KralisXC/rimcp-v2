# RimWorldData

本目录用于存放构建索引所需的 **RimWorld 游戏数据与反编译源码**。内容体积较大，且版权归 Ludeon Studios 所有，因此整个目录已被 `.gitignore` 排除，不会上传到 Git，请勿将游戏数据二次分发。

请将解包后的 RimWorld `Data` 目录与 `RimWorld_Decompiled` 源码放置于此目录下，最终结构如下：

```text
RimWorldData/
├─ Data/
│  ├─ Core/
│  │  ├─ Defs/
│  │  ├─ Assemblies/
│  │  └─ About/About.xml
│  ├─ Royalty/
│  ├─ Ideology/
│  └─ Anomaly/
└─ RimWorld_Decompiled/
   └─ Assembly-CSharp/
      ├─ RimWorld/
      ├─ Verse/
      └─ LudeonTK/
```

## 各部分的作用

| 目录 | 作用 |
| --- | --- |
| `Data/` | XML Def 来源。`Data/Core` 是核心分片，`Data/` 下其余 DLC 子目录会被识别为独立分片；若没有 `Data/Core`，则整个 `Data/` 作为核心分片（旧布局回退） |
| `Data/*/Assemblies/` | 分片程序集（`.dll`），用于符号与元数据 |
| `Data/*/About/About.xml` | 用于自动识别游戏版本（读取 `supportedVersions`） |
| `RimWorld_Decompiled/Assembly-CSharp/` | 反编译后的 C# 源码，默认只索引 `RimWorld`、`Verse`、`LudeonTK` 三个命名空间 |

`Data/` 缺失时构建会直接失败；`RimWorld_Decompiled/` 缺失时仍可构建 Def 索引，但符号、继承与调用关系等 C# 相关能力会缺失。

## 准备步骤

1. 复制游戏 Data 目录（以下为 Steam 默认安装路径示例）：

```powershell
Copy-Item "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Data" -Destination "RimWorldData\Data" -Recurse
```

2. 使用 ILSpy 或 `ilspycmd` 反编译 `Assembly-CSharp.dll`：

```powershell
ilspycmd -p -o RimWorldData\RimWorld_Decompiled\Assembly-CSharp `
  "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorld_Data\Managed\Assembly-CSharp.dll"
```

3. 启动 Launcher，顶部“RimWorldData”一项变为绿色即表示数据目录已就绪。

## 路径约定

- 默认路径：`<RiMCP_v2 根>/RimWorldData`
- Launcher 通过 `RIMCP_ROOT` 定位（发布版会自动使用 `app/` 的上一级目录）
- Indexer 可用 `--root <RimWorldData 目录>` 覆盖；`--csharp-subdir` 可修改反编译源码子目录
