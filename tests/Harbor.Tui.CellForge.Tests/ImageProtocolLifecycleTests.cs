using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2222: kitty image identity / stacking / delete, the content-hash
/// transmission cache, Sixel dispatch, and the two wire probes. Every test
/// drives the real byte formatters or the real layer — no protocol is mocked
/// away, so a broken escape sequence fails here rather than in a terminal.
/// </summary>
public class ImageProtocolLifecycleTests
{
    private static readonly byte[] TinyPng = MakePng(2, 1, [0x39, 0xBA, 0xE6, 0x00, 0x00, 0x00]);
    private static readonly byte[] OtherPng = MakePng(2, 1, [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00]);

    private static InlineImageLayer KittyLayer =>
        new() { Kind = InlineImageKind.KittyApc, FrameBounds = new Rect(0, 0, 40, 20) };

    // ── kitty identity + delete wire format ────────────────────────────────

    [Test]
    public async Task Kitty_IdAndZ_AreStampedIntoTheEnvelope()
    {
        string seq = Encoding.ASCII.GetString(Graphics.KittyPngInline(TinyPng, 4, 2, 7, 3));
        await Assert.That(seq).Contains("a=T,C=1,c=4,r=2,i=7,z=3,");
        await Assert.That(seq).EndsWith("\u001B\\");
    }

    [Test]
    public async Task Kitty_DeleteById_IsAnAtomicApcCommand()
    {
        await Assert.That(Encoding.ASCII.GetString(Graphics.KittyDeleteImage(7)))
            .IsEqualTo("\u001B_Ga=d,d=I,i=7\u001B\\");
        await Assert.That(Graphics.KittyDeleteImage(0).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Layer_ReusesImageId_BySourceContentHash()
    {
        var layer = KittyLayer;
        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, out byte[]? first)).IsTrue();
        await Assert.That(layer.TryEncode("a.png", "image/png", (byte[])TinyPng.Clone(), 4, 2, out byte[]? again)).IsTrue();
        await Assert.That(layer.TryEncode("b.png", "image/png", OtherPng, 4, 2, out byte[]? other)).IsTrue();

        string a = Encoding.ASCII.GetString(first!);
        string b = Encoding.ASCII.GetString(again!);
        string c = Encoding.ASCII.GetString(other!);
        await Assert.That(a).Contains("i=1,");
        await Assert.That(b).Contains("i=1,");   // same bytes ⇒ same id
        await Assert.That(c).Contains("i=2,");   // different bytes ⇒ new id
    }

