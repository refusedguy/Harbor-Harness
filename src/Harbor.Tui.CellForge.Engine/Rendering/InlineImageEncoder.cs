using Harbor.Tui.CellForge.Capabilities;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Protocol dispatch for a *scaled* inline image (KILLER_FEATURES §2.7
/// Feature 12): picks the encoder for the probed
/// <see cref="Capabilities.InlineImageKind" /> and stamps the destination cell
/// box into the envelope, so the terminal stretches the bitmap into the block
/// rect the timeline measured instead of drawing it at natural size.
///
/// Pure byte formatter — nothing here touches Console. Callers hand the result
/// to the frame's <see cref="AnsiWriter" /> (lesson from
/// <c>AnsiTui</c>'s <c>Ansi</c> class): the image rides INSIDE the composed
/// frame byte span, so it leaves through the same single backend write as the
/// cell diff and never bypasses the engine.
///
/// <para>Protocol notes (verified against the upstream specs):
/// <list type="bullet">
///   <item><description>kitty — <c>a=T</c> transmit-and-display at the cursor,
///   <c>C=1</c> suppresses the cursor move the placement would otherwise
///   cause, <c>c</c>/<c>r</c> give the destination box in cells (the image is
///   scaled to fit it). <c>i</c>/<c>z</c> add identity and stacking order
///   (issue #2222).</description></item>
///   <item><description>OSC 1337 — <c>width</c>/<c>height</c> in cell units
///   (<c>N</c> with no suffix means character cells per the iTerm2 spec).</description></item>
///   <item><description>Sixel — no cell box exists in the protocol, so the
///   decoder keeps the PNG's pixel geometry; the PNG is quantized against the
///   fixed palette by <see cref="Graphics.EncodeSixel" />.</description></item>
/// </list>
/// </para>
/// </summary>
public static class InlineImageEncoder
{
    /// <summary>Pixel-area ceiling for one Sixel transmission — keeps a
    /// hostile attachment from turning a frame into megabytes of raster
    /// passes. Larger images keep the text card.</summary>
    public const int MaxSixelPixels = 1_000_000;

    /// <summary>
    /// Encodes <paramref name="data"/> for placement into a
    /// <paramref name="cols"/>×<paramref name="rows"/> cell box. Returns
    /// <c>null</c> when no protocol applies, the payload is empty/oversize, or
    /// the destination box is degenerate — callers then keep the text card.
    /// </summary>
    /// <param name="kind">Protocol from <see cref="Capabilities.InlineImageProbe.Detect" />.</param>
    /// <param name="name">File name shown by the terminal (sanitized by the encoder).</param>
    /// <param name="data">Raw PNG/JPEG bytes — kitty sniffs PNG only, OSC 1337 sniffs the format.</param>
    /// <param name="cols">Destination width in cells (must be ≥ 1).</param>
    /// <param name="rows">Destination height in cells (must be ≥ 1).</param>
    public static byte[]? Encode(InlineImageKind kind, string name, ReadOnlySpan<byte> data, int cols, int rows) =>
        Encode(kind, name, data, cols, rows, 0, 0);

    /// <summary>
    /// Identity-aware twin of
    /// <see cref="Encode(InlineImageKind, string, ReadOnlySpan{byte}, int, int)" />:
    /// a non-zero <paramref name="imageId"/> makes the kitty placement
    /// addressable for delete, and <paramref name="zIndex"/> orders it against
    /// other graphics. Zero id keeps the classic anonymous envelope.
    /// </summary>
    public static byte[]? Encode(
        InlineImageKind kind,
        string name,
        ReadOnlySpan<byte> data,
        int cols,
        int rows,
        uint imageId,
        int zIndex)
    {
        if (cols <= 0 || rows <= 0)
        {
            return null;
        }

        return kind switch
        {
            InlineImageKind.KittyApc => imageId == 0
                ? Graphics.KittyPngInline(data, cols, rows)
                : Graphics.KittyPngInline(data, cols, rows, imageId, zIndex),
            InlineImageKind.Osc1337 => Osc1337Image.Encode(name, data, cols, rows),
            InlineImageKind.Sixel => EncodeSixel(data),
            _ => null,
        };
    }

    /// <summary>Wires the standalone Sixel encoder into the protocol dispatch:
    /// decode the PNG to RGB24, then quantize it into a DCS q sequence.
    /// Unsupported PNG variants and oversized bitmaps return null so the
    /// caller falls back to its text card.</summary>
    public static byte[]? EncodeSixel(ReadOnlySpan<byte> png)
    {
        if (Graphics.PngSize(png) is not { } size
            || (long)size.Width * size.Height > MaxSixelPixels)
        {
            return null;
        }

        byte[]? rgb = Graphics.PngToRgb24(png);
        return rgb is null ? null : Graphics.EncodeSixel(rgb, size.Width, size.Height);
    }

    /// <summary>
    /// True when <paramref name="kind"/> can carry <paramref name="mimeType"/>:
    /// kitty and Sixel speak PNG (the latter after an RGB24 decode), OSC 1337
    /// rides any format the terminal sniffs. A block consults this before it
    /// spends a scale computation on a payload that the encoder would drop
    /// anyway.
    /// </summary>
    public static bool Supports(InlineImageKind kind, string mimeType)
    {
        if (kind == InlineImageKind.Osc1337)
        {
            return true;
        }

        if (kind is InlineImageKind.KittyApc or InlineImageKind.Sixel)
        {
            return mimeType.EndsWith("png", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
