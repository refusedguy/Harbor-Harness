using Harbor.Application.Attachments;

namespace Harbor.Application.Tests.Attachments;

/// <summary>
///     Issue #386 — the MIME type on the wire comes from magic bytes, never the
///     file extension, so the probe is what keeps a mislabelled file from
///     becoming a provider 400.
/// </summary>
public class ImageProbeTests
{
    [Test]
    public async Task TryDetectMimeType_PngJpegGifWebp_AreRecognised()
    {
        await Assert.That(ImageProbe.TryDetectMimeType(Png(), out string png)).IsTrue();
        await Assert.That(png).IsEqualTo(ImageProbe.PngMimeType);

        await Assert.That(ImageProbe.TryDetectMimeType([0xFF, 0xD8, 0xFF, 0xE0], out string jpeg)).IsTrue();
        await Assert.That(jpeg).IsEqualTo(ImageProbe.JpegMimeType);

        await Assert.That(ImageProbe.TryDetectMimeType("GIF89a"u8.ToArray(), out string gif)).IsTrue();
        await Assert.That(gif).IsEqualTo(ImageProbe.GifMimeType);

        await Assert.That(ImageProbe.TryDetectMimeType(WebpLossy(), out string webp)).IsTrue();
        await Assert.That(webp).IsEqualTo(ImageProbe.WebpMimeType);
    }

    [Test]
    public async Task TryDetectMimeType_NonImages_AreRejected()
    {
        await Assert.That(ImageProbe.TryDetectMimeType("hello world"u8.ToArray(), out _)).IsFalse();
        await Assert.That(ImageProbe.TryDetectMimeType([], out _)).IsFalse();
        await Assert.That(ImageProbe.TryDetectMimeType([0x42, 0x4D], out _)).IsFalse(); // BMP magic
    }

    [Test]
    public async Task TryDetectMimeType_TruncatedPngSignature_IsNotAPng()
    {
        // Only 4 of the 8 signature bytes: must not claim image/png.
        await Assert.That(ImageProbe.TryDetectMimeType([0x89, 0x50, 0x4E, 0x47], out _)).IsFalse();
    }

    [Test]
    public async Task TryReadDimensions_Png_ReadsIhdr()
    {
        await Assert.That(ImageProbe.TryReadDimensions(Png(1024, 768), ImageProbe.PngMimeType, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(1024);
        await Assert.That(h).IsEqualTo(768);
    }

    [Test]
    public async Task TryReadDimensions_Gif_ReadsLittleEndianScreenDescriptor()
    {
        byte[] gif = "GIF89a"u8.ToArray();
        var data = new byte[10];
        "GIF89a"u8.CopyTo(data);
        data[6] = 0xE0; data[7] = 0x00; // 224 LE
        data[8] = 0x40; data[9] = 0x01; // 320 LE

        await Assert.That(ImageProbe.TryReadDimensions(data, ImageProbe.GifMimeType, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(224);
        await Assert.That(h).IsEqualTo(320);
    }

    [Test]
    public async Task TryReadDimensions_Jpeg_ReadsStartOfFrameSegment()
    {
        // SOI, APP0 segment (16 bytes), then SOF0 carrying 480×640.
        var data = new List<byte> { 0xFF, 0xD8 };
        data.AddRange([0xFF, 0xE0, 0x00, 0x10]);
        data.AddRange(new byte[14]);
        data.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        data.AddRange([0x02, 0x58]); // height 600
        data.AddRange([0x01, 0x90]); // width 400
        data.AddRange(new byte[10]);

        await Assert.That(ImageProbe.TryReadDimensions([.. data], ImageProbe.JpegMimeType, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(400);
        await Assert.That(h).IsEqualTo(600);
    }

    [Test]
    public async Task TryReadDimensions_WebpLossy_StripsTheTwoScalingBits()
    {
        byte[] webp = WebpLossy(1920, 1080);

        await Assert.That(ImageProbe.TryReadDimensions(webp, ImageProbe.WebpMimeType, out int w, out int h)).IsTrue();
        await Assert.That(w).IsEqualTo(1920);
        await Assert.That(h).IsEqualTo(1080);
    }

    [Test]
    public async Task TryReadDimensions_TruncatedPayload_ReturnsFalseNotGarbage()
    {
        await Assert.That(ImageProbe.TryReadDimensions(Png(), ImageProbe.PngMimeType, out int w, out int h)).IsFalse();
        await Assert.That(w).IsEqualTo(0);
        await Assert.That(h).IsEqualTo(0);
    }

    [Test]
    public async Task TryProbe_SupportedFormatWithUnknownDimensions_StillSucceeds()
    {
        // A valid GIF header whose descriptor is cut short: keep the attachment,
        // report no dimensions. Rejecting it would be the wrong trade.
        await Assert.That(ImageProbe.TryProbe("GIF89a"u8.ToArray(), out string mime, out int w, out int h)).IsTrue();
        await Assert.That(mime).IsEqualTo(ImageProbe.GifMimeType);
        await Assert.That(w).IsEqualTo(0);
        await Assert.That(h).IsEqualTo(0);
    }

    private static byte[] Png(int width = 1, int height = 1)
    {
        var data = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(data, 0);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        WriteBigEndian(data, 16, width);
        WriteBigEndian(data, 20, height);
        return data;
    }

    private static byte[] WebpLossy(int width = 1, int height = 1)
    {
        var data = new byte[30];
        "RIFF"u8.CopyTo(data.AsSpan(0));
        "WEBP"u8.CopyTo(data.AsSpan(8));
        "VP8 "u8.CopyTo(data.AsSpan(12));
        data[26] = (byte)(width & 0xFF);
        data[27] = (byte)(width >> 8);
        data[28] = (byte)(height & 0xFF);
        data[29] = (byte)(height >> 8);
        return data;
    }

    private static void WriteBigEndian(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
