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
///   scaled to fit it).</description></item>
///   <item><description>OSC 1337 — <c>width</c>/<c>height</c> in cell units
///   (<c>N</c> with no suffix means character cells per the iTerm2 spec).</description></item>
/// </list>
/// </para>
/// </summary>
public static class InlineImageEncoder
{
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
    public static byte[]? Encode(InlineImageKind kind, string name, ReadOnlySpan<byte> data, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return null;
        }

        return kind switch
        {
            InlineImageKind.KittyApc => Graphics.KittyPngInline(data, cols, rows),
            InlineImageKind.Osc1337 => Osc1337Image.Encode(name, data, cols, rows),
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="kind"/> can carry <paramref name="mimeType"/>:
    /// kitty speaks PNG only (f=100), OSC 1337 rides any format the terminal
    /// sniffs. A block consults this before it spends a scale computation on a
    /// payload that the encoder would drop anyway.
    /// </summary>
    public static bool Supports(InlineImageKind kind, string mimeType)
    {
        if (kind == InlineImageKind.Osc1337)
        {
            return true;
        }

        return kind == InlineImageKind.KittyApc
            && mimeType.EndsWith("png", StringComparison.OrdinalIgnoreCase);
    }
}
