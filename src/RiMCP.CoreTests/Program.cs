using System.Globalization;
using RiMCP.Core.Indexing;
using RiMCP.Core.Models;
using RiMCP.Core.Paths;
using RiMCP.Core.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;

int failures = 0;

void Assert(bool cond, string message)
{
    if (cond) return;
    failures++;
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  [FAIL] {message}");
    Console.ResetColor();
}

string WorkDir()
{
    string d = Path.Combine(Path.GetTempPath(), "rimcp_tests_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(d);
    return d;
}

Console.WriteLine("=== Test 1: SymbolEntry + TSV 往返 ===");
Test1_Tsv();
Console.WriteLine("=== Test 2: VectorsBin 往返 (mmap) ===");
Test2_Vectors();
Console.WriteLine("=== Test 3: BuildJson 往返 ===");
Test3_BuildJson();
Console.WriteLine("=== Test 4: IdAllocator 排序 ===");
Test4_IdAllocator();
Console.WriteLine("=== Test 5: SymbolIndex 反查 ===");
Test5_SymbolIndex();
Console.WriteLine("=== Test 6: 全局符号表同名 Def 覆盖 ===");
Test6_GlobalSymbolTableOverride();
Console.WriteLine("=== Test 7: 嵌入映射表 key 为 (分片, 本地序号) ===");
Test7_EmbeddingCacheSlotKey();
Console.WriteLine("=== Test 8: 检查点恢复校验 ===");
Test8_CheckpointResume();
Console.WriteLine("=== Test 9: UpdateAdvisor 四态诊断 ===");
Test9_UpdateAdvisor();
Console.WriteLine("=== Test 10: 索引目录解析（GetIndexDirectory） ===");
Test10_IndexDirectoryResolution();

Console.WriteLine();
if (failures == 0)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("ALL TESTS PASSED");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAILED: {failures} 处断言失败");
    Console.ResetColor();
}
return failures;

