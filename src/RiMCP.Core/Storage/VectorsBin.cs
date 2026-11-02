using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace RiMCP.Core.Storage;

/// <summary>
/// vectors.bin 的读写与 mmap 随机访问。64 字节固定头部、8 字节对齐，
/// 数据区为定长记录（id + float16 向量），支持按 id 零拷贝 O(1) 读取。
/// 另提供 <see cref="OpenInMemory"/>：整文件读入 byte[]，避免按需换页。
/// </summary>
public sealed unsafe class VectorsBin : IDisposable
{
    private const long Magic = 0x5F56454354524F52; // "_VECTOR"
    private const int Version = 1;
    private const int HeaderSize = 64;
    private const int IdBytes = sizeof(long);

    private FileStream? _stream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _base;        // 视图映射基址（对应文件偏移 0）
    private byte[]? _buffer;    // full 模式：整文件读入后的缓冲区

    private bool _writable;
    private int _dim;
    private int _vecStride;
    private long _recordCount;
    private long _dataOffset;
    private long _seedHash;

    private VectorsBin() { }

    /// <summary>向量维度。</summary>
    public int Dim => _dim;

    /// <summary>每条记录的字节步长（id + dim*2）。</summary>
    public int VecStride => _vecStride;

    /// <summary>记录总数。</summary>
    public long RecordCount => _recordCount;

    /// <summary>模型/归一化指纹（build.json 中 model.hash 的低 8 字节）。</summary>
    public long SeedHash => _seedHash;

    private void ThrowIfNotMapped()
    {
        if (_base == null && _buffer == null)
            throw new InvalidOperationException("VectorsBin 未打开，请先调用 Open/Create。");
    }