    [Test]
    public async Task Layer_ForwardsZ_ToTheKittyEnvelope()
    {
        var layer = KittyLayer;
        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, 5, out byte[]? payload)).IsTrue();
        await Assert.That(Encoding.ASCII.GetString(payload!)).Contains("z=5,");
    }

    // ── content-hash transmission cache ────────────────────────────────────

    [Test]
    public async Task Emit_DedupesByContentHash_EvenWhenTheArrayIsRecreated()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = KittyLayer;

        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, out byte[]? first)).IsTrue();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 4, 2), first!);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsGreaterThan(0);

        // A fresh byte array with identical bytes must NOT re-transmit. This is
        // the acceptance point: reference-identity dedupe alone fails here.
        backend.ResetForTests();
        await Assert.That(layer.TryEncode("a.png", "image/png", (byte[])TinyPng.Clone(), 4, 2, out byte[]? copy)).IsTrue();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 4, 2), copy!);
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsEqualTo(0);
    }

    // ── delete on leave ────────────────────────────────────────────────────

    [Test]
    public async Task Emit_DeletesById_WhenTheImageLeavesTheFrame()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = KittyLayer;

        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, out byte[]? payload)).IsTrue();
        writer.BeginFrame();
        layer.Place(new Rect(2, 0, 4, 2), payload!);
        layer.Emit(writer);
        writer.EndFrame();

        backend.ResetForTests();
        layer.BeginFrame();
        writer.BeginFrame();
        layer.Emit(writer);
        writer.EndFrame();

        string frame = Encoding.ASCII.GetString(backend.Writes[0]);
        await Assert.That(frame).Contains("a=d,d=I,i=1");
        await Assert.That(frame).DoesNotContain("a=T");
    }

    [Test]
    public async Task Emit_EmitsNoDelete_WhenTheImageIsStillVisible()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = KittyLayer;
        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, out byte[]? payload)).IsTrue();

        for (int frame = 0; frame < 2; frame++)
        {
            backend.ResetForTests();
            layer.BeginFrame();
            writer.BeginFrame();
            layer.Place(new Rect(2, 0, 4, 2), payload!);
            layer.Emit(writer);
            writer.EndFrame();
        }

        // No delete and no re-transmission: a steady frame with a live image
        // is completely silent on the wire.
        await Assert.That(backend.TotalBytes).IsEqualTo(0);
    }

    // ── z-order ────────────────────────────────────────────────────────────

    [Test]
    public async Task Emit_OrdersPlacementsByAscendingZ()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = KittyLayer;

        await Assert.That(layer.TryEncode("hi.png", "image/png", TinyPng, 4, 2, 5, out byte[]? high)).IsTrue();
        await Assert.That(layer.TryEncode("lo.png", "image/png", OtherPng, 4, 2, -1, out byte[]? low)).IsTrue();

        writer.BeginFrame();
        layer.Place(new Rect(0, 0, 4, 2), high!);
        layer.Place(new Rect(10, 0, 4, 2), low!);
        layer.Emit(writer);
        writer.EndFrame();

        string frame = Encoding.ASCII.GetString(backend.Writes[0]);
        int lowerZ = frame.IndexOf("i=2", StringComparison.Ordinal);   // z=-1
        int higherZ = frame.IndexOf("i=1", StringComparison.Ordinal);  // z=5
        await Assert.That(lowerZ).IsGreaterThanOrEqualTo(0);
        await Assert.That(higherZ).IsGreaterThanOrEqualTo(0);
        await Assert.That(lowerZ).IsLessThan(higherZ);
    }

    [Test]
    public async Task ApplySkip_MarksOnlyTheCellsUnderAPlacement()
    {
        var layer = KittyLayer;
        var buffer = new EngineCells.ScreenBuffer(10, 5);
        layer.Place(new Rect(2, 1, 3, 2), new byte[] { 1, 2, 3 });

        layer.ApplySkip(buffer);

        await Assert.That(buffer.GetDiffOption(2, 1)).IsEqualTo(EngineCells.CellDiffOption.Skip);
        await Assert.That(buffer.GetDiffOption(4, 2)).IsEqualTo(EngineCells.CellDiffOption.Skip);
        await Assert.That(buffer.GetDiffOption(1, 1)).IsEqualTo(EngineCells.CellDiffOption.None);
        await Assert.That(buffer.GetDiffOption(5, 2)).IsEqualTo(EngineCells.CellDiffOption.None);
    }

    // ── wire probes ────────────────────────────────────────────────────────

    [Test]
    public async Task GraphicsProbe_EvaluatesAnswers_AndKeepsTheFallback()
    {
        await Assert.That(GraphicsProbe.Evaluate("\u001B_Gi=31;OK\u001B\\", null))
            .IsEqualTo(InlineImageKind.KittyApc);
        await Assert.That(GraphicsProbe.Evaluate(null, "\u001B[>1;95;0c"))
            .IsEqualTo(InlineImageKind.Sixel);
        await Assert.That(GraphicsProbe.Evaluate(null, "\u001B[>0;95;0c"))
            .IsEqualTo(InlineImageKind.None);
        await Assert.That(GraphicsProbe.Evaluate(null, null, InlineImageKind.Osc1337))
            .IsEqualTo(InlineImageKind.Osc1337);
        await Assert.That(GraphicsProbe.IsKittyGraphicsAnswer("\u001B_Gi=31;ERROR\u001B\\")).IsFalse();
        await Assert.That(GraphicsProbe.IsSixelAnswer(null)).IsFalse();
    }

    [Test]
    public async Task GraphicsProbe_AsyncShortCircuits_OnAKittyAnswer()
    {
        var transport = new FakeGraphicsTransport("\u001B_Gi=31;OK\u001B\\");
        var kind = await GraphicsProbe.ProbeAsync(transport);
        await Assert.That(kind).IsEqualTo(InlineImageKind.KittyApc);
        await Assert.That(transport.Sent.Count).IsEqualTo(1);
        await Assert.That(transport.Sent[0]).IsEqualTo(TerminalQueries.KittyGraphicsProbe);
    }

    [Test]
    public async Task GraphicsProbe_AsyncFallsThroughToSixel()
    {
        var transport = new FakeGraphicsTransport(null, "\u001B[>1;95;0c");
        var kind = await GraphicsProbe.ProbeAsync(transport);
        await Assert.That(kind).IsEqualTo(InlineImageKind.Sixel);
        await Assert.That(transport.Sent.Count).IsEqualTo(2);
        await Assert.That(transport.Sent[1]).IsEqualTo(TerminalQueries.SixelProbe);
    }

    [Test]
    public async Task InlineImageProbe_DetectsSixel_WithoutStealingOsc1337Terminals()
    {
        Func<string, string?> env = key => key switch
        {
            "HARBOR_INLINE_IMAGE" => "sixel",
            _ => null,
        };
        await Assert.That(InlineImageProbe.Detect(env)).IsEqualTo(InlineImageKind.Sixel);

        Func<string, string?> termEnv = key => key == "TERM" ? "xterm-sixel" : null;
        await Assert.That(InlineImageProbe.Detect(termEnv)).IsEqualTo(InlineImageKind.Sixel);

        Func<string, string?> wezEnv = key => key == "WEZTERM_EXECUTABLE" ? "/usr/bin/wezterm" : null;
        await Assert.That(InlineImageProbe.Detect(wezEnv)).IsEqualTo(InlineImageKind.Osc1337);
    }

    // ── Sixel dispatch + fallback ──────────────────────────────────────────

    [Test]
    public async Task Sixel_IsWiredIntoTheEncoder_ThroughThePngDecoder()
    {
        await Assert.That(InlineImageEncoder.Supports(InlineImageKind.Sixel, "image/png")).IsTrue();
        await Assert.That(InlineImageEncoder.Supports(InlineImageKind.Sixel, "image/jpeg")).IsFalse();
        await Assert.That(InlineImageEncoder.Supports(InlineImageKind.None, "image/png")).IsFalse();

        byte[]? payload = InlineImageEncoder.Encode(InlineImageKind.Sixel, "a.png", TinyPng, 4, 2);
        await Assert.That(payload).IsNotNull();
        string seq = Encoding.ASCII.GetString(payload!);
        await Assert.That(seq).StartsWith("\u001BPq\"1;1;2;1");
        await Assert.That(seq).EndsWith("\u001B\\");

        var layer = new InlineImageLayer { Kind = InlineImageKind.Sixel };
        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, out byte[]? viaLayer)).IsTrue();
        await Assert.That(Encoding.ASCII.GetString(viaLayer!)).StartsWith("\u001BPq");
    }

    [Test]
    public async Task Fallback_WithoutAProtocol_EmitsNothingIncludingDeletes()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var layer = new InlineImageLayer(); // Kind.None

        await Assert.That(layer.Enabled).IsFalse();
        await Assert.That(layer.TryEncode("a.png", "image/png", TinyPng, 4, 2, out byte[]? payload)).IsFalse();
        await Assert.That(payload).IsNull();

        layer.Place(new Rect(2, 0, 4, 2), TinyPng);
        writer.BeginFrame();
        layer.Emit(writer);
        writer.EndFrame();
        await Assert.That(backend.TotalBytes).IsEqualTo(0);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private sealed class FakeGraphicsTransport(params string?[] answers) : IGraphicsProbeTransport
    {
        private int _index;

        public List<string> Sent { get; } = [];

        public Task SendAsync(string sequence, CancellationToken cancellationToken = default)
        {
            Sent.Add(sequence);
            return Task.CompletedTask;
        }

        public Task<string?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(_index < answers.Length ? answers[_index++] : null);
    }

    /// <summary>Minimal, valid 8-bit RGB PNG (filter 0) for the Sixel decoder.</summary>
    private static byte[] MakePng(int width, int height, byte[] rgb)
    {
        using var scanlines = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            scanlines.WriteByte(0); // filter: None
            scanlines.Write(rgb, y * width * 3, width * 3);
        }

        byte[] inflated;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(scanlines.ToArray());
            }

            inflated = compressed.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour RGB

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, inflated);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.Write(length);
        target.Write(type);
        target.Write(data);
        target.Write(new byte[4]); // CRC is not verified by the renderer's decoder
    }
}
