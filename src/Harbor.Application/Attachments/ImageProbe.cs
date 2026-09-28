namespace Harbor.Application.Attachments;

/// <summary>
///     Magic-byte image probe (issue #386): identifies an image container and
///     its pixel dimensions from the file HEADER only.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately independent of the file extension and of any imaging
///         library: providers reject an image whose declared MIME type lies,
///         so the type we put on the wire must come from the bytes. A
///         <c>shot.png</c> that is really a JPEG has to reach the model as
///         <c>image/jpeg</c>.
///     </para>
///     <para>
///         Supported containers: PNG, JPEG, GIF, WebP — the four every
///         OpenAI-compatible and Anthropic vision endpoint accepts. Anything
///         else (BMP, TIFF, SVG, HEIC…) is reported as unsupported at attach
///         time instead of becoming a provider 400 at request time.
///     </para>
///     <para>
///         All methods are pure and allocation-free (span reads only), so the
///         probe is safe on the request hot path as well as at attach time.
///     </para>
/// </remarks>
public static class ImageProbe
{
    /// <summary>MIME type for PNG.</summary>
    public const string PngMimeType = "image/png";

    /// <summary>MIME type for JPEG.</summary>
    public const string JpegMimeType = "image/jpeg";

    /// <summary>MIME type for GIF.</summary>
    public const string GifMimeType = "image/gif";

    /// <summary>MIME type for WebP.</summary>
    public const string WebpMimeType = "image/webp";

    /// <summary>Human-readable list of the accepted containers, for error messages.</summary>
    public const string SupportedFormats = "PNG, JPEG, GIF or WebP";

    /// <summary>
    ///     Identify the image container from its leading bytes.
    /// </summary>
    /// <param name="header">The first bytes of the file (12 or more is enough).</param>
    /// <param name="mimeType">The probed MIME type on success.</param>
    /// <returns><see langword="true" /> when the header matches a supported container.</returns>
    public static bool TryDetectMimeType(ReadOnlySpan<byte> header, out string mimeType)
    {
        if (IsPng(header))
        {
            mimeType = PngMimeType;
            return true;
        }

        if (IsGif(header))
        {
            mimeType = GifMimeType;
            return true;
        }

        if (IsJpeg(header))
        {
            mimeType = JpegMimeType;
            return true;
        }

        if (IsWebp(header))
        {
            mimeType = WebpMimeType;
            return true;
        }

        mimeType = string.Empty;
        return false;
    }

    /// <summary>
    ///     Read the pixel dimensions of a probed image.
    /// </summary>
    /// <param name="data">The full file bytes.</param>
    /// <param name="mimeType">The MIME type previously returned by <see cref="TryDetectMimeType" />.</param>
    /// <param name="width">Pixel width, 0 when unknown.</param>
    /// <param name="height">Pixel height, 0 when unknown.</param>
    /// <returns><see langword="true" /> when both dimensions were recovered.</returns>
    public static bool TryReadDimensions(ReadOnlySpan<byte> data, string mimeType, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (mimeType == PngMimeType)
            return TryReadPngDimensions(data, out width, out height);

        if (mimeType == JpegMimeType)
            return TryReadJpegDimensions(data, out width, out height);

        if (mimeType == GifMimeType)
            return TryReadGifDimensions(data, out width, out height);

        if (mimeType == WebpMimeType)
            return TryReadWebpDimensions(data, out width, out height);

        return false;
    }

    /// <summary>
    ///     Probe container + dimensions in one pass over the header.
    /// </summary>
    /// <param name="data">The full file bytes.</param>
    /// <param name="mimeType">The probed MIME type.</param>
    /// <param name="width">Pixel width, 0 when unknown.</param>
    /// <param name="height">Pixel height, 0 when unknown.</param>
    /// <returns><see langword="true" /> when the container is supported; dimensions are best-effort.</returns>
    public static bool TryProbe(ReadOnlySpan<byte> data, out string mimeType, out int width, out int height)
    {
        if (!TryDetectMimeType(data, out mimeType))
        {
            width = 0;
            height = 0;
            return false;
        }

        // A truncated or unusual file can still be a valid image: keep the
        // attachment and report unknown dimensions rather than reject it.
        TryReadDimensions(data, mimeType, out width, out height);
        return true;
    }

    private static bool IsPng(ReadOnlySpan<byte> h) =>
        h.Length >= 8
        && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47
        && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A;

