using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>Inline-graphics protocol primitives (kitty passthrough + Sixel).</summary>
public class GraphicsTests
{
    [Test]
    public async Task PngSize_ReadsIhdrBigEndian()
    {
        var png = new byte[32];
        png[0] = 0x89;
        png[1] = (byte)'P';
        // width 0x00000100 @16, height 0x00000040 @20
        png[18] = 1;   // width 256
        png[23] = 0x40; // height 64

        await Assert.That(Graphics.PngSize(png)).IsEqualTo((256, 64));
    }

    [Test]
    public async Task KittyPngInline_ChunksBase64_WithFinalMarker()
    {
        var png = MakeFakePng(Graphics.KittyChunkChars * 3 / 4 + 8); // ≥2 base64 chunks

        string seq = Encoding.ASCII.GetString(Graphics.KittyPngInline(png));
        if (!Graphics.PngSize(png).HasValue)
        {
            throw new InvalidOperationException("precondition failed: MakeFakePng header rejected");
        }

        AssertStartsAndEndsDcs(seq);
        // Multiple DCS strings for a chunked payload.
        await Assert.That(seq.Count(c => c == '\u001B') / 2).IsGreaterThanOrEqualTo(2);

        // Final chunk carries m=0; every earlier chunk m=1.
        await Assert.That(seq.Contains("m=0;", StringComparison.Ordinal)).IsTrue();
        await Assert.That(seq.Contains("m=1;", StringComparison.Ordinal)).IsTrue();

        static void AssertStartsAndEndsDcs(string s)
        {
            if (!s.StartsWith("\u001B_Gf=100,a=T,m=", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"must open with Gf=100,a=T header; got len={{seq.Length}} head={{string.Create(seq.Length, seq, (span, s) => s.AsSpan(0, Math.Min(8, span.Length)).CopyTo(span))}}");
            }

            if (!s.EndsWith("\u001B\\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("must terminate with ST");
            }
        }
    }

    [Test]
    public async Task KittyPngInline_EmptyInput_ProducesEmpty()
    {
        byte[] result = Graphics.KittyPngInline(ReadOnlySpan<byte>.Empty);
        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EncodeSixel_EmitsHeaderPaletteAndTerminator()
    {
        // 2×1: left accent, right black.
        var rgb = new byte[] { 0x39, 0xBA, 0xE6, 0x00, 0x00, 0x00 };

        string sixel = Encoding.ASCII.GetString(Graphics.EncodeSixel(rgb, 2, 1));

        await Assert.That(sixel).Contains("\u001BPq\"1;1;2;1");
        await Assert.That(sixel).Contains("#5");                       // accent slot referenced
        await Assert.That(sixel.EndsWith("\u001B\\", StringComparison.Ordinal)).IsTrue();
        await Assert.That(sixel).EndsWith("\u001B\\");
    }

    [Test]
    public async Task EncodeSixel_BandBits_MapLsbToTopRow()
    {
        // 1×6: top pixel white (bit0), rest error — slot1 (white) pass must be
        // '?'-padded for its single column; bits live in the slot2/… passes.
        var rgb = new List<byte>(18);
        for (int y = 0; y < 6; y++)
        {
            if (y == 0)
            {
                rgb.AddRange([0xFF, 0xFF, 0xFF]);
            }
            else
            {
                rgb.AddRange([0xFF, 0x6B, 0x6B]); // error red
            }
        }

        string sixel = Encoding.ASCII.GetString(Graphics.EncodeSixel(rgb.ToArray(), 1, 6));
        int body = sixel.IndexOf('\n');
        string bandPasses = sixel[(body + 1)..^2];

        // White pass ('#1'): top row bit0 ⇒ '@' (0x3F+1). Red pass ('#2'):
        // rows 1..5 set → '}' (bits 0b111110 = 0x3E → 0x3F+0x3E).
        await Assert.That(bandPasses).Contains("#1");
        await Assert.That(bandPasses).Contains("@"); // white, bit0 only
        await Assert.That(bandPasses).Contains("}"); // red, bits 1..5
    }

    [Test]
    public async Task NearestSlot_ClassifiesPrimaries()
    {
        await Assert.That(Graphics.NearestSlot(0x39, 0xBA, 0xE6)).IsEqualTo(5);
        await Assert.That(Graphics.NearestSlot(0x7F, 0xD9, 0x62)).IsEqualTo(3);
        await Assert.That(Graphics.NearestSlot(0x10, 0x10, 0x10)).IsEqualTo(0);
    }

    /// <summary>Synthetic PNG-shaped buffer: real signature + IHDR dims (64x64) + filler.</summary>
    private static byte[] MakeFakePng(int totalBytes)
    {
        var png = new byte[Math.Max(32, totalBytes)];
        png[0] = 0x89;
        png[1] = (byte)'P';
        png[2] = (byte)'N';
        png[3] = (byte)'G';
        png[19] = 64;  // width
        png[23] = 64;  // height
        return png;
    }
}
