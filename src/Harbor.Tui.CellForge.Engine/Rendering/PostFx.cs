namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// One frame's glow source, published by widgets during paint (renderer-moat
/// T3): the screen-space region, the accent color the surface painted this
/// frame (captured, never re-derived — no drift), and the effect intensity
/// in [0..1]. Warning/error surfaces only by publisher contract (pending
/// approval gates, error banners).
/// </summary>
public readonly struct GlowRegion
{
    public GlowRegion(Rect bounds, PackedColor accent, double intensity)
    {
        Bounds = bounds;
        Accent = accent;
        Intensity = double.Clamp(intensity, 0.0, 1.0);
    }

    /// <summary>Screen-space region the glow applies to.</summary>
    public Rect Bounds { get; }

    /// <summary>Accent color painted this frame — cells whose foreground
    /// matches exactly are lifted toward the hot glow tone.</summary>
    public PackedColor Accent { get; }

    /// <summary>Effect strength this frame (0 = no glow — identity transform).</summary>
    public double Intensity { get; }
}

/// <summary>
/// Shader-like post-render stage (renderer-moat T3): transforms cells AFTER
/// the diff selected them and right BEFORE the SGR automaton encodes them.
/// Implementations must be pure functions of (position, cell, internal
/// per-frame state) and allocation-free.
/// </summary>
public interface IPostEffect
{
    /// <summary>Screen-space region the effect applies to (callers clip by it).</summary>
    Rect Region { get; }

    /// <summary>Transforms one cell. Identity results cost nothing downstream
    /// (the SGR automaton emits no delta for an unchanged style).</summary>
    Cell Transform(int x, int y, in Cell cell);
}

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

    private Rect _region;
    private PackedColor _accent;
    private PackedColor _hot;
    private double _intensity;

    public Rect Region => _region;

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
            _hot = PackedColor.Rgb(Burn(r), Burn(g), Burn(b));
        }
        else
        {
            // Palette-index accents don't glow (publisher contract: accents
            // are truecolor ChatPalette projections).
            _hot = _accent;
        }

        static byte Burn(byte channel) => (byte)(channel + ((255 - channel) * HotBurn));
    }

    public Cell Transform(int x, int y, in Cell cell)
    {
        if (_intensity <= 0.0
            || !cell.Style.Fg.IsRgb
            || cell.Style.Fg != _accent
            || !_region.Contains(x, y))
        {
            return cell;
        }

        var style = cell.Style;
        var glow = PanelFx.Lerp(style.Fg, _hot, _intensity * PeakStrength);
        return Cell.FromRaw(cell.Rune, glow.Value, cell.Bg, cell.Flags, cell.Width);
    }
}

/// <summary>
/// Composable effect pipeline (renderer-moat T3): a fixed slot table of
/// post-render stages applied in slot order after the diff selects a cell
/// and before the ANSI write. The empty pipeline (the steady state on
/// non-effect frames) short-circuits to a single null/zero check — zero
/// perf regression, byte-identical output. All state lives in the fixed
/// slot array; the steady path allocates nothing.
/// </summary>
public sealed class PostFxPipeline
{
    public const int MaxEffects = 8;

    private readonly IPostEffect?[] _slots = new IPostEffect?[MaxEffects];
    private int _count;

    /// <summary>Number of armed effects (0 → the pipeline is a no-op).</summary>
    public int Count => _count;

    /// <summary>Installs (or removes with null) an effect in one slot. The
    /// same effect instance is refreshed per frame via its own Update-like
    /// method — arming the pipeline never allocates.</summary>
    public void Set(int slot, IPostEffect? effect)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, MaxEffects);
        _slots[slot] = effect;
        Recount();
    }

    /// <summary>Disarms every effect.</summary>
    public void Clear()
    {
        Array.Clear(_slots, 0, _slots.Length);
        _count = 0;
    }

    /// <summary>Runs <paramref name="cell"/> through every armed effect whose
    /// region contains the position, in slot order.</summary>
    public Cell Transform(int x, int y, in Cell cell)
    {
        var current = cell;
        for (int i = 0; i < _slots.Length; i++)
        {
            var effect = _slots[i];
            if (effect is null || !effect.Region.Contains(x, y))
            {
                continue;
            }

            current = effect.Transform(x, y, in current);
        }

        return current;
    }

    private void Recount()
    {
        int count = 0;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is not null)
            {
                count++;
            }
        }

        _count = count;
    }
}