void Test1_Tsv()
{
    string dir = WorkDir();
    try
    {
        string path = Path.Combine(dir, "test.tsv");
        var entries = new List<SymbolEntry>
        {
            MakeEntry(0, SymbolKind.CsClass,   SourceType.CSharp, "Bool",    "System", "true\tcontains\\tab->Value", "Assembly-CSharp/System/Boolean.cs", 10, 20, false, null, null, "含缩进\t的preview"),
            MakeEntry(1, SymbolKind.CsMethod,  SourceType.CSharp, "Parse",   "System", "Parse(string s)\nnl",       "Assembly-CSharp/System/Text.cs",    30, 40, true,  null, "System.Bool", "换行\npreview"),
            MakeEntry(2, SymbolKind.XmlDef,    SourceType.Xml,    "ThingDef", "Building", "Building.Windows_00",       "Data/Core/Defs/Buildings.xml",      0, 0, false, "Windows_00", null, "中文Def预览"),
            MakeEntry(3, SymbolKind.CsField,   SourceType.CSharp, "Count",   "",        "int Count",                  "",                                5, 5, true,  null, null, ""),
            MakeEntry(4, SymbolKind.XmlDefField, SourceType.Xml,  "statOffset", "Building", "Building.statOffset",   "Data/Core/Defs/Buildings.xml",      0, 1, true,  null, null, "尾部字段"),
        };

        SymbolsTsv.Save(path, entries);
        var loaded = SymbolsTsv.Load(path);

        Assert(loaded.Count == entries.Count, $"条目数量不匹配：期望 {entries.Count}，实际 {loaded.Count}");

        for (int i = 0; i < entries.Count && i < loaded.Count; i++)
        {
            var a = entries[i];
            var b = loaded[i];
            int idx = i;
            Assert(a.Id == b.Id, $"T[{idx}] Id：{a.Id} vs {b.Id}");
            Assert(a.Kind == b.Kind, $"T[{idx}] Kind：{a.Kind} vs {b.Kind}");
            Assert(a.Source == b.Source, $"T[{idx}] Source：{a.Source} vs {b.Source}");
            Assert(a.ShortName == b.ShortName, $"T[{idx}] ShortName：<{a.ShortName}> vs <{b.ShortName}>");
            Assert(a.Namespace == b.Namespace, $"T[{idx}] Namespace：<{a.Namespace}> vs <{b.Namespace}>");
            Assert(a.Signature == b.Signature, $"T[{idx}] Signature：<{a.Signature}> vs <{b.Signature}>");
            Assert(a.FileRelPath == b.FileRelPath, $"T[{idx}] FileRelPath：<{a.FileRelPath}> vs <{b.FileRelPath}>");
            Assert(a.LineStart == b.LineStart, $"T[{idx}] LineStart：{a.LineStart} vs {b.LineStart}");
            Assert(a.LineEnd == b.LineEnd, $"T[{idx}] LineEnd：{a.LineEnd} vs {b.LineEnd}");
            Assert(a.IsLeaf == b.IsLeaf, $"T[{idx}] IsLeaf：{a.IsLeaf} vs {b.IsLeaf}");
            Assert(a.DefLabel == b.DefLabel, $"T[{idx}] DefLabel：<{a.DefLabel}> vs <{b.DefLabel}>");
            Assert(a.ParentRef == b.ParentRef, $"T[{idx}] ParentRef：<{a.ParentRef}> vs <{b.ParentRef}>");
            Assert(a.Preview == b.Preview, $"T[{idx}] Preview：<{a.Preview}> vs <{b.Preview}>");
        }

        Assert(File.ReadLines(path).First().StartsWith("id\t", StringComparison.Ordinal), "首行应为表头");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test2_Vectors()
{
    const int dim = 768;
    const int count = 5;
    string dir = WorkDir();
    string path = Path.Combine(dir, "test.bin");
    var rand = new Random(42);

    // 生成 Half 向量（由 float 转换），保留原值用于逐分量比对
    var expected = new List<Half[]>(count);
    for (int i = 0; i < count; i++)
    {
        var v = new Half[dim];
        for (int j = 0; j < dim; j++)
            v[j] = (Half)(float)(rand.NextDouble() * 2 - 1);
        expected.Add(v);
    }

    try
    {
        using (var w = VectorsBin.Create(path, dim, count, seedHash: 0x123456789ABCDEF0))
        {
            Assert(w.Dim == dim, $"Create.Dim：{w.Dim} vs {dim}");
            Assert(w.RecordCount == count, $"Create.RecordCount：{w.RecordCount} vs {count}");
            Assert(w.VecStride == 8 + dim * 2, $"Create.VecStride：{w.VecStride} vs {8 + dim * 2}");
            for (int i = 0; i < count; i++)
                w.WriteVector(i, expected[i]);
        }

        using var r = VectorsBin.Open(path); // 重新打开触发 mmap
        Assert(r.Dim == dim, $"Open.Dim：{r.Dim} vs {dim}");

        // 边界：第 0 条；常规中间；第 4 条（最后一条）
        int[] check = { 0, 2, count - 1 };
        foreach (int i in check)
        {
            var got = r.GetVector(i);
            Assert(got.Length == dim, $"GetVector({i}).Length：{got.Length} vs {dim}");
            var exp = expected[i];
            for (int j = 0; j < dim; j++)
                Assert(got[j] == exp[j], $"Vec[{i}][{j}]：{got[j]:R} vs {exp[j]:R}");
        }

        // 越界应抛异常
        bool threw = false;
        try { r.GetVector(count); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Assert(threw, "GetVector(count) 越界应抛 ArgumentOutOfRangeException");

        bool threwNeg = false;
        try { r.GetVector(-1); }
        catch (ArgumentOutOfRangeException) { threwNeg = true; }
        Assert(threwNeg, "GetVector(-1) 越界应抛 ArgumentOutOfRangeException");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test3_BuildJson()
{
    string dir = WorkDir();
    string path = Path.Combine(dir, "test.json");
    try
    {
        var info = new BuildJson.BuildInfo
        {
            Schema = BuildJson.Schema,
            FormatVersion = BuildJson.CurrentFormatVersion,
            Generator = new BuildJson.GeneratorInfo { Name = "RiMCP.Indexer", Version = "0.1.0" },
            Dim = 768,
            Model = new BuildJson.ModelInfo { Name = "e5-base-v2-onnx", File = "models/e5-base-v2-onnx/model.onnx", Hash = "a1b2c3d4e5f60718" },
            Counts = new BuildJson.CountsInfo { Symbols = 244801, Nodes = 244801, Edges = 815641 },
            Shards = new List<BuildJson.ShardPointer>
            {
                new() { Name = "core", Index = 0, BaseId = 0, SymbolCount = 200000, EdgeCount = 700000, ManifestPath = "index/core/manifest.json", ManifestHash = "m1" },
                new() { Name = "dlc_anomaly", Index = 1, BaseId = 200000, SymbolCount = 44801, EdgeCount = 115641, ManifestPath = "index/dlc_anomaly/manifest.json", ManifestHash = "m2" },
            },
            EnabledShards = new List<string> { "core", "dlc_anomaly" },
            Graph = new BuildJson.GlobalGraphInfo
            {
                Path = "index/graph",
                Hash = "g1",
                NodeCount = 244801,
                EdgeCount = 815641,
                BuiltAt = "2026-01-01T00:00:00Z",
                CrossShard = true,
            },
            Paths = new Dictionary<string, string>
            {
                ["rimeworldData"] = "RimWorldData",
                ["indexDir"] = "index",
                ["vectors"] = "index/vectors.bin",
                ["symbolsTsv"] = "index/symbols.tsv",
            },
            Granularity = "symbol-with-line",
            Lucene = new BuildJson.LuceneInfo { Ok = true, Version = "4.8.1" },
        };

        BuildJson.Save(path, info);
        var loaded = BuildJson.Load(path);

        Assert(loaded.Schema == info.Schema, $"Schema：{loaded.Schema} vs {info.Schema}");
        Assert(loaded.FormatVersion == info.FormatVersion, $"FormatVersion：{loaded.FormatVersion} vs {info.FormatVersion}");
        Assert(loaded.Generator?.Name == "RiMCP.Indexer", $"Generator.Name：{loaded.Generator?.Name}");
        Assert(loaded.Generator?.Version == "0.1.0", $"Generator.Version：{loaded.Generator?.Version}");
        Assert(loaded.Dim == 768, $"Dim：{loaded.Dim}");
        Assert(loaded.Model?.Name == "e5-base-v2-onnx", $"Model.Name：{loaded.Model?.Name}");
        Assert(loaded.Model?.File == "models/e5-base-v2-onnx/model.onnx", $"Model.File：{loaded.Model?.File}");
        Assert(loaded.Model?.Hash == "a1b2c3d4e5f60718", $"Model.Hash：{loaded.Model?.Hash}");
        Assert(loaded.Counts?.Symbols == 244801, $"Counts.Symbols：{loaded.Counts?.Symbols}");
        Assert(loaded.Counts?.Nodes == 244801, $"Counts.Nodes：{loaded.Counts?.Nodes}");
        Assert(loaded.Counts?.Edges == 815641, $"Counts.Edges：{loaded.Counts?.Edges}");
        Assert(loaded.Paths.TryGetValue("vectors", out var v) && v == "index/vectors.bin", $"Paths[vectors]：{v}");
        Assert(loaded.Granularity == "symbol-with-line", $"Granularity：{loaded.Granularity}");
        Assert(loaded.Lucene?.Ok == true, $"Lucene.Ok：{loaded.Lucene?.Ok}");
        Assert(loaded.Lucene?.Version == "4.8.1", $"Lucene.Version：{loaded.Lucene?.Version}");
        Assert(loaded.Shards?.Count == 2, $"Shards：{loaded.Shards?.Count} vs 2");
        Assert(loaded.EnabledShards?.Count == 2, $"EnabledShards：{loaded.EnabledShards?.Count} vs 2");
        Assert(loaded.Graph?.NodeCount == 244801, $"Graph.NodeCount：{loaded.Graph?.NodeCount}");
        Assert(loaded.Graph?.CrossShard == true, "Graph.CrossShard 应为 true");

        // Validate 应通过（阶段二：v3 要求分片指针存在）
        try { BuildJson.Validate(loaded, 768, 244801); }
        catch (Exception ex) { Assert(false, $"Validate 不应抛异常：{ex.Message}"); }

        // 未启用任何分片时只校验指针，不要求 manifest 文件存在（MCP 轻量诊断路径）。
        try { BuildJson.Validate(loaded, 768, 244801, Array.Empty<string>(), dir); }
        catch (Exception ex) { Assert(false, $"Validate(启用分片为空) 不应抛异常：{ex.Message}"); }

        // 启用未声明的分片名：必须明确报错。
        try
        {
            BuildJson.Validate(loaded, 768, 244801, new[] { "no_such_shard" }, dir);
            Assert(false, "启用未知分片应抛出 InvalidDataException");
        }
        catch (InvalidDataException) { }

        // 旧格式（formatVersion 2）：必须拒绝并提示全量重建。
        loaded.FormatVersion = 2;
        try
        {
            BuildJson.Validate(loaded, 768, 244801);
            Assert(false, "旧格式索引应被拒绝");
        }
        catch (InvalidDataException ex)
        {
            Assert(ex.Message.Contains("全量重建", StringComparison.Ordinal),
                $"旧格式提示应包含“全量重建”：{ex.Message}");
        }
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test4_IdAllocator()
{
    var entries = new List<SymbolEntry>
    {
        MakeEntry(-1, SymbolKind.CsMethod,  SourceType.CSharp, "Parse",   "Zeta",   "Zeta.Parse()",   "", 0, 0, true, null, null, ""),
        MakeEntry(-1, SymbolKind.CsClass,   SourceType.CSharp, "Alpha",   "Beta",   "Beta.Alpha",     "", 0, 0, false, null, null, ""),
        MakeEntry(-1, SymbolKind.XmlDef,    SourceType.Xml,    "Thing",   "A",      "A.Thing",        "", 0, 0, false, null, null, ""),
        MakeEntry(-1, SymbolKind.CsField,   SourceType.CSharp, "X",       "Alpha",  "Alpha.X",        "", 0, 0, true, null, null, ""),
        MakeEntry(-1, SymbolKind.CsMethod,  SourceType.CSharp, "Go",      "Alpha",  "Alpha.Go()",     "", 0, 0, true, null, null, ""),
        MakeEntry(-1, SymbolKind.CsClass,   SourceType.CSharp, "Alpha",   "Alpha",  "Alpha.Alpha",    "", 0, 0, false, null, null, ""),
        MakeEntry(-1, SymbolKind.XmlDefField, SourceType.Xml,  "stat",    "B",      "B.statOffset",   "", 0, 0, true, null, null, ""),
        MakeEntry(-1, SymbolKind.CsStruct,  SourceType.CSharp, "Vec",     "Math",   "Math.Vec",       "", 0, 0, false, null, null, ""),
        MakeEntry(-1, SymbolKind.CsEnum,    SourceType.CSharp, "Mode",    "Core",   "Core.Mode",      "", 0, 0, false, null, null, ""),
        MakeEntry(-1, SymbolKind.CsInterface, SourceType.CSharp, "IThing", "Sys",   "Sys.IThing",     "", 0, 0, false, null, null, ""),
    };

    var sorted = IdAllocator.Assign(entries);

    Assert(sorted.Count == 10, $"Assign 数量：{sorted.Count} vs 10");
    for (int i = 0; i < sorted.Count; i++)
        Assert(sorted[i].Id == i, $"Id[索引{i}] 应为 {i}，实际 {sorted[i].Id}");

    // 验证严格字典序：(Source, Kind, Namespace, Signature)
    for (int i = 1; i < sorted.Count; i++)
    {
        var a = sorted[i - 1];
        var b = sorted[i];
        int sc = a.Source.CompareTo(b.Source);
        if (sc == 0) sc = a.Kind.CompareTo(b.Kind);
        if (sc == 0) sc = string.CompareOrdinal(a.Namespace, b.Namespace);
        if (sc == 0) sc = string.CompareOrdinal(a.Signature, b.Signature);
        Assert(sc < 0, $"第 {i} 个应大于第 {i - 1} 个（字典序）：<{a.Source}/{a.Kind}/{a.Namespace}/{a.Signature}> vs <{b.Source}/{b.Kind}/{b.Namespace}/{b.Signature}>");
    }
}

void Test5_SymbolIndex()
{
    string dir = WorkDir();
    string path = Path.Combine(dir, "test.tsv");
    try
    {
        var entries = new List<SymbolEntry>
        {
            MakeEntry(-1, SymbolKind.CsClass, SourceType.CSharp, "Foo", "Ns1", "Ns1.Foo", "", 1, 2, false, null, null, "预览Foo"),
            MakeEntry(-1, SymbolKind.CsMethod, SourceType.CSharp, "Foo", "Ns1", "Ns1.Foo.Run()", "", 3, 4, true, null, "Ns1.Foo", "预览Run"),
            MakeEntry(-1, SymbolKind.CsField, SourceType.CSharp, "Count", "Ns1", "Ns1.Foo.Count", "", 5, 5, true, null, "Ns1.Foo", ""),
            MakeEntry(-1, SymbolKind.XmlDef, SourceType.Xml, "Thing", "Building", "Building.Wall", "Data/Core/Defs/X.xml", 0, 0, false, "Wall", null, "墙"),
            MakeEntry(-1, SymbolKind.XmlDefField, SourceType.Xml, "health", "Building", "Building.Wall.health", "Data/Core/Defs/X.xml", 0, 0, true, null, "Building.Wall", ""),
        };
        var assigned = IdAllocator.Assign(entries);
        SymbolsTsv.Save(path, assigned);

        var idx = SymbolIndex.LoadFromTsv(path);
        Assert(idx.Count == 5, $"Count：{idx.Count} vs 5");

        // GetById
        var e0 = idx.GetById(0);
        Assert(e0 != null && e0.Signature == "Ns1.Foo", $"GetById(0).Signature：{e0?.Signature}");
        var e2 = idx.GetById(2);
        Assert(e2 != null && e2.ShortName == "Count", $"GetById(2).ShortName：{e2?.ShortName}");
        Assert(idx.GetById(999) == null, "GetById(999) 应为 null");

        // GetBySignature
        var sig = idx.GetBySignature("Building.Wall");
        Assert(sig is { Count: 1 } && sig[0] == GetActualId(assigned, "Building.Wall"),
            $"GetBySignature(\"Building.Wall\")：{sig?.Count} 项，id={sig?[0]}");
        Assert(idx.GetBySignature("No.Such") == null, "GetBySignature(不存在) 应为 null");

        // GetByShortName：Foo 有 2 条
        var foos = idx.GetByShortName("Foo");
        Assert(foos is { Count: 2 }, $"GetByShortName(\"Foo\") 应为 2 项，实际 {foos?.Count}");
        Assert(idx.GetByShortName("missing") == null, "GetByShortName(不存在) 应为 null");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test6_GlobalSymbolTableOverride()
{
    var coreEntries = new List<SymbolEntry>
    {
        MakeEntry(-1, SymbolKind.XmlDef, SourceType.Xml, "ParentThing", "ThingDef", "ThingDef:ParentThing",
            "Data/Core/Things.xml", 1, 3, false, "parent thing core", null, "core parent"),
        MakeEntry(-1, SymbolKind.XmlDef, SourceType.Xml, "ChildThing", "ThingDef", "ThingDef:ChildThing",
            "Data/Core/Things.xml", 5, 7, false, "child thing", "ThingDef:ParentThing", "core child"),
    };
    var dlcEntries = new List<SymbolEntry>
    {
        MakeEntry(-1, SymbolKind.XmlDef, SourceType.Xml, "ParentThing", "ThingDef", "ThingDef:ParentThing",
            "Data/TestDlc/Defs/Things.xml", 1, 3, false, "parent thing dlc", null, "dlc parent"),
    };

    var table = GlobalSymbolTable.Build(new List<ShardSymbolSet>
    {
        new("core", 0, 0, coreEntries),
        new("dlc_testdlc", 1, 2, dlcEntries),
    });

    Assert(table.SymbolCount == 3, $"SymbolCount：{table.SymbolCount} vs 3");
    Assert(table.ResolveSlot("core", 0) == 0, $"core[0] 全局 id：{table.ResolveSlot("core", 0)} vs 0");
    Assert(table.ResolveSlot("core", 1) == 1, $"core[1] 全局 id：{table.ResolveSlot("core", 1)} vs 1");
    Assert(table.ResolveSlot("dlc_testdlc", 0) == 2, $"dlc[0] 全局 id：{table.ResolveSlot("dlc_testdlc", 0)} vs 2");

    // 同名 XML Def：签名/短名解析到高 shardIndex 的 DLC 覆盖条目。
    Assert(table.ResolveSignature("ThingDef:ParentThing") == 2,
        $"ResolveSignature(ParentThing) 应落到 DLC id=2，实际 {table.ResolveSignature("ThingDef:ParentThing")}");
    Assert(table.ResolveShortName("ParentThing") == 2,
        $"ResolveShortName(ParentThing) 应落到 DLC id=2，实际 {table.ResolveShortName("ParentThing")}");

    // core 条目未被删除：全局 id 0 仍映射回 core 槽位，core 内其他符号仍可解析。
    Assert(table.GlobalIdToSlot.TryGetValue(0, out var slot) && slot.Shard == "core" && slot.LocalSeq == 0,
        "core ParentThing 的全局 id 0 应仍映射回 (core, 0)");
    Assert(table.ResolveSignature("ThingDef:ChildThing") == 1,
        $"core ChildThing 应保持 id=1，实际 {table.ResolveSignature("ThingDef:ChildThing")}");

    // core 内部指向被覆盖 Def 的边：ChildThing 的 ParentRef 必须解析到 DLC 覆盖条目。
    string parentRef = coreEntries[1].ParentRef!;
    Assert(table.ResolveSignature(parentRef) == 2,
        $"core 内部 ParentRef({parentRef}) 应解析到 DLC id=2，实际 {table.ResolveSignature(parentRef)}");
}

void Test7_EmbeddingCacheSlotKey()
{
    string dir = WorkDir();
    string path = Path.Combine(dir, EmbeddingCache.FileName);
    const string hashOld = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string hashOld2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string hashNew = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    try
    {
        // 旧布局：分片 shardA 的 3/4 号槽位。
        EmbeddingCache.Save(path, new[]
        {
            new EmbeddingCache.Row(hashOld, "shardA", 3),
            new EmbeddingCache.Row(hashOld2, "shardA", 4),
        });

        var old = EmbeddingCache.Load(path);
        Assert(old.Exists, "旧映射表应存在");
        Assert(old.Rows.Count == 2, $"旧映射表行数：{old.Rows.Count} vs 2");
        Assert(old.TryGet(hashOld, out var shard, out var local) && shard == "shardA" && local == 3,
            $"TryGet(hashOld) 应命中 (shardA, 3)，实际 ({shard}, {local})");
        Assert(old.TryGetBySlot("shardA", 3, out var bySlot) && bySlot == hashOld,
            "TryGetBySlot(shardA, 3) 应还原 hashOld");

        // 分片布局变化：新表按新的 (分片, 本地序号) 定位；旧槽位与淘汰 hash 均失效。
        EmbeddingCache.Save(path, new[] { new EmbeddingCache.Row(hashNew, "shardB", 7) });

        var updated = EmbeddingCache.Load(path);
        Assert(updated.TryGetBySlot("shardB", 7, out var newBySlot) && newBySlot == hashNew,
            "新表 TryGetBySlot(shardB, 7) 应命中 hashNew");
        Assert(!updated.TryGetBySlot("shardA", 3, out _), "布局变化后旧槽位 (shardA, 3) 不应再命中");
        Assert(!updated.TryGet(hashOld, out _, out _), "被淘汰的旧 hash 不应再命中");

        // 文件结构固定为三列：contentHash \t shardName \t localId（不得用 globalId 作 key）。
        var line = File.ReadLines(path).First();
        var parts = line.Split('\t');
        Assert(parts.Length == 3 && parts[0] == hashNew && parts[1] == "shardB" && parts[2] == "7",
            $"映射表行结构异常：<{line}>");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test8_CheckpointResume()
{
    string dir = WorkDir();
    const int dim = 8;
    const int count = 4;
    string vectors = Path.Combine(dir, "vectors.bin");
    string cpA = BuildCheckpoint.PathFor(dir, "shardA");
    string cpB = BuildCheckpoint.PathFor(dir, "shardB");
    try
    {
        using (var w = VectorsBin.Create(vectors, dim, count, seedHash: 1))
            for (int i = 0; i < count; i++) w.WriteRecordId(i);

        Assert(BuildCheckpoint.ValidateVectors(vectors, count), "id 从 0 连续的 vectors.bin 应通过校验");
        Assert(!BuildCheckpoint.ValidateVectors(vectors, count - 1), "记录数不等于全量符号数时应失败");

        // 破坏第 2 条记录的 id（模拟复制阶段未完整写入）：应判定不连续。
        long offset = 64 + 2L * (8 + dim * 2);
        using (var fs = new FileStream(vectors, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(99L), 0, 8);
        }
        Assert(!BuildCheckpoint.ValidateVectors(vectors, count), "id 不连续时应失败");

        // 每分片独立检查点：互不串扰，恢复时各自续跑。
        BuildCheckpoint.AppendBatch(cpA, 1, new[] { "h1", "h2" });
        BuildCheckpoint.AppendBatch(cpA, 2, new[] { "h3" });
        BuildCheckpoint.AppendBatch(cpB, 1, new[] { "x1" });

        var a = BuildCheckpoint.Load(cpA);
        Assert(a.LastBatchNo == 2, $"shardA LastBatchNo：{a.LastBatchNo} vs 2");
        Assert(a.CompletedHashes.SetEquals(new[] { "h1", "h2", "h3" }), "shardA 已完成 hash 集合不符");

        var b = BuildCheckpoint.Load(cpB);
        Assert(b.LastBatchNo == 1, $"shardB LastBatchNo：{b.LastBatchNo} vs 1");
        Assert(b.CompletedHashes.SetEquals(new[] { "x1" }), "shardB 不应看到 shardA 的 hash");

        BuildCheckpoint.Delete(cpA);
        BuildCheckpoint.Delete(cpB);
        Assert(!File.Exists(cpA), "删除后检查点文件应不存在");
        Assert(!Directory.Exists(Path.Combine(dir, BuildCheckpoint.DirectoryName)), "检查点目录为空时应被清理");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test9_UpdateAdvisor()
{
    string dir = WorkDir();
    string root = Path.Combine(dir, "RimWorldData");
    string coreXmlDir = Path.Combine(root, "Data", "Core");
    string xmlPath = Path.Combine(coreXmlDir, "Defs.xml");
    string indexDir = Path.Combine(dir, "index");

    const string model = "e5-base-v2-onnx";
    const int dim = 768;
    const string precision = "fp16";
    const int prep = 1;

    string xmlOriginal = """
        <?xml version="1.0" encoding="utf-8"?>
        <Defs>
          <ThingDef a="1" b="2">
            <defName>Foo</defName>
            <label>Foo Label</label>
            <marketValue>10</marketValue>
            <comps><li Class="CompX" /></comps>
          </ThingDef>
          <RecipeDef>
            <defName>Bar</defName>
            <label>Bar Label</label>
            <workAmount>100</workAmount>
          </RecipeDef>
        </Defs>
        """;

    void WriteXml(string content)
    {
        Directory.CreateDirectory(coreXmlDir);
        File.WriteAllText(xmlPath, content);
    }

    (IReadOnlyList<ShardSource>, IReadOnlyList<ShardCatalog.ShardEnvironment>) BuildCurrent()
    {
        var sources = ShardCatalog.Discover(root, ShardCatalog.DefaultCSharpSubdir);
        var envs = ShardCatalog.BuildEnvironments(
            sources, root, "1.5", "cpu", precision, model, dim, "test");
        return (sources, envs);
    }

    void WriteIndex(IReadOnlyList<ShardSource> sources, IReadOnlyList<ShardCatalog.ShardEnvironment> envs)
    {
        Directory.CreateDirectory(Path.Combine(indexDir, "core"));
        Directory.CreateDirectory(Path.Combine(indexDir, "graph"));
        foreach (var name in new[]
                 {
                     "graph.csr.bin", "graph.csc.bin", "graph.nodes.tsv",
                     "graph.pagerank.tsv", "graph.meta.json",
                 })
        {
            File.WriteAllBytes(Path.Combine(indexDir, "graph", name), Array.Empty<byte>());
        }

        var source = sources.First(s => s.Name == "core");

        // 与 Indexer 一致：按 (Kind, Namespace, Signature) 排序后分配 id 0..N-1。
        var contentEntries = UpdateAdvisor
            .ComputeXmlContentEntries(source, root, model, dim, precision, prep)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Namespace, StringComparer.Ordinal)
            .ThenBy(e => e.Signature, StringComparer.Ordinal)
            .ToList();

        var entries = contentEntries.Select((e, i) => new SymbolEntry
        {
            Id = i,
            Kind = e.Kind,
            Source = SourceType.Xml,
            ShortName = e.ShortName,
            Namespace = e.Namespace,
            Signature = e.Signature,
            FileRelPath = e.FileRelPath,
            LineStart = e.LineStart,
            LineEnd = e.LineStart,
            IsLeaf = e.Kind == SymbolKind.XmlDefField,
            DefLabel = e.ShortName,
            Preview = e.Signature,
        }).ToList();
        SymbolsTsv.Save(Path.Combine(indexDir, "core", "symbols.tsv"), entries);
        EmbeddingCache.Save(
            Path.Combine(indexDir, EmbeddingCache.FileName),
            contentEntries.Select((e, i) => new EmbeddingCache.Row(e.ContentHash, "core", i)).ToArray());

        var env = envs.First(e => e.ShardName == "core");
        string builtAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var manifest = new ShardManifest
        {
            Schema = ShardManifest.SchemaId,
            FormatVersion = ShardManifest.CurrentFormatVersion,
            Shard = "core",
            ShardIndex = env.ShardIndex,
            BaseId = 0,
            SymbolCount = entries.Count,
            Truncated = false,
            GameVersion = env.GameVersion,
            SourceHash = env.SourceHash,
            BuilderCommit = "test",
            OnnxProvider = env.OnnxProvider,
            Precision = precision,
            EmbeddingModel = model,
            Dim = dim,
            PreprocessingRevision = prep,
            BuiltAt = builtAt,
        };
        foreach (var name in ShardManifest.LayerNames)
        {
            manifest.Layers[name] = new LayerInfo
            {
                Hash = env.LayerHashes[name],
                BuiltAt = builtAt,
            };
        }
        string manifestPath = Path.Combine(indexDir, "core", "manifest.json");
        manifest.Save(manifestPath);

        var build = new BuildJson.BuildInfo
        {
            Schema = BuildJson.Schema,
            FormatVersion = BuildJson.CurrentFormatVersion,
            Dim = dim,
            Model = new BuildJson.ModelInfo { Name = model, File = "models/model.onnx", Hash = "test" },
            Counts = new BuildJson.CountsInfo { Symbols = entries.Count },
            GameVersion = "1.5",
            BuilderCommit = "test",
            Graph = new BuildJson.GlobalGraphInfo
            {
                Path = "index/graph",
                Hash = env.LayerHashes[ShardCatalog.LayerHashes.Graph],
                BuiltAt = builtAt,
            },
        };
        build.Shards.Add(new BuildJson.ShardPointer
        {
            Name = "core",
            Index = 0,
            BaseId = 0,
            SymbolCount = entries.Count,
            ManifestPath = "index/core/manifest.json",
            ManifestHash = ShardManifest.ComputeFileHash(manifestPath),
        });
        build.EnabledShards.Add("core");
        BuildJson.Save(Path.Combine(indexDir, "build.json"), build);
    }

    UpdateAdvice DiagnoseCurrent()
    {
        var (sources, envs) = BuildCurrent();
        var build = BuildJson.Load(Path.Combine(indexDir, "build.json"));
        return UpdateAdvisor.Diagnose(dir, root, indexDir, sources, envs, build);
    }

    try
    {
        // ---- ① 无变化 → None ----
        WriteXml(xmlOriginal);
        var (sources0, envs0) = BuildCurrent();
        WriteIndex(sources0, envs0);

        var advice = DiagnoseCurrent();
        Assert(advice.Overall == ChangeKind.None, $"无变化应判 None，实际 {advice.Overall}");
        Assert(advice.RecommendedAction == "无需构建", $"无变化建议应为“无需构建”，实际 {advice.RecommendedAction}");
        Assert(advice.Summary.Contains("无需构建", StringComparison.Ordinal), $"Summary 缺少“无需构建”：{advice.Summary}");
        var core0 = advice.Shards.Single(d => d.Shard == "core");
        Assert(core0.Kind == ChangeKind.None, $"core 分片应判 None，实际 {core0.Kind}");

        // ---- ② 改一个 Def 数值 → ContentChanged ----
        WriteXml(xmlOriginal.Replace("<marketValue>10</marketValue>", "<marketValue>12</marketValue>"));
        advice = DiagnoseCurrent();
        Assert(advice.Overall == ChangeKind.ContentChanged, $"改 Def 数值应判 ContentChanged，实际 {advice.Overall}");
        Assert(advice.RecommendedAction == "增量构建", $"改值建议应为“增量构建”，实际 {advice.RecommendedAction}");
        Assert(advice.Summary.Contains("建议：增量构建", StringComparison.Ordinal), $"Summary 文案不符：{advice.Summary}");
        var core1 = advice.Shards.Single(d => d.Shard == "core");
        Assert(core1.Kind == ChangeKind.ContentChanged, $"core 分片应判 ContentChanged，实际 {core1.Kind}");
        Assert(core1.ChangedLayers.Count > 0, "ContentChanged 应列出变化层");

        // ---- ③ 仅交换 XML 属性顺序（不增删行，避免行号漂移）→ TextOnly ----
        WriteXml("""
            <?xml version="1.0" encoding="utf-8"?>
            <Defs>
              <ThingDef b="2" a="1">
                <defName>Foo</defName>
                <label>Foo Label</label>
                <marketValue>10</marketValue>
                <comps><li Class="CompX" /></comps>
              </ThingDef>
              <RecipeDef>
                <defName>Bar</defName>
                <label>Bar Label</label>
                <workAmount>100</workAmount>
              </RecipeDef>
            </Defs>
            """);
        advice = DiagnoseCurrent();
        Assert(advice.Overall == ChangeKind.TextOnly, $"仅属性换序应判 TextOnly，实际 {advice.Overall}");
        Assert(advice.RecommendedAction == "仅图谱", $"仅换序建议应为“仅图谱”，实际 {advice.RecommendedAction}");
        Assert(advice.Summary.Contains("建议：仅图谱", StringComparison.Ordinal), $"Summary 文案不符：{advice.Summary}");
        var core2 = advice.Shards.Single(d => d.Shard == "core");
        Assert(core2.Kind == ChangeKind.TextOnly, $"core 分片应判 TextOnly，实际 {core2.Kind}");

        // ---- ④ 删除 embedding-cache.tsv → Incompatible ----
        File.Delete(Path.Combine(indexDir, EmbeddingCache.FileName));
        advice = DiagnoseCurrent();
        Assert(advice.Overall == ChangeKind.Incompatible, $"删 cache 应判 Incompatible，实际 {advice.Overall}");
        Assert(advice.RecommendedAction == "全量重建", $"删 cache 建议应为“全量重建”，实际 {advice.RecommendedAction}");
        Assert(advice.Summary.Contains("必须全量重建", StringComparison.Ordinal), $"Summary 文案不符：{advice.Summary}");
        var core3 = advice.Shards.Single(d => d.Shard == "core");
        Assert(core3.Kind == ChangeKind.Incompatible, $"core 分片应判 Incompatible，实际 {core3.Kind}");
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
}

void Test10_IndexDirectoryResolution()
{
    string root = Path.Combine(Path.GetTempPath(), "rimcp_root_" + Guid.NewGuid().ToString("N"));
    string? originalEnv = Environment.GetEnvironmentVariable(PathUtil.IndexDirEnvVar);
    bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    try
    {
        // ① 无 CLI / 无环境变量 → 默认 <root>/index
        Environment.SetEnvironmentVariable(PathUtil.IndexDirEnvVar, null);
        Assert(Eq(PathUtil.GetIndexDirectory(root), Path.GetFullPath(Path.Combine(root, "index"))),
            $"默认应解析为 <root>/index，实际 {PathUtil.GetIndexDirectory(root)}");

        // ② CLI 目录名优先
        Assert(Eq(PathUtil.GetIndexDirectory(root, "index_17"), Path.GetFullPath(Path.Combine(root, "index_17"))),
            $"CLI index_17 解析错误：{PathUtil.GetIndexDirectory(root, "index_17")}");

        // ③ 环境变量 RIMCP_INDEX_DIR 生效，且 CLI 仍优先
        Environment.SetEnvironmentVariable(PathUtil.IndexDirEnvVar, "index_16");
        Assert(Eq(PathUtil.GetIndexDirectory(root), Path.GetFullPath(Path.Combine(root, "index_16"))),
            $"环境变量应解析 index_16，实际 {PathUtil.GetIndexDirectory(root)}");
        Assert(Eq(PathUtil.GetIndexDirectory(root, "index_17"), Path.GetFullPath(Path.Combine(root, "index_17"))),
            "CLI 目录名应优先于环境变量");

        // ④ 绝对路径直接使用（CLI 与环境变量各一次）
        string abs = Path.GetFullPath(Path.Combine(root, "abs_index"));
        Assert(Eq(PathUtil.GetIndexDirectory(root, abs), abs), $"CLI 绝对路径应原样返回，实际 {PathUtil.GetIndexDirectory(root, abs)}");
        Environment.SetEnvironmentVariable(PathUtil.IndexDirEnvVar, abs);
        Assert(Eq(PathUtil.GetIndexDirectory(root), abs), $"环境变量绝对路径应原样返回，实际 {PathUtil.GetIndexDirectory(root)}");

        Assert(PathUtil.DefaultIndexDirName == "index", "默认索引目录名应为 index");
    }
    finally
    {
        Environment.SetEnvironmentVariable(PathUtil.IndexDirEnvVar, originalEnv);
    }
}

// 依 Signature 查实际分配到的 id（供断言对照）
int GetActualId(List<SymbolEntry> entries, string signature)
    => entries.First(e => e.Signature == signature).Id;

// 构造条目的便捷方法；id 传 -1 表示待 Assign 分配
SymbolEntry MakeEntry(int id, SymbolKind kind, SourceType src, string shortName, string ns,
    string sig, string file, int ls, int le, bool leaf, string? defLabel, string? parentRef, string preview)
    => new()
    {
        Id = id,
        Kind = kind,
        Source = src,
        ShortName = shortName,
        Namespace = ns,
        Signature = sig,
        FileRelPath = file,
        LineStart = ls,
        LineEnd = le,
        IsLeaf = leaf,
        DefLabel = defLabel,
        ParentRef = parentRef,
        Preview = preview,
    };