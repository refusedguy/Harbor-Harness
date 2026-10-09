// #436: moved from Harbor.Tui.CellForge.Engine/Rendering/PostFx.cs — the chat-owned glow effect.
// The pipeline (PostFxPipeline), its contract (IPostEffect) and GlowRegion stay engine-side;
// only the PanelFx-coupled effect moves. It implements the engine contract,
// so its grid types are engine-qualified; only PanelFx stays Rendering-bound.
using Harbor.Ui.Framework.Rendering.Widgets;
// #436: this effect implements the ENGINE post-effect contract, so its grid
// types are engine-qualified explicitly (the assembly-wide aliases pin them
// to the Rendering vocabulary). Only PanelFx stays Rendering-bound.
using EngineCells = Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Rendering;

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
        return EngineCells.Cell.FromRaw(cell.Rune, glow.Value, cell.Bg.Value, (ushort)cell.Flags, cell.Width);
    }
}
