using System.Threading.Channels;
using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Pipeline tests (design §5.1): stream bytes → TerminalInputSource reader
/// thread → parser → channel of typed events, including the ESC-flush and
/// paste-watchdog timer policies running on the reader thread.
/// </summary>
public class TerminalInputSourceTests
{
    [Test]
    public async Task Preloaded_Stream_Flows_Through_Parser_To_Channel()
    {
        using var source = new TerminalInputSource(
            new MemoryStream("\u001B[<64;3;3Ma\u001B[13;2u\u001B["u8.ToArray()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = source.RunAsync(cts.Token);

        var wheel = await ReadOne(source.Events, cts.Token);
        var letter = await ReadOne(source.Events, cts.Token);
        var shiftEnter = await ReadOne(source.Events, cts.Token);

        await Assert.That(wheel.Mouse.Type).IsEqualTo(MouseEventType.WheelUp);
        await Assert.That(letter.Key.Character).IsEqualTo(new Rune('a'));
        await Assert.That(shiftEnter.Key.Key).IsEqualTo(KeyCode.Enter);
        await Assert.That(shiftEnter.Key.Modifiers).IsEqualTo(KeyModifiers.Shift);

        // EOF completes the run task and closes the channel.
        await run;
        await source.Events.Completion;
    }

    [Test]
    public async Task EscFlush_Timer_Emits_Lone_Escape_At_Chunk_Boundary()
    {
        using var stream = new PulsedStream();
        using var source = new TerminalInputSource(
            stream,
            new TerminalInputSourceOptions
            {
                EscFlushTimeout = TimeSpan.FromMilliseconds(30),
                PasteAbortTimeout = TimeSpan.Zero,
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = source.RunAsync(cts.Token);

        stream.Push([(byte)0x1B]);

        var evt = await ReadOne(source.Events, cts.Token);
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Key);
        await Assert.That(evt.Key.Key).IsEqualTo(KeyCode.Escape);

        // Continuation afterwards still decodes as its own sequence.
        stream.Push("\u001B[A"u8.ToArray());
        var arrow = await ReadOne(source.Events, cts.Token);
        await Assert.That(arrow.Key.Key).IsEqualTo(KeyCode.Up);
    }

    [Test]
    public async Task Paste_Watchdog_Aborts_Unclosed_Paste()
    {
        using var stream = new PulsedStream();
        using var source = new TerminalInputSource(
            stream,
            new TerminalInputSourceOptions
            {
                EscFlushTimeout = TimeSpan.Zero,
                PasteAbortTimeout = TimeSpan.FromMilliseconds(40),
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = source.RunAsync(cts.Token);

        stream.Push("\u001B[200~stuck payload"u8.ToArray());

        var evt = await ReadOne(source.Events, cts.Token);
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Paste);
        await Assert.That(evt.Paste.Text).IsEqualTo("stuck payload");
        await Assert.That(evt.Paste.WasTruncated).IsTrue();
    }

    [Test]
    public async Task Resize_Polling_Emits_ResizeSignal_On_Size_Change()
    {
        // Deterministic probe: first two calls report the old viewport,
        // every later call reports the resized one (baseline capture is
        // asynchronous, so value sequences — not timing — define the test).
        var probes = 0;
        using var stream = new PulsedStream();
        using var source = new TerminalInputSource(
            stream,
            new TerminalInputSourceOptions
            {
                ResizePollInterval = TimeSpan.FromMilliseconds(20),
                SizeProvider = () => ++probes <= 2 ? (80, 24) : (100, 30),
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = source.RunAsync(cts.Token);

        var evt = await ReadOne(source.Events, cts.Token);
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Resize);
        await Assert.That(evt.Resize.Width).IsEqualTo(100);
        await Assert.That(evt.Resize.Height).IsEqualTo(30);
    }

    [Test]
    public async Task Dispose_Stops_The_Reader_Thread()
    {
        var stream = new PulsedStream();
        var source = new TerminalInputSource(
            stream,
            new TerminalInputSourceOptions { EscFlushTimeout = TimeSpan.Zero, PasteAbortTimeout = TimeSpan.Zero });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = source.RunAsync(cts.Token);
        stream.Push("idle"u8.ToArray());
        _ = await ReadOne(source.Events, cts.Token); // reader is alive

        source.Dispose();

        await run.WaitAsync(TimeSpan.FromSeconds(3));
        // Reader.Completion completes only after the channel drains: "idle"
        // queued four Char events and only the first one was consumed.
        var drained = 0;
        while (source.Events.TryRead(out _))
        {
            drained++;
        }

        await Assert.That(drained).IsEqualTo(3); // 'd', 'l', 'e'
        await source.Events.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Double_Start_Is_Rejected()
    {
        var source = new TerminalInputSource(
            new MemoryStream([1]));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = source.RunAsync(cts.Token);

            await Assert.That(() => source.RunAsync(cts.Token))
                .Throws<InvalidOperationException>();
        }
        finally
        {
            source.Dispose();
        }
    }

    private static async Task<InputEvent> ReadOne(System.Threading.Channels.ChannelReader<InputEvent> reader, CancellationToken ct)
    {
        await Assert.That(await reader.WaitToReadAsync(ct)).IsTrue();
        await Assert.That(reader.TryRead(out var evt)).IsTrue();
        return evt;
    }

    /// <summary>In-memory stream whose reads block until chunks are pushed —
    /// emulates a live terminal without a real tty.</summary>
    private sealed class PulsedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly object _gate = new();

        public void Push(byte[] chunk)
        {
            lock (_gate)
            {
                _chunks.Enqueue(chunk);
            }

            _available.Release();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[] chunk;
            lock (_gate)
            {
                chunk = _chunks.Dequeue();
            }

            var count = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, count).CopyTo(buffer.Span);
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
