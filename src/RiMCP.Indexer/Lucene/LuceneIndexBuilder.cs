// Portions of this file are based on RiMCP_hybrid
// Original: https://github.com/h7lu/RiMCP_hybrid
// Licensed under MIT License
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using RiMCP.Core.Models;

namespace RiMCP.Indexer.Lucene;

/// <summary>
/// P5：基于已定 id 的 SymbolEntry 生成 Lucene 索引目录。
/// 全量重建（OpenMode.CREATE），id 由 IdAllocator 保证唯一，无需按 id UpdateDocument。
/// </summary>
public sealed class LuceneIndexBuilder : IDisposable
{
    /// <summary>主键 int32，不分析、保留。</summary>
    public const string FieldId = "id";
    /// <summary>完整签名，分析、保留。</summary>
    public const string FieldSignature = "signature";
    /// <summary>短名，分析、保留。</summary>
    public const string FieldShortName = "shortName";
    /// <summary>符号类型（CsClass/Method/…），不分析、精确过滤。</summary>
    public const string FieldKind = "kind";
    /// <summary>来源（CSharp/Xml），不分析。</summary>
    public const string FieldSource = "source";
    /// <summary>相对路径，不分析、保留（可回拼绝对路径）。</summary>
    public const string FieldFileRel = "fileRel";
    /// <summary>预览，分析、保留（全文搜索主体）。</summary>
    public const string FieldPreview = "preview";

    private readonly IndexWriter _writer;

    /// <summary>在 indexDir 下创建/重建 Lucene 索引目录。</summary>
    public LuceneIndexBuilder(string indexPath)
    {
        System.IO.Directory.CreateDirectory(indexPath);
        var directory = FSDirectory.Open(indexPath);
        var analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48);
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)
        {
            // 全量重建：清空已有索引再写
            OpenMode = OpenMode.CREATE,
        };
        _writer = new IndexWriter(directory, config);
    }

    /// <summary>为单条符号建立 Document 并写入。</summary>
    public void Add(SymbolEntry e) => Add(e, e.Id);

    /// <summary>用指定的全局 id 写入文档（分片构建时 id = baseId + 本地 id）。</summary>
    public void Add(SymbolEntry e, int globalId)
    {
        var doc = new Document
        {
            new Int32Field(FieldId, globalId, Field.Store.YES),
            new TextField(FieldSignature, e.Signature, Field.Store.YES),
            new TextField(FieldShortName, e.ShortName, Field.Store.YES),
            new StringField(FieldKind, e.Kind.ToString(), Field.Store.YES),
            new StringField(FieldSource, e.Source.ToString(), Field.Store.YES),
            new StringField(FieldFileRel, e.FileRelPath ?? string.Empty, Field.Store.YES),
            new TextField(FieldPreview, e.Preview, Field.Store.YES),
        };
        _writer.AddDocument(doc);
    }

    /// <summary>提交索引。用 flush + commit 保证落盘。</summary>
    public void Commit()
    {
        _writer.Flush(false, false);
        _writer.Commit();
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}