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

        // Validate 应通过
        try { BuildJson.Validate(loaded, 768, 244801); }
        catch (Exception ex) { Assert(false, $"Validate 不应抛异常：{ex.Message}"); }
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