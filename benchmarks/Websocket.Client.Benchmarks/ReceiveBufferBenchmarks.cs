using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.IO;

namespace Websocket.Client.Benchmarks;

#if !NET472
[MemoryDiagnoser]
[ShortRunJob]
[RankColumn]
public class ReceiveBufferBenchmarks
{
    private const int ChunkSize = 1024 * 4;
    private const int MaxRetainedReceiveBufferSize = 1024 * 64;
    private static readonly Encoding Encoding = Encoding.UTF8;
    private readonly RecyclableMemoryStreamManager _memoryStreamManager = new();
    private ArrayBufferWriter<byte> _arrayBufferWriter = null!;
    private BenchmarkPooledBufferWriter _pooledBufferWriter = null!;
    private byte[] _payload = null!;

    [Params(128, 4096, 32768, 131072)]
    public int MessageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = Encoding.GetBytes(new string('x', MessageSize));
        _arrayBufferWriter = new ArrayBufferWriter<byte>(ChunkSize * 4);
        _pooledBufferWriter = new BenchmarkPooledBufferWriter(ChunkSize * 4);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pooledBufferWriter.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Before: RecyclableMemoryStream")]
    public string Before_RecyclableMemoryStream()
    {
        using var stream = _memoryStreamManager.GetStream();
        WriteStreamChunks(stream);

        return Encoding.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    [Benchmark(Description = "PR #158: ArrayBufferWriter")]
    public string Pr158_ArrayBufferWriter()
    {
        try
        {
            WriteChunks(_arrayBufferWriter);
            return Encoding.GetString(_arrayBufferWriter.WrittenSpan);
        }
        finally
        {
            _arrayBufferWriter.Clear();
        }
    }

    [Benchmark(Description = "Current: ArrayPool buffer")]
    public string Current_PooledBufferWriter()
    {
        try
        {
            WriteChunks(_pooledBufferWriter);
            return Encoding.GetString(_pooledBufferWriter.WrittenSpan);
        }
        finally
        {
            _pooledBufferWriter.Clear(MaxRetainedReceiveBufferSize);
        }
    }

    private void WriteStreamChunks(Stream stream)
    {
        var offset = 0;

        while (offset < _payload.Length)
        {
            var count = Math.Min(ChunkSize, _payload.Length - offset);
            stream.Write(_payload.AsSpan(offset, count));
            offset += count;
        }
    }

    private void WriteChunks(IBufferWriter<byte> writer)
    {
        var offset = 0;

        while (offset < _payload.Length)
        {
            var count = Math.Min(ChunkSize, _payload.Length - offset);
            _payload.AsSpan(offset, count).CopyTo(writer.GetSpan(ChunkSize));
            writer.Advance(count);
            offset += count;
        }
    }
}
#endif
