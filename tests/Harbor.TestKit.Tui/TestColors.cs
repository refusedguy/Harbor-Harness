using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.TestKit;

/// <summary>
///     The packed colours the glow suites assert on.
/// </summary>
/// <remarks>
///     <para>
///         A glow blends a cell's foreground toward white, and the suites that
///         assert on the result had to derive that hot tone themselves. The
///         derivation is the product's — <c>GlowEffect.HotBurn</c> is its constant —
///         so it belongs with the engine types the test kit already reaches.
///     </para>
/// </remarks>
public static class TestColors
{
    /// <summary>The RGB channels of a packed colour, or black when it carries none.</summary>
    public static (byte R, byte G, byte B) Channels(EngineCells.PackedColor c) =>
        c.IsRgb ? c.RgbChannels : ((byte)0, (byte)0, (byte)0);

    /// <summary>The fixed burn <c>GlowEffect</c> applies toward white to derive its hot tone.</summary>
    public static EngineCells.PackedColor HotTone(EngineCells.PackedColor accent)
    {
        var (r, g, b) = Channels(accent);
        const double burn = 0.65; // GlowEffect.HotBurn
        return EngineCells.PackedColor.Rgb(
            (byte)(r + ((255 - r) * burn)),
            (byte)(g + ((255 - g) * burn)),
            (byte)(b + ((255 - b) * burn)));
    }
}
