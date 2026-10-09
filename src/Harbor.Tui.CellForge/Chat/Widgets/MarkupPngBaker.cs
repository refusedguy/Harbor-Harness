using System.Buffers.Binary;
using System.IO.Compression;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Bakes a <see cref="MarkupAnnotationModel" /> onto source PNG pixels and
/// re-encodes the composite as a new PNG (KILLER_FEATURES §2.7 Feature 14,
/// issue #400 slice 1/2).
/// <para><b>BCL-only, no image NuGet.</b> CellForge stays trim and AOT-clean,
/// so the codec here is hand-rolled: signature + chunk walk, zlib wrap around
/// <see cref="DeflateStream" />, PNG unfiltering (None/Sub/Up/Average/Paeth)
/// and a filter-0 encoder. Decode accepts 8-bit RGB/RGBA non-interlaced —
/// what screenshots are; anything else is a display-string error, never an
/// exception. The text primitive uses a built-in 3×5 bitmap font (ASCII
/// letters/digits + basic punctuation, uppercased), scaled by the
/// annotation weight — honest pixels at any size, no font file needed.</para>
/// <para>All failures surface as <c>error</c> text (unreadable
/// source, unwritable target is the host's concern, non-PNG input); this
/// class never throws out of <see cref="TryBake" />.</para>
/// </summary>
public static class MarkupPngBaker
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Maximum accepted dimension (100 MP guards a corrupt IHDR from allocating gigabytes).</summary>
    private const int MaxDimension = 16384;

    /// <summary>Annotation red (opaque): rectangles, arrows, label boxes.</summary>
    private const uint Red = 0xFF0000FF;

    /// <summary>Label glyph white (opaque).</summary>
    private const uint White = 0xFFFFFFFF;

    /// <summary>
    /// Composites <paramref name="model" /> onto <paramref name="sourcePng" />
    /// and encodes the result. False with <paramref name="error" /> set on any
    /// failure; never throws.
    /// </summary>
    public static bool TryBake(byte[]? sourcePng, MarkupAnnotationModel model, out byte[]? png, out string error)
    {
        png = null;
        error = string.Empty;
        try
        {
            if (!TryDecode(sourcePng, out var image, out error))
            {
                return false;
            }

            Draw(image!, model);
            png = Encode(image!);
            return true;
        }
        catch (InvalidDataException ex)
        {
            png = null;
            error = "Cannot bake annotations: " + ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            png = null;
            error = "Cannot bake annotations: " + ex.Message;
            return false;
        }
        catch (IndexOutOfRangeException ex)
        {
            png = null;
            error = "Cannot bake annotations: " + ex.Message;
            return false;
        }
    }

    /// <summary>Decoded RGBA image (row-major, 4 bytes per pixel).</summary>
    internal sealed class RgbaImage(int width, int height, byte[] pixels)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Pixels { get; } = pixels;
    }

    internal static bool TryDecode(byte[]? data, out RgbaImage? image, out string error)
    {
        image = null;
        error = string.Empty;
        if (data is null || data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            error = "Not a PNG image (bad signature).";
            return false;
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        bool seenIhdr = false;
        var idat = new List<byte[]>();
        int idatLength = 0;

        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            var type = data.AsSpan(pos + 4, 4);
            if (length > (uint)(data.Length - pos - 12))
            {
                error = "Cannot decode PNG (truncated or corrupt).";
                return false;
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (seenIhdr || length != 13)
                {
                    error = "Cannot decode PNG (truncated or corrupt).";
                    return false;
                }

                seenIhdr = true;
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 12, 4));
                bitDepth = data[pos + 16];
                colorType = data[pos + 17];
                int compression = data[pos + 18];
                int filter = data[pos + 19];
                int interlace = data[pos + 20];
                if (bitDepth != 8 || (colorType != 2 && colorType != 6) || compression != 0 || filter != 0 || interlace != 0)
                {
                    error = "Unsupported PNG: 8-bit RGB/RGBA non-interlaced only.";
                    return false;
                }

                if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
                {
                    error = "Cannot decode PNG (truncated or corrupt).";
                    return false;
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                var chunk = new byte[length];
                Array.Copy(data, pos + 8, chunk, 0, (int)length);
                idat.Add(chunk);
                idatLength += (int)length;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            pos += 8 + (int)length + 4;
        }

        if (!seenIhdr || idatLength == 0)
        {
            error = "Cannot decode PNG (truncated or corrupt).";
            return false;
        }

        var zlib = new byte[idatLength];
        int at = 0;
        foreach (byte[] chunk in idat)
        {
            Array.Copy(chunk, 0, zlib, at, chunk.Length);
            at += chunk.Length;
        }

        if (zlib.Length < 6 || (zlib[0] & 0x0F) != 8)
        {
            error = "Cannot decode PNG (truncated or corrupt).";
            return false;
        }

        int channels = colorType == 6 ? 4 : 3;
        int stride = width * channels;
        long expected = (long)height * (stride + 1);
        if (expected > int.MaxValue)
        {
            error = "Cannot decode PNG (truncated or corrupt).";
            return false;
        }

        byte[] raw;
        using (var input = new MemoryStream(zlib, 2, zlib.Length - 6, writable: false))
        using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream((int)expected))
        {
            try
            {
                inflate.CopyTo(output);
            }
            catch (InvalidDataException)
            {
                error = "Cannot decode PNG (truncated or corrupt).";
                return false;
            }

            raw = output.ToArray();
        }

        if (raw.Length < expected || !CheckAdler(raw, zlib.AsSpan(zlib.Length - 4, 4)))
        {
            error = "Cannot decode PNG (truncated or corrupt).";
            return false;
        }

        var pixels = new byte[width * height * 4];
        var prev = new byte[stride];
        int bpp = channels;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            int filterType = raw[rowStart];
            if (filterType > 4)
            {
                error = "Cannot decode PNG (truncated or corrupt).";
                return false;
            }

            var recon = new byte[stride];
            for (int x = 0; x < stride; x++)
            {
                int filt = raw[rowStart + 1 + x];
                int left = x >= bpp ? recon[x - bpp] : 0;
                int up = prev[x];
                int upperLeft = x >= bpp ? prev[x - bpp] : 0;
                int value = filterType switch
                {
                    0 => filt,
                    1 => filt + left,
                    2 => filt + up,
                    3 => filt + ((left + up) >> 1),
                    _ => filt + Paeth(left, up, upperLeft),
                };
                recon[x] = (byte)value;
            }

            Array.Copy(recon, prev, stride);
            for (int x = 0; x < width; x++)
            {
                int dst = (y * width * 4) + (x * 4);
                int src = (y * stride) + (x * channels);
                pixels[dst] = recon[src];
                pixels[dst + 1] = recon[src + 1];
                pixels[dst + 2] = recon[src + 2];
                pixels[dst + 3] = channels == 4 ? recon[src + 3] : (byte)255;
            }
        }

        image = new RgbaImage(width, height, pixels);
        return true;
    }

    internal static void Draw(RgbaImage image, MarkupAnnotationModel model)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Items.Length == 0 || image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        foreach (MarkupAnnotation item in model.Items)
        {
            switch (item.Kind)
            {
                case MarkupKind.Rectangle:
                    DrawRect(image, item);
                    break;
                case MarkupKind.Arrow:
                    DrawArrow(image, item);
                    break;
                default:
                    DrawLabel(image, item);
                    break;
            }
        }
    }

    internal static byte[] Encode(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int stride = image.Width * 4;
        var raw = new byte[image.Height * (stride + 1)];
        for (int y = 0; y < image.Height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(image.Pixels, y * stride, raw, (y * (stride + 1)) + 1, stride);
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

        uint adler = Adler(raw);
        var zlib = new byte[2 + deflated.Length + 4];
        zlib[0] = 0x78;
        zlib[1] = 0x01;
        Array.Copy(deflated, 0, zlib, 2, deflated.Length);
        BinaryPrimitives.WriteUInt32BigEndian(zlib.AsSpan(2 + deflated.Length, 4), adler);

        using var png = new MemoryStream();
        png.Write(PngSignature, 0, PngSignature.Length);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)image.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, zlib);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(MemoryStream png, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        png.Write(length);
        png.Write(type);
        if (data.Length > 0)
        {
            png.Write(data, 0, data.Length);
        }

        uint crc = Crc(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        png.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
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

    private static uint Crc(ReadOnlySpan<byte> type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Adler(byte[] data)
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

    private static bool CheckAdler(byte[] data, ReadOnlySpan<byte> expected)
    {
        uint actual = Adler(data);
        return BinaryPrimitives.ReadUInt32BigEndian(expected) == actual;
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        int p = (left + up) - upperLeft;
        int pa = Math.Abs(p - left);
        int pb = Math.Abs(p - up);
        int pc = Math.Abs(p - upperLeft);
        return pa <= pb && pa <= pc ? left : pb <= pc ? up : upperLeft;
    }

    private static (int X, int Y) ToPixels(NormalizedPoint point, RgbaImage image) =>
        (Math.Clamp((int)Math.Round(point.X * (image.Width - 1), MidpointRounding.AwayFromZero), 0, image.Width - 1),
            Math.Clamp((int)Math.Round(point.Y * (image.Height - 1), MidpointRounding.AwayFromZero), 0, image.Height - 1));

    private static void SetPixel(RgbaImage image, int x, int y, uint rgba)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
        {
            return;
        }

        int at = (y * image.Width * 4) + (x * 4);
        image.Pixels[at] = (byte)(rgba >> 24);
        image.Pixels[at + 1] = (byte)(rgba >> 16);
        image.Pixels[at + 2] = (byte)(rgba >> 8);
        image.Pixels[at + 3] = (byte)rgba;
    }

    private static void Stamp(RgbaImage image, int x, int y, int half, uint rgba)
    {
        for (int dy = -half; dy <= half; dy++)
        {
            for (int dx = -half; dx <= half; dx++)
            {
                SetPixel(image, x + dx, y + dy, rgba);
            }
        }
    }

    private static void Line(RgbaImage image, int x0, int y0, int x1, int y1, int half, uint rgba)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            Stamp(image, x0, y0, half, rgba);
            if (x0 == x1 && y0 == y1)
            {
                return;
            }

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static void DrawRect(RgbaImage image, MarkupAnnotation item)
    {
        var (x0, y0) = ToPixels(item.From, image);
        var (x1, y1) = ToPixels(item.To, image);
        int half = item.Weight / 2;
        Line(image, x0, y0, x1, y0, half, Red);
        Line(image, x1, y0, x1, y1, half, Red);
        Line(image, x0: x1, y0: y1, x1: x0, y1: y1, half: half, rgba: Red);
        Line(image, x0: x0, y0: y1, x1: x0, y1: y0, half: half, rgba: Red);
    }

    private static void DrawArrow(RgbaImage image, MarkupAnnotation item)
    {
        var (x0, y0) = ToPixels(item.From, image);
        var (x1, y1) = ToPixels(item.To, image);
        int half = item.Weight / 2;
        if (x0 == x1 && y0 == y1)
        {
            Stamp(image, x0, y0, half, Red);
            return;
        }

        Line(image, x0, y0, x1, y1, half, Red);
        double angle = Math.Atan2(y1 - y0, x1 - x0);
        double headLen = 5 + (2 * item.Weight);
        foreach (double spread in new[] { 2.618, -2.618 })
        {
            int hx = x1 + (int)Math.Round(headLen * Math.Cos(angle + spread), MidpointRounding.AwayFromZero);
            int hy = y1 + (int)Math.Round(headLen * Math.Sin(angle + spread), MidpointRounding.AwayFromZero);
            Line(image, x1, y1, hx, hy, half, Red);
        }
    }

    private static void DrawLabel(RgbaImage image, MarkupAnnotation item)
    {
        if (string.IsNullOrEmpty(item.Text))
        {
            return;
        }

        string text = item.Text.ToUpperInvariant();
        int scale = Math.Clamp(item.Weight, MarkupAnnotation.MinWeight, MarkupAnnotation.MaxWeight);
        int glyphW = 3 * scale;
        int glyphH = 5 * scale;
        int advance = 4 * scale;
        int pad = Math.Max(1, scale);
        int boxW = ((text.Length - 1) * advance) + glyphW + (2 * pad);
        int boxH = glyphH + (2 * pad);

        var (ax, ay) = ToPixels(item.From, image);
        for (int y = 0; y < boxH; y++)
        {
            for (int x = 0; x < boxW; x++)
            {
                SetPixel(image, ax + x, ay + y, Red);
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            DrawGlyph(image, text[i], ax + pad + (i * advance), ay + pad, scale);
        }
    }

    private static void DrawGlyph(RgbaImage image, char glyph, int x, int y, int scale)
    {
        if (!Font.TryGetValue(glyph, out string[]? rows))
        {
            rows = Font['?'];
        }
        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] != '#')
                {
                    continue;
                }

                for (int dy = 0; dy < scale; dy++)
                {
                    for (int dx = 0; dx < scale; dx++)
                    {
                        SetPixel(image, x + (c * scale) + dx, y + (r * scale) + dy, White);
                    }
                }
            }
        }
    }

    private static readonly Dictionary<char, string[]> Font = new()
    {
        [' '] = ["...", "...", "...", "...", "..."],
        ['!'] = ["#..", "#..", "#..", "...", "#.."],
        ['"'] = ["#.#", "#.#", "...", "...", "..."],
        ['\''] = ["#..", "#..", "...", "...", "..."],
        ['('] = [".#.", "#..", "#..", "#..", ".#."],
        [')'] = ["#..", ".#.", ".#.", ".#.", "#.."],
        [','] = ["...", "...", "...", "#..", "#.."],
        ['-'] = ["...", "...", "###", "...", "..."],
        ['.'] = ["...", "...", "...", "...", "#.."],
        ['/'] = ["..#", "..#", ".#.", "#..", "#.."],
        ['0'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['1'] = [".#.", "##.", ".#.", ".#.", "###"],
        ['2'] = ["###", "..#", "###", "#..", "###"],
        ['3'] = ["###", "..#", "###", "..#", "###"],
        ['4'] = ["#.#", "#.#", "###", "..#", "..#"],
        ['5'] = ["###", "#..", "###", "..#", "###"],
        ['6'] = ["###", "#..", "###", "#.#", "###"],
        ['7'] = ["###", "..#", "..#", ".#.", ".#."],
        ['8'] = ["###", "#.#", "###", "#.#", "###"],
        ['9'] = ["###", "#.#", "###", "..#", "###"],
        [':'] = ["...", "#..", "...", "#..", "..."],
        [';'] = ["...", "#..", "...", "#..", "#.."],
        ['?'] = ["###", "..#", ".#.", "...", ".#."],
        ['_'] = ["...", "...", "...", "...", "###"],
        ['A'] = [".#.", "#.#", "###", "#.#", "#.#"],
        ['B'] = ["##.", "#.#", "##.", "#.#", "##."],
        ['C'] = [".##", "#..", "#..", "#..", ".##"],
        ['D'] = ["##.", "#.#", "#.#", "#.#", "##."],
        ['E'] = ["###", "#..", "##.", "#..", "###"],
        ['F'] = ["###", "#..", "##.", "#..", "#.."],
        ['G'] = [".##", "#..", "#.#", "#.#", ".##"],
        ['H'] = ["#.#", "#.#", "###", "#.#", "#.#"],
        ['I'] = ["###", ".#.", ".#.", ".#.", "###"],
        ['J'] = ["..#", "..#", "..#", "#.#", "###"],
        ['K'] = ["#.#", "#.#", "##.", "#.#", "#.#"],
        ['L'] = ["#..", "#..", "#..", "#..", "###"],
        ['M'] = ["#.#", "###", "###", "#.#", "#.#"],
        ['N'] = ["#.#", "###", "###", "###", "#.#"],
        ['O'] = [".#.", "#.#", "#.#", "#.#", ".#."],
        ['P'] = ["##.", "#.#", "##.", "#..", "#.."],
        ['Q'] = [".#.", "#.#", "#.#", "##.", ".##"],
        ['R'] = ["##.", "#.#", "##.", "#.#", "#.#"],
        ['S'] = [".##", "#..", ".#.", "..#", "##."],
        ['T'] = ["###", ".#.", ".#.", ".#.", ".#."],
        ['U'] = ["#.#", "#.#", "#.#", "#.#", "###"],
        ['V'] = ["#.#", "#.#", "#.#", "#.#", ".#."],
        ['W'] = ["#.#", "#.#", "###", "###", "#.#"],
        ['X'] = ["#.#", "#.#", ".#.", "#.#", "#.#"],
        ['Y'] = ["#.#", "#.#", ".#.", ".#.", ".#."],
        ['Z'] = ["###", "..#", ".#.", "#..", "###"],
    };
}