    private static bool IsGif(ReadOnlySpan<byte> h) =>
        h.Length >= 6 && h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F'
        && h[3] == (byte)'8' && (h[4] == (byte)'7' || h[4] == (byte)'9') && h[5] == (byte)'a';

    private static bool IsJpeg(ReadOnlySpan<byte> h) =>
        h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;

    private static bool IsWebp(ReadOnlySpan<byte> h) =>
        h.Length >= 12
        && h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F'
        && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P';

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> d, out int width, out int height)
    {
        // Signature (8) + length (4) + "IHDR" (4) → width at 16, height at 20.
        width = 0;
        height = 0;
        if (d.Length < 24
            || d[12] != (byte)'I' || d[13] != (byte)'H' || d[14] != (byte)'D' || d[15] != (byte)'R')
        {
            return false;
        }

        width = ReadBigEndianInt32(d, 16);
        height = ReadBigEndianInt32(d, 20);
        return width > 0 && height > 0;
    }

    private static bool TryReadGifDimensions(ReadOnlySpan<byte> d, out int width, out int height)
    {
        // Header (6) + logical screen descriptor: width LE at 6, height LE at 8.
        width = 0;
        height = 0;
        if (d.Length < 10)
        {
            return false;
        }

        width = ReadLittleEndianUInt16(d, 6);
        height = ReadLittleEndianUInt16(d, 8);
        return width > 0 && height > 0;
    }

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> d, out int width, out int height)
    {
        // Walk the marker segments until a Start-Of-Frame: height at +5, width at +7.
        width = 0;
        height = 0;
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = d[i + 1];
            // Standalone markers carry no length field.
            if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            int segmentLength = (d[i + 2] << 8) | d[i + 3];
            if (segmentLength < 2)
            {
                return false;
            }

            bool isStartOfFrame = marker is >= 0xC0 and <= 0xCF
                                  && marker is not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                height = (d[i + 5] << 8) | d[i + 6];
                width = (d[i + 7] << 8) | d[i + 8];
                return width > 0 && height > 0;
            }

            i += 2 + segmentLength;
        }

        return false;
    }

    private static bool TryReadWebpDimensions(ReadOnlySpan<byte> d, out int width, out int height)
    {
        // "RIFF" (4) + size (4) + "WEBP" (4) + fourcc (4) → payload at 20.
        width = 0;
        height = 0;
        if (d.Length < 30)
        {
            return false;
        }

        if (d[12] == (byte)'V' && d[13] == (byte)'P' && d[14] == (byte)'8' && d[15] == (byte)' ')
        {
            // Lossy: 3-byte frame tag, then 14-bit LE width/height.
            width = ReadLittleEndianUInt16(d, 26) & 0x3FFF;
            height = ReadLittleEndianUInt16(d, 28) & 0x3FFF;
            return width > 0 && height > 0;
        }

        if (d[12] == (byte)'V' && d[13] == (byte)'P' && d[14] == (byte)'8' && d[15] == (byte)'L')
        {
            // Lossless: 1 signature byte, 3 version bytes, then 14-bit LE dims.
            width = ReadLittleEndianUInt16(d, 21) & 0x3FFF;
            height = ReadLittleEndianUInt16(d, 23) & 0x3FFF;
            return width > 0 && height > 0;
        }

        if (d[12] == (byte)'V' && d[13] == (byte)'P' && d[14] == (byte)'8' && d[15] == (byte)'X')
        {
            // Extended: 24-bit LE canvas width-1 / height-1.
            width = ReadLittleEndianInt24(d, 24) + 1;
            height = ReadLittleEndianInt24(d, 27) + 1;
            return width > 0 && height > 0;
        }

        return false;
    }

    /// <summary>
    ///     PNG dimensions are 32-bit UNSIGNED big-endian; masking the sign bit
    ///     keeps an absurd header from producing a negative width.
    /// </summary>
    private static int ReadBigEndianInt32(ReadOnlySpan<byte> d, int offset) =>
        ((d[offset] & 0x7F) << 24) | (d[offset + 1] << 16) | (d[offset + 2] << 8) | d[offset + 3];

    private static int ReadLittleEndianUInt16(ReadOnlySpan<byte> d, int offset) => d[offset] | (d[offset + 1] << 8);

    private static int ReadLittleEndianInt24(ReadOnlySpan<byte> d, int offset) =>
        d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16);
}