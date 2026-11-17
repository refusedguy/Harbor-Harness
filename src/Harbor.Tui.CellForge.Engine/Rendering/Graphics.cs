using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Inline image graphics for terminals that advertise them (HDS v1 §Media):
/// kitty graphics passthrough and a self-contained Sixel encoder.
///
/// The cell-diff pipeline owns exactly one output seam —
/// <see cref="ITerminalBackend.WriteAsync" /> — so images ride INSIDE the
/// composed frame byte span; terminals draw DCS payloads on arrival while
/// the diff layer keeps treating cells as opaque text.
///
/// Pure byte functions — deterministic, AOT-safe, no decoding libraries:
/// PNG dimensions come straight from the IHDR header; Sixel quantizes raw
/// RGB24 against the fixed catalog palette below.
/// </summary>
public static class Graphics
{
    private const char EscChar = '\u001B';

    /// <summary>Kitty chunked-transfer limit (base64 characters per DCS string).</summary>
    public const int KittyChunkChars = 4096;

    /// <summary>Sixel geometry: one character band covers six pixel rows.</summary>
    public const int SixelBandRows = 6;

    /// <summary>
    /// Fixed quantization palette (RGB triplets): primaries + grey ramp.
    /// Slot indices are the Sixel color numbers emitted as #&lt;n&gt; headers.
    /// </summary>
    public static readonly byte[][] SixelPalette =
    [
        [0x00, 0x00, 0x00], // 0 black
        [0xFF, 0xFF, 0xFF], // 1 white
        [0xFF, 0x6B, 0x6B], // 2 error
        [0x7F, 0xD9, 0x62], // 3 success
        [0xFF, 0xB4, 0x54], // 4 warning
        [0x39, 0xBA, 0xE6], // 5 accent
        [0xD2, 0xA6, 0xFF], // 6 tool
        [0xF2, 0x96, 0x68], // 7 system
        [0x5C, 0x67, 0x73], // 8 muted
        [0xB3, 0xB9, 0xC5], // 9 text grey
    ];

