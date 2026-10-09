using System.Buffers.Binary;
using System.IO.Compression;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// PNG bake for issue #400 slice 1/2: each primitive lands on the real
/// pixels, dimensions survive the round trip, and every failure (garbage,
/// non-PNG, truncated, unsupported layout) is a display string — never an
/// exception.
/// </summary>
public class MarkupPngBakerTests
{
    private static MarkupAnnotationModel RectModel() =>
        MarkupAnnotationModel.Empty.Add(
            MarkupKind.Rectangle, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.3, 0.3));

    private static (byte R, byte G, byte B, byte A) At(MarkupPngBaker.RgbaImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }

    [Test]
    public async Task Bake_Rect_DrawsRedBorder_AndKeepsDimensions()
    {
        byte[] source = TestPng(40, 20, 255, 255, 255);

        bool ok = MarkupPngBaker.TryBake(source, RectModel(), out byte[]? png, out string error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(png is not null).IsTrue();
        await Assert.That(PngProbe.TryReadDimensions(png!, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(40);
        await Assert.That(h).IsEqualTo(20);
    }

    [Test]
    public async Task Draw_Rect_BorderIsRed_InteriorUntouched()
    {
        MarkupPngBaker.TryDecode(TestPng(40, 20, 255, 255, 255), out var image, out _);
        MarkupPngBaker.Draw(image!, RectModel());

        // (0.1,0.1) → (4,2) on 40×20: weight-2 stroke stamps a 3px border there.
        await Assert.That(At(image!, 4, 2)).IsEqualTo(((byte)255, (byte)0, (byte)0, (byte)255));
        await Assert.That(At(image!, 8, 4)).IsEqualTo(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Test]
    public async Task Draw_Arrow_HeadLands_OnTipPixel()
    {
        MarkupPngBaker.TryDecode(TestPng(40, 20, 255, 255, 255), out var image, out _);
        var model = MarkupAnnotationModel.Empty.Add(
            MarkupKind.Arrow, NormalizedPoint.Create(0.1, 0.5), NormalizedPoint.Create(0.5, 0.5));
        MarkupPngBaker.Draw(image!, model);

        // Tip (0.5,0.5) → (20,10).
        await Assert.That(At(image!, 20, 10)).IsEqualTo(((byte)255, (byte)0, (byte)0, (byte)255));
    }

    [Test]
    public async Task Draw_Text_LabelBoxIsRed_GlyphIsWhite()
    {
        MarkupPngBaker.TryDecode(TestPng(40, 20, 255, 255, 255), out var image, out _);
        var model = MarkupAnnotationModel.Empty.Add(
            MarkupKind.Text, NormalizedPoint.Create(0.1, 0.1), NormalizedPoint.Create(0.1, 0.1), text: "HI");
        MarkupPngBaker.Draw(image!, model);

        // Anchor (0.1,0.1) → (4,2): label box starts red…
        await Assert.That(At(image!, 4, 2)).IsEqualTo(((byte)255, (byte)0, (byte)0, (byte)255));
        // …and the H stem (row ".#.", scale 2) lands white at (8,4).
        await Assert.That(At(image!, 8, 4)).IsEqualTo(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Test]
    public async Task Bake_EmptyModel_StillProducesValidPng()
    {
        bool ok = MarkupPngBaker.TryBake(TestPng(16, 8, 10, 20, 30), MarkupAnnotationModel.Empty, out byte[]? png, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(PngProbe.TryReadDimensions(png!, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(16);
        await Assert.That(h).IsEqualTo(8);
    }

    [Test]
    public async Task Bake_Rejects_NonPng_Truncated_And_Unsupported()
    {
        await Assert.That(MarkupPngBaker.TryBake(null, RectModel(), out _, out string e1)).IsFalse();
        await Assert.That(e1).IsNotEmpty();

        await Assert.That(MarkupPngBaker.TryBake([1, 2, 3, 4], RectModel(), out _, out string e2)).IsFalse();
        await Assert.That(e2).IsNotEmpty();

        byte[] jpegMagic = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        await Assert.That(MarkupPngBaker.TryBake(jpegMagic, RectModel(), out _, out string e3)).IsFalse();
        await Assert.That(e3).Contains("PNG");

        byte[] full = TestPng(16, 8, 10, 20, 30);
        byte[] cut = full[..40];
        await Assert.That(MarkupPngBaker.TryBake(cut, RectModel(), out _, out string e4)).IsFalse();
        await Assert.That(e4).IsNotEmpty();

        byte[] gray = TestPng(16, 8, 10, 20, 30, colorType: 0);
        await Assert.That(MarkupPngBaker.TryBake(gray, RectModel(), out _, out string e5)).IsFalse();
        await Assert.That(e5).Contains("8-bit");
    }

    [Test]
    public async Task Encode_Decode_RoundTrip_PreservesDimensions()
    {
        MarkupPngBaker.TryDecode(TestPng(24, 12, 7, 8, 9), out var image, out _);
        byte[] encoded = MarkupPngBaker.Encode(image!);

        await Assert.That(PngProbe.TryReadDimensions(encoded, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(24);
        await Assert.That(h).IsEqualTo(12);

        // And the re-encoded bytes bake again — the encoder output is valid decoder input.
        await Assert.That(MarkupPngBaker.TryBake(encoded, RectModel(), out _, out _)).IsTrue();
    }

    // ── minimal test-side PNG writer (filter-0 rows, own CRC/Adler) ──

    private static byte[] TestPng(int w, int h, byte r, byte g, byte b, int colorType = 6)
    {
        int channels = colorType == 6 ? 4 : colorType == 2 ? 3 : 1;
        var raw = new byte[h * (1 + (w * channels))];
        for (int y = 0; y < h; y++)
        {
            int row = y * (1 + (w * channels));
            raw[row] = 0;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    byte v = channels == 4
                        ? c == 0 ? r : c == 1 ? g : c == 2 ? b : (byte)255
                        : c == 0 ? r : (byte)0;
                    raw[row + 1 + ((x * channels) + c)] = v;
                }
            }
        }

        byte[] deflated;
        using (var output = new MemoryStream())
        {
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }

            deflated = output.ToArray();
        }

        var zlib = new byte[2 + deflated.Length + 4];
        zlib[0] = 0x78;
        zlib[1] = 0x01;
        Array.Copy(deflated, 0, zlib, 2, deflated.Length);
        BinaryPrimitives.WriteUInt32BigEndian(zlib.AsSpan(2 + deflated.Length, 4), TestAdler(raw));

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8;
        ihdr[9] = (byte)colorType;

        using var png = new MemoryStream();
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        png.Write(signature, 0, signature.Length);
        TestChunk(png, "IHDR"u8, ihdr);
        TestChunk(png, "IDAT"u8, zlib);
        TestChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void TestChunk(MemoryStream png, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        png.Write(length);
        png.Write(type);
        if (data.Length > 0)
        {
            png.Write(data, 0, data.Length);
        }

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
        {
            crc = TestCrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = TestCrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        crc ^= 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        png.Write(crcBytes);
    }

    private static readonly uint[] TestCrcTable = BuildTestCrcTable();

    private static uint[] BuildTestCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint TestAdler(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }
}
