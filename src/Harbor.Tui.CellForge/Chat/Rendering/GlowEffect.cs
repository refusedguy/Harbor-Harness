// #436: chat-owned glow effect, moved out of the engine grid sources.
// The effect pipeline with its contract and the glow region stay
// engine-side. Only the panel-coupled effect moves here. It implements
// the engine contract, so its grid types are engine-qualified below.
// Only PanelFx stays Rendering-bound.
using Harbor.Ui.Framework.Rendering.Widgets;
// #436: this effect implements the ENGINE post-effect contract, so its grid
// types are engine-qualified explicitly (the assembly-wide aliases pin them
// to the Rendering vocabulary). Only PanelFx stays Rendering-bound.
using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// TachyonFX-style bloom/glow for warning/error states only: cells whose
/// foreground matches the published accent (the warning/error tone the
/// surface actually painted) blend toward a fixed "hot" tone — the accent
/// burned toward white — proportional to the frame intensity. Every other
/// cell in the region passes through untouched, so the bloom reads as a
/// halo around the warning text, not a panel-wide wash.
/// </summary>
public sealed class GlowEffect : IPostEffect
{
    /// <summary>Blend weight toward the hot tone at intensity 1 (pulse peak).</summary>
    public const double PeakStrength = 0.55;

    /// <summary>Fraction of the way to white the hot tone sits (fixed burn).</summary>
    private const double HotBurn = 0.65;

    private EngineCells.Rect _region;
    private EngineCells.PackedColor _accent;
    private EngineCells.PackedColor _hot;
    private double _intensity;

    public EngineCells.Rect Region => _region;

    /// <summary>
    /// Per-frame refresh — zero-alloc. The hot tone is derived once per frame
    /// from the accent (accent lerped <see cref="HotBurn"/> toward white), so
    /// the transform itself stays two integer compares plus one color lerp.
    /// </summary>
    public void Update(in GlowRegion region)
    {
        _region = region.Bounds;
        _accent = region.Accent;
        _intensity = region.Intensity;

        if (_accent.IsRgb)
        {
            var (r, g, b) = _accent.RgbChannels;
            _hot = EngineCells.PackedColor.Rgb(Burn(r), Burn(g), Burn(b));
        }
        else
        {
            // Palette-index accents don't glow (publisher contract: accents
            // are truecolor ChatPalette projections).
            _hot = _accent;
        }

        static byte Burn(byte channel) => (byte)(channel + ((255 - channel) * HotBurn));
    }

    public EngineCells.Cell Transform(int x, int y, in EngineCells.Cell cell)
    {
        if (_intensity <= 0.0
            || !cell.Style.Fg.IsRgb
            || cell.Style.Fg != _accent
            || !_region.Contains(x, y))
        {
            return cell;
        }

        // The lerp is renderer-side (PanelFx); both colors round-trip through
        // the packed uint exactly, so the blend is bit-identical either way.
        var style = cell.Style;
        var glow = PanelFx.Lerp(
            Harbor.Ui.Framework.Rendering.PackedColor.FromRaw(style.Fg.Value),
            Harbor.Ui.Framework.Rendering.PackedColor.FromRaw(_hot.Value),
            _intensity * PeakStrength);
        return EngineCells.Cell.FromRaw(cell.Rune, glow.Value, cell.Bg, cell.Flags, cell.Width);
    }

    // ── tab-marker settle (#1173, opencode steal) ────────────────────────────
    //
    // opencode's tab-pulse is a per-frame Renderable animation (running sweep
    // + unread glow + completion flash, shaped by smootherstep envelopes).
    // That clock does not exist on a cell-diff surface — there is no
    // requestRender loop, only frames the store produces — so the sweep is
    // deliberately NOT ported. What survives is its steady state: a static
    // unread marker at full intensity while unread, plus a short settle fade
    // when the marker clears (a frame-counted smootherstep drain, no clock).
    // The panel owns the frame counting; these are the pure shapes.

    /// <summary>
    ///     The smootherstep shaping curve opencode's pulse envelopes are built
    ///     on, kept for the settle drain below (not the sweep — see above).
    /// </summary>
    public static double Smootherstep(double value)
    {
        double t = Math.Clamp(value, 0.0, 1.0);
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    /// <summary>
    ///     Settle level of a just-cleared tab marker, <c>1 → 0</c> over
    ///     <paramref name="fadeTicks" /> painted frames (#1173). The panel
    ///     counts the frames; this only shapes them, so the fade survives
    ///     without a Renderable clock.
    /// </summary>
    /// <param name="ticksLeft">Frames remaining (inclusive): full fade length at clear time, 0 when done.</param>
    /// <param name="fadeTicks">Total fade length in frames. Non-positive reads as already settled.</param>
    /// <returns>Marker intensity in <c>[0 .. 1]</c>: 1 while <paramref name="ticksLeft" /> covers the whole fade, 0 at 0.</returns>
    public static double TabMarkerFade(int ticksLeft, int fadeTicks) =>
        fadeTicks <= 0 || ticksLeft <= 0
            ? 0.0
            : 1.0 - Smootherstep(1.0 - Math.Clamp((double)ticksLeft / fadeTicks, 0.0, 1.0));
}