    /// <summary>PNG dimensions from the IHDR header (width @16, height @20, big-endian).</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length >= 24 && png[0] == 0x89 && png[1] == (byte)'P')
        {
            int w = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
            int h = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
            if (w > 0 && h > 0)
            {
                return (w, h);
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// Kitty graphics passthrough for a raw PNG payload: f=100 (PNG format),
    /// a=T (display inline at cursor), base64 transfer chunked every
    /// <see cref="KittyChunkChars" /> (m=1 more-chunks / m=0 final). Empty or
    /// non-PNG input yields an empty result — callers skip emission.
    /// </summary>
    public static byte[] KittyPngInline(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.IsEmpty || !PngSize(pngBytes).HasValue)
        {
            return [];
        }

        string b64 = Convert.ToBase64String(pngBytes);
        var sb = new StringBuilder(b64.Length + ((b64.Length / KittyChunkChars) * 16) + 32);
        for (int offset = 0; offset < b64.Length; offset += KittyChunkChars)
        {
            int len = Math.Min(KittyChunkChars, b64.Length - offset);
            sb.Append(EscChar).Append("_Gf=100,a=T,m=");
            sb.Append(offset + len < b64.Length ? '1' : '0');
            sb.Append(';').Append(b64, offset, len);
            sb.Append(EscChar).Append('\\');
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Cell-sized twin of <see cref="KittyPngInline(ReadOnlySpan{byte})" />: the
    /// same chunked transfer, plus the destination box in cells (<c>c</c>/<c>r</c>)
    /// and <c>C=1</c> so the placement does not move the cursor out from under
    /// the frame's pen tracking. Empty / non-PNG input yields an empty result.
    /// </summary>
    public static byte[] KittyPngInline(ReadOnlySpan<byte> pngBytes, int cols, int rows)
    {
        if (pngBytes.IsEmpty || cols <= 0 || rows <= 0 || !PngSize(pngBytes).HasValue)
        {
            return [];
        }

        return BuildKittyPng(pngBytes, $"c={cols},r={rows},");
    }

    /// <summary>
    /// Cell-sized kitty transfer with an explicit image identity and stacking
    /// order (issue #2222): <c>i</c> lets the terminal address the placement
    /// later (<see cref="KittyDeleteImage" />) and <c>z</c> is the graphics
    /// z-index — larger values composite above smaller ones. Every chunk
    /// repeats both keys, as kitty requires for a multi-part transmission.
    /// Empty / non-PNG / degenerate input yields an empty result.
    /// </summary>
    public static byte[] KittyPngInline(ReadOnlySpan<byte> pngBytes, int cols, int rows, uint imageId, int zIndex)
    {
        if (pngBytes.IsEmpty || cols <= 0 || rows <= 0 || imageId == 0 || !PngSize(pngBytes).HasValue)
        {
            return [];
        }

        return BuildKittyPng(pngBytes, $"c={cols},r={rows},i={imageId},z={zIndex},");
    }

    /// <summary>Shared chunked-transfer builder: every chunk repeats the
    /// placement keys so a multi-part kitty transmission stays atomic.</summary>
    private static byte[] BuildKittyPng(ReadOnlySpan<byte> pngBytes, string box)
    {
        string b64 = Convert.ToBase64String(pngBytes);
        var sb = new StringBuilder(b64.Length + ((b64.Length / KittyChunkChars) * (box.Length + 16)) + 32);
        for (int offset = 0; offset < b64.Length; offset += KittyChunkChars)
        {
            int len = Math.Min(KittyChunkChars, b64.Length - offset);
            sb.Append(EscChar).Append("_Gf=100,a=T,C=1,").Append(box);
            sb.Append("m=");
            sb.Append(offset + len < b64.Length ? '1' : '0');
            sb.Append(';').Append(b64, offset, len);
            sb.Append(EscChar).Append('\\');
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Kitty delete-by-id: <c>a=d,d=I,i=&lt;id&gt;</c> removes every placement
    /// of that image (issue #2222). A zero id is a no-op so callers can emit
    /// unconditionally for a slot that never held an image.
    /// </summary>
    public static byte[] KittyDeleteImage(uint imageId) =>
        imageId == 0
            ? []
            : Encoding.ASCII.GetBytes($"{EscChar}_Ga=d,d=I,i={imageId}{EscChar}\\");

    /// <summary>
    /// Encodes 24-bit RGB bytes as a complete Sixel sequence (DCS header,
    /// raster attributes, palette definitions, body, ST terminator).
    ///
    /// Layout per 6-row band: every palette slot used anywhere in the band is
    /// printed as its own pass — '#&lt;slot&gt;' then RLE-compressed column
    /// masks ('!' repeat) with '?' no-op filler keeping columns aligned — and
    /// passes are separated by '$'; bands end with '-'. Bits inside a mask
    /// char map LSB→topmost row (DEC order).
    /// </summary>
    public static byte[] EncodeSixel(ReadOnlySpan<byte> rgb24, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width == 0 || height == 0 || (long)width * height * 3 != rgb24.Length)
        {
            throw new ArgumentException("rgb24 length must be width*height*3 with positive dimensions");
        }

        int bandCount = (height + SixelBandRows - 1) / SixelBandRows;

        var sb = new StringBuilder((bandCount * width * 3) + 128);
        sb.Append(EscChar).Append("Pq\"1;1;").Append(width).Append(';').Append(height);

        foreach (var p in SixelPalette)
        {
            sb.Append($"#{Array.IndexOf(SixelPalette, p)};2;");
            sb.Append((p[0] * 100) / 255).Append(';')
              .Append((p[1] * 100) / 255).Append(';')
              .Append((p[2] * 100) / 255);
        }
        sb.Append('\n');

        for (int band = 0; band < bandCount; band++)
        {
            var slotsUsed = SlotsUsedInBand(rgb24, width, height, band);
            bool firstPass = true;
            for (int slot = 0; slot < SixelPalette.Length; slot++)
            {
                if (!slotsUsed[slot])
                {
                    continue;
                }

                if (!firstPass)
                {
                    sb.Append('$'); // carriage-return before the next overlay pass
                }

                firstPass = false;
                sb.Append('#').Append(slot);
                AppendMaskRun(sb, rgb24, width, height, band, slot);
            }

            sb.Append('-'); // next band
        }

        sb.Append(EscChar).Append('\\');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static bool[] SlotsUsedInBand(ReadOnlySpan<byte> rgb24, int width, int height, int band)
    {
        var used = new bool[SixelPalette.Length];
        int yStart = band * SixelBandRows;
        int yEnd = Math.Min(height, yStart + SixelBandRows);
        for (int y = yStart; y < yEnd; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int off = ((y * width) + x) * 3;
                used[NearestSlot(rgb24[off], rgb24[off + 1], rgb24[off + 2])] = true;
            }
        }

        return used;
    }

    /// <summary>Emits one slot's masked band with '!'-run compression and '?' fillers.</summary>
    private static void AppendMaskRun(StringBuilder sb, ReadOnlySpan<byte> rgb24, int width, int height, int band, int slot)
    {
        int runLen = 0;
        char runVal = '?';

        void Flush()
        {
            switch (runLen)
            {
                case 0:
                    return;
                case 1:
                    sb.Append(runVal);
                    break;
                default:
                    sb.Append('!').Append(runLen).Append(runVal);
                    break;
            }

            runLen = 0;
        }

        for (int x = 0; x < width; x++)
        {
            int bits = 0;
            int yStart = band * SixelBandRows;
            int yEnd = Math.Min(height, yStart + SixelBandRows);
            for (int y = yStart; y < yEnd; y++)
            {
                int off = ((y * width) + x) * 3;
                if (NearestSlot(rgb24[off], rgb24[off + 1], rgb24[off + 2]) == slot)
                {
                    bits |= 1 << (y - yStart); // LSB = topmost row of the band
                }
            }

            char c = bits == 0 ? '?' : (char)(0x3F + bits);
            if (runLen > 0 && c != runVal)
            {
                Flush();
            }

            runVal = c;
            runLen++;
        }

        Flush();
    }

    /// <summary>Squared-distance nearest match against <see cref="SixelPalette" />.</summary>
    internal static int NearestSlot(byte r, byte g, byte b)
    {
        int best = 0;
        long bestDist = long.MaxValue;
        for (int i = 0; i < SixelPalette.Length; i++)
        {
            long dr = r - SixelPalette[i][0];
            long dg = g - SixelPalette[i][1];
            long db = b - SixelPalette[i][2];
            long dist = (dr * dr) + (dg * dg) + (db * db);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Minimal PNG → RGB24 decoder for the Sixel path (issue #2222): 8-bit,
    /// non-interlaced, colour type 2 (RGB) or 6 (RGBA), all five scanline
    /// filters. Anything else returns null and the caller keeps its text card.
    /// CRC is not verified — this feeds a renderer, not a trust boundary.
    /// </summary>
    internal static byte[]? PngToRgb24(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 33 || !png[..8].SequenceEqual(signature))
        {
            return null;
        }

        int width = 0;
        int height = 0;
        int bpp = 0;
        var idat = new List<byte>();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12L + length > png.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (length < 13)
                {
                    return null;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                int depth = data[8];
                int colour = data[9];
                int interlace = data[12];
                if (depth != 8 || interlace != 0 || (colour != 2 && colour != 6) || width <= 0 || height <= 0)
                {
                    return null;
                }

                bpp = colour == 2 ? 3 : 4;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.AddRange(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        if (width <= 0 || height <= 0 || bpp == 0 || idat.Count == 0)
        {
            return null;
        }

        byte[] inflated;
        try
        {
            using var input = new MemoryStream(idat.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            inflated = output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }

        int stride = width * bpp;
        if (inflated.Length < height * (stride + 1))
        {
            return null;
        }

        byte[] rgb = new byte[width * height * 3];
        byte[] current = new byte[stride];
        byte[] previous = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int source = y * (stride + 1);
            int filter = inflated[source];
            if (filter is < 0 or > 4)
            {
                return null;
            }

            for (int x = 0; x < stride; x++)
            {
                byte value = inflated[source + 1 + x];
                int left = x >= bpp ? current[x - bpp] : 0;
                int above = previous[x];
                int upperLeft = x >= bpp ? previous[x - bpp] : 0;
                current[x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + above),
                    3 => (byte)(value + ((left + above) >> 1)),
                    4 => (byte)(value + Paeth(left, above, upperLeft)),
                    _ => 0,
                };
            }

            int destination = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                rgb[destination] = current[x * bpp];
                rgb[destination + 1] = current[(x * bpp) + 1];
                rgb[destination + 2] = current[(x * bpp) + 2];
                destination += 3;
            }

            (current, previous) = (previous, current);
        }

        return rgb;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int p = left + above - upperLeft;
        int pa = Math.Abs(p - left);
        int pb = Math.Abs(p - above);
        int pc = Math.Abs(p - upperLeft);
        return pa <= pb && pa <= pc ? left : pb <= pc ? above : upperLeft;
    }
}
