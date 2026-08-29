#nullable enable
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Harbor.Ipc.Protocol;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks the length-prefixed MessagePack framing layer used by
///     <see cref="WireCodec" />. Three scenarios are exercised over one
///     persistent <see cref="PipeStream" /> (per-iteration connection setup
///     excluded — the codec is measured, not the dial):
///     - <see cref="Roundtrip_Full" />: WriteResponseAsync + ReadResponseAsync
///       with MessagePack serialization, steady-state (pre-built response).
///     - <see cref="Roundtrip_FrameOnly" />: raw frame write + read without
///       MessagePack — the framing layer alone; steady-state target is 0 B.
///     - <see cref="ReadFrame_FromPipe" />: raw frame-header parsing from a
///       <see cref="PipeReader" />.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class IpcFramingBenchmark
{
    private Pipe _pipe = null!;
    private PipeStream _stream = null!;
    private PipeReader _reader = null!;
    private byte[] _preformedFrame = null!;
    private OkResponse _response = null!;

    [Params(64, 4096, 65536)]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false));
        _stream = new PipeStream(_pipe);
        _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));

        var payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
        _response = new OkResponse { RequestId = Guid.NewGuid(), Payload = payload };

        _preformedFrame = new byte[PayloadSize + 4];
        // Little-endian here matches the raw-header reader below (this
        // benchmark's internal convention predates the big-endian wire
        // format used by WireCodec itself).
        BinaryPrimitives.WriteInt32LittleEndian(_preformedFrame, PayloadSize);
        Random.Shared.NextBytes(_preformedFrame.AsSpan(4));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Complete();
        _stream.Dispose();
    }

    [Benchmark(Description = "WireCodec roundtrip (MessagePack, pooled + PipeReader)", Baseline = true)]
    public async ValueTask<HarborResponse?> Roundtrip_Full()
    {
        await WireCodec.WriteResponseAsync(_stream, _response).ConfigureAwait(false);
        return await WireCodec.ReadResponseAsync(_reader).ConfigureAwait(false);
    }

    [Benchmark(Description = "WireCodec frame-only roundtrip (no MessagePack, pooled + PipeReader)")]
    public async ValueTask<int> Roundtrip_FrameOnly()
    {
        await WireCodec.WriteFrameAsync(_stream, _preformedFrame.AsMemory(4)).ConfigureAwait(false);
        ReadOnlySequence<byte>? frame = await WireCodec.ReadFrameAsync(_reader).ConfigureAwait(false);
        int length = (int)frame!.Value.Length;
        _reader.AdvanceTo(frame.Value.End);
        return length;
    }

    [Benchmark(Description = "Read frame header from PipeReader")]
    public async ValueTask<ReadOnlySequence<byte>> ReadFrame_FromPipe()
    {
        await _pipe.Writer.WriteAsync(_preformedFrame).ConfigureAwait(false);
        var result = await _pipe.Reader.ReadAsync().ConfigureAwait(false);
        var buffer = result.Buffer;

        if (buffer.Length < 4)
        {
            _pipe.Reader.AdvanceTo(buffer.Start);
            return ReadOnlySequence<byte>.Empty;
        }

        var lengthSpan = buffer.Slice(0, 4);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthSpan.FirstSpan);

        // Consume only a complete frame; otherwise leave bytes buffered for the next read.
        if (buffer.Length < 4 + length)
        {
            _pipe.Reader.AdvanceTo(buffer.Start);
            return ReadOnlySequence<byte>.Empty;
        }

        var payload = buffer.Slice(4, length);
        _pipe.Reader.AdvanceTo(payload.End);
        return payload;
    }
}

/// <summary>
///     Minimal <see cref="Stream" /> implementation backed by a
///     <see cref="Pipe" />, used to benchmark the WireCodec Stream-based
///     roundtrip without allocating a real network socket.
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly Pipe _pipe;
    private bool _disposed;

    public PipeStream(Pipe pipe)
    {
        _pipe = pipe;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
        // No-op: flushing must not complete the pipe writer, otherwise the
        // reader side observes EOF mid-roundtrip.
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
#pragma warning disable CA2012
        var memory = new Memory<byte>(buffer, offset, count);
        return ReadAsync(memory, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore CA2012
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sequence = result.Buffer;
        int bytes = (int)Math.Min(buffer.Length, sequence.Length);
        var slice = sequence.Slice(0, bytes);
        slice.CopyTo(buffer.Span);
        _pipe.Reader.AdvanceTo(slice.End, slice.End);
        return bytes;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
#pragma warning disable CA2012
        var memory = new Memory<byte>(buffer, offset, count);
        WriteAsync(memory, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore CA2012
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _pipe.Writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _pipe.Writer.Complete();
            _pipe.Reader.Complete();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