    private void CheckId(int id)
    {
        if (id < 0 || (long)id >= _recordCount)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"id 超出范围 0..{_recordCount - 1}。");
    }

    /// <summary>
    /// 创建新的向量文件（含 64 字节头部）并映射为可写。文件会被覆盖。
    /// 建议先经 IdAllocator 排定 id 再按 id 序写入。
    /// </summary>
    public static VectorsBin Create(string path, int dim, long recordCount, long seedHash)
    {
        if (dim <= 0) throw new ArgumentOutOfRangeException(nameof(dim));
        if (recordCount < 0) throw new ArgumentOutOfRangeException(nameof(recordCount));

        var result = new VectorsBin
        {
            _writable = true,
            _dim = dim,
            _recordCount = recordCount,
            _seedHash = seedHash,
        };
        result._vecStride = IdBytes + dim * sizeof(short);
        result._dataOffset = HeaderSize;

        result._stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        WriteHeader(result._stream, dim, result._vecStride, recordCount, seedHash);
        long dataBytes = checked(result._recordCount * result._vecStride);
        result._stream.SetLength(HeaderSize + dataBytes);
        result._stream.Flush();
        result.MapView(readOnly: false);
        return result;
    }

    /// <summary>
    /// 以只读方式打开已有向量文件并 mmap，用于检索侧零拷贝随机访问。
    /// </summary>
    public static VectorsBin Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var header = new byte[HeaderSize];
            int n = stream.Read(header, 0, HeaderSize);
            if (n != HeaderSize)
                throw new InvalidDataException("vectors.bin 文件过短，无法读取头部。");

            var result = new VectorsBin();
            ReadHeader(header, result);
            if (result._vecStride != IdBytes + result._dim * sizeof(short))
                throw new InvalidDataException("vecStride 与 dim 不符，索引损坏。");
            result._stream = stream;
            result.MapView(readOnly: true);
            return result;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 以只读方式打开已有向量文件并把整个文件读入内存（full 加载模式），
    /// 避免查询时按需换页；<see cref="GetVector"/> 直接切片该缓冲区。
    /// </summary>
    public static VectorsBin OpenInMemory(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize)
            throw new InvalidDataException("vectors.bin 文件过短，无法读取头部。");

        var result = new VectorsBin();
        ReadHeader(bytes, result);
        if (result._vecStride != IdBytes + result._dim * sizeof(short))
            throw new InvalidDataException("vecStride 与 dim 不符，索引损坏。");
        long expected = result._dataOffset + result._recordCount * result._vecStride;
        if (bytes.LongLength != expected)
            throw new InvalidDataException(
                $"vectors.bin 长度 {bytes.LongLength} 与头部声明的 {expected} 不符，索引损坏。");
        result._buffer = bytes;
        return result;
    }

    /// <summary>
    /// 以读写方式打开已有向量文件并 mmap（检查点续跑时继续写入未完成的 ONNX 批次）。
    /// </summary>
    public static VectorsBin OpenWritable(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            var header = new byte[HeaderSize];
            int n = stream.Read(header, 0, HeaderSize);
            if (n != HeaderSize)
                throw new InvalidDataException("vectors.bin 文件过短，无法读取头部。");

            var result = new VectorsBin { _writable = true };
            ReadHeader(header, result);
            if (result._vecStride != IdBytes + result._dim * sizeof(short))
                throw new InvalidDataException("vecStride 与 dim 不符，索引损坏。");
            result._stream = stream;
            result.MapView(readOnly: false);
            return result;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void MapView(bool readOnly)
    {
        var access = readOnly ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite;
        _mmf = MemoryMappedFile.CreateFromFile(_stream!, mapName: null, capacity: 0, access,
            HandleInheritability.None, leaveOpen: true);
        _view = _mmf.CreateViewAccessor(0, 0, access);
        // 指针在下层 _view 存活期间有效；Dispose 时才释放视图与句柄。
        _base = (byte*)_view.SafeMemoryMappedViewHandle.DangerousGetHandle().ToPointer();
    }

    private static void WriteHeader(Stream s, int dim, int vecStride, long recordCount, long seedHash)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        h.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(h, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(h[12..], HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], dim);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], 1); // flags bit0: little-endian
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], vecStride);
        // 28: reserved32 留 0
        BinaryPrimitives.WriteInt64LittleEndian(h[32..], recordCount);
        BinaryPrimitives.WriteInt64LittleEndian(h[40..], (long)HeaderSize);
        // 48: reserved64 留 0
        BinaryPrimitives.WriteInt64LittleEndian(h[56..], seedHash);
        s.Write(h);
    }

    private static void ReadHeader(byte[] h, VectorsBin target)
    {
        long magic = BinaryPrimitives.ReadInt64LittleEndian(h);
        if (magic != Magic) throw new InvalidDataException("vectors.bin magic 不匹配。");
        int version = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8));
        if (version != Version)
            throw new InvalidDataException($"vectors.bin 版本 {version} 不受支持（期望 {Version}）。");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(12));
        if (headerSize != HeaderSize)
            throw new InvalidDataException("vectors.bin 头部大小不匹配。");
        target._dim = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(16));
        target._vecStride = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(24));
        target._recordCount = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(32));
        target._dataOffset = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(40));
        target._seedHash = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(56));
    }

    /// <summary>写入第 <paramref name="id"/> 条向量（同时写入其 id）。</summary>
    public void WriteVector(int id, ReadOnlySpan<Half> vector)
    {
        if (!_writable) throw new InvalidOperationException("只读模式不能写入。");
        ThrowIfNotMapped();
        CheckId(id);
        if (vector.Length < _dim)
            throw new ArgumentException($"向量长度 {vector.Length} 小于 dim={_dim}。", nameof(vector));

        byte* row = _base + _dataOffset + (long)id * _vecStride;
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(row, IdBytes), id);
        vector.Slice(0, _dim).CopyTo(new Span<Half>(row + IdBytes, _dim));
    }

    /// <summary>只写记录头的 id 字段，不写向量数据（复制阶段为未命中记录占位）。</summary>
    public void WriteRecordId(int id)
    {
        if (!_writable) throw new InvalidOperationException("只读模式不能写入。");
        ThrowIfNotMapped();
        CheckId(id);
        byte* row = _base + _dataOffset + (long)id * _vecStride;
        BinaryPrimitives.WriteInt64LittleEndian(new Span<byte>(row, IdBytes), id);
    }

    /// <summary>读取记录头中存储的 id（用于校验文件连续性）。</summary>
    public long GetRecordId(int id)
    {
        ThrowIfNotMapped();
        CheckId(id);
        long offset = _dataOffset + (long)id * _vecStride;
        if (_buffer is not null)
            return BinaryPrimitives.ReadInt64LittleEndian(
                new ReadOnlySpan<byte>(_buffer, checked((int)offset), IdBytes));
        byte* row = _base + offset;
        return BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(row, IdBytes));
    }

    /// <summary>
    /// 读取第 <paramref name="id"/> 条向量，返回指向映射内存（或 full 模式缓冲区）的只读 Span。
    /// 注意：返回的 Span 仅在实例未 <see cref="Close"/> 前有效。
    /// </summary>
    public ReadOnlySpan<Half> GetVector(int id)
    {
        ThrowIfNotMapped();
        CheckId(id);
        long offset = _dataOffset + (long)id * _vecStride;
        int length = _dim * sizeof(short);
        if (_buffer is not null)
            return MemoryMarshal.Cast<byte, Half>(
                new ReadOnlySpan<byte>(_buffer, checked((int)(offset + IdBytes)), length));
        byte* row = _base + offset;
        return new ReadOnlySpan<Half>(row + IdBytes, _dim);
    }

    /// <summary>关闭并释放底层映射与文件句柄（full 模式同时丢弃内存缓冲区）。</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        _base = null;
        _buffer = null;
        _view?.Dispose();
        _view = null;
        _mmf?.Dispose();
        _mmf = null;
        _stream?.Dispose();
        _stream = null;
        GC.SuppressFinalize(this);
    }
}