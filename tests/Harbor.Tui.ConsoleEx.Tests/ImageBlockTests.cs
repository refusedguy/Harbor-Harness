using System.Buffers.Binary;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

public class ImageBlockTests
{
    private static byte[] PngHeader(uint width, uint height)
    {
        var data = new byte[24];
        Signature(data);
        data[12] = (byte)'I';
        data[13] = (byte)'H';
        data[14] = (byte)'D';
        data[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), height);
        return data;

        static void Signature(byte[] d)
        {
            d[0] = 0x89; d[1] = 0x50; d[2] = 0x4E; d[3] = 0x47;
            d[4] = 0x0D; d[5] = 0x0A; d[6] = 0x1A; d[7] = 0x0A;
        }
    }

    [Test]
    public async Task Probe_Reads_IhdrDimensions()
    {
        bool ok = PngProbe.TryReadDimensions(PngHeader(1920, 1080), out var w, out var h);
        await Assert.That(ok).IsTrue();
        await Assert.That(w).IsEqualTo(1920);
        await Assert.That(h).IsEqualTo(1080);
    }

    [Test]
    public async Task Probe_Rejects_SignatureMismatch_AndShortData()
    {
        var bad = PngHeader(10, 10);
        bad[1] = 0x51; // ломаем сигнатуру
        await Assert.That(PngProbe.TryReadDimensions(bad, out _, out _)).IsFalse();
        await Assert.That(PngProbe.TryReadDimensions([1, 2, 3], out _, out _)).IsFalse();
    }

    [Test]
    public async Task Paint_Shows_Name_Dims_AndSize()
    {
        var block = new ImageBlock("shots/screenshot.png", "image/png", 2048, PngHeader(640, 480));
        var buffer = new ScreenBuffer(40, 2);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, 2), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("◉ screenshot.png");
        await Assert.That(art).Contains("640×480");
        await Assert.That(art).Contains("2 KB");
    }

    [Test]
    public async Task NonImage_DataWithoutPng_FallsBackToMimeLine()
    {
        var pdf = new ImageBlock("report.pdf", "application/pdf", 64 * 1024, null);
        await Assert.That(pdf.IsImage).IsFalse();
        await Assert.That(pdf.HasPngHeader).IsFalse();
        await Assert.That(pdf.SummaryLine()).IsEqualTo("application/pdf · 64 KB");

        var pngNoData = new ImageBlock("a.png", "image/png", 128, null);
        await Assert.That(pngNoData.Dimensions).IsNull();

        string kindKind = new ImageBlock("f.png", "image/png", 1, PngHeader(2, 2)).Kind;
        await Assert.That(kindKind).IsEqualTo("image");
    }
}

public class JpegProbeTests
{
    /// <summary>FFD8 + APP0-заглушка + SOF-сегмент с указанными размерами.</summary>
    private static byte[] Jpeg(ushort width, ushort height, byte sofMarker = 0xC0, bool padBeforeSof = false)
    {
        var data = new List<byte>(24) { 0xFF, 0xD8 };
        if (padBeforeSof)
        {
            data.AddRange([0xFF, 0xE0, 0x00, 0x04, 0x4A]); // APP0 с усечённым контентом
        }

        data.Add(0xFF);
        if (padBeforeSof)
        {
            data.Add(0xFF); // забивной байт перед кодом маркера
        }

        data.AddRange([sofMarker, 0x00, 0x11, 0x08]);
        data.Add((byte)(height >> 8));
        data.Add((byte)height);
        data.Add((byte)(width >> 8));
        data.Add((byte)width);
        data.AddRange([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xDA, 0xFF, 0xD9]);
        return [.. data];
    }

    [Test]
    public async Task Probe_ReadsBaselineAndProgressiveDimensions()
    {
        bool baseline = JpegProbe.TryReadDimensions(Jpeg(1920, 1080), out var w, out var h);
        await Assert.That(baseline).IsTrue();
        await Assert.That(w).IsEqualTo(1920);
        await Assert.That(h).IsEqualTo(1080);

        // SOF0 хранит height раньше width — проверяем перепутывание порядка.
        await Assert.That(JpegProbe.TryReadDimensions(Jpeg(480, 700, sofMarker: 0xC2), out w, out h)).IsTrue();
        await Assert.That(w).IsEqualTo(480);
        await Assert.That(h).IsEqualTo(700);
    }

    [Test]
    public async Task Probe_ToleratesFillBytes_AndSkipsSegments()
    {
        await Assert.That(JpegProbe.TryReadDimensions(Jpeg(640, 480, padBeforeSof: true), out var w, out _)).IsTrue();
        await Assert.That(w).IsEqualTo(640);
    }

    [Test]
    public async Task Probe_RejectsSignatureTruncationZeroDimsAndSosFirst()
    {
        await Assert.That(JpegProbe.TryReadDimensions([], out _, out _)).IsFalse();
        await Assert.That(JpegProbe.TryReadDimensions([0xFF, 0xD9], out _, out _)).IsFalse();

        byte[] truncated = Jpeg(10, 10)[..8]; // обрыв внутри SOF
        await Assert.That(JpegProbe.TryReadDimensions(truncated, out _, out _)).IsFalse();

        byte[] zeros = Jpeg(0, 42);
        await Assert.That(JpegProbe.TryReadDimensions(zeros, out _, out _)).IsFalse();

        byte[] noSof = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02]; // SOS без SOF → метаданных нет
        await Assert.That(JpegProbe.TryReadDimensions(noSof, out _, out _)).IsFalse();
    }
}
