using Harbor.Desktop.Animations;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// HDS v1 motion primitives for the CellForge renderer (widgets §3.x):
/// entrance fades/slides, the approval warn-glow pulse, and status-accent
/// crossfades. Every helper is a pure function of monotonic frame ticks —
/// no timers, no allocations (same contract as <see cref="SpinnerStrip" />).
///
/// Durations come from <see cref="AnimationTokens" /> (Fast=150 ms micro,
/// Normal=300 ms standard) converted to frames at the display cadence of
/// 60 fps; easing curves mirror EasingEaseOut/EasingEaseIn/EasingCubicInOut.
/// </summary>
public static class PanelFx
{
    /// <summary>Display-cadence assumption used to convert ms tokens to ticks.</summary>
    internal const double MsPerFrame = 1000d / 60;

    /// <summary>Micro duration (HDS v1) — 150 ms.</summary>
    public const int FadeMs = AnimationTokens.FastMs;

    /// <summary>Standard duration (HDS v1) — 300 ms.</summary>
    public const int SlideMs = AnimationTokens.NormalMs;

    /// <summary>Fade length in frames at 60 fps (9).</summary>
    public static readonly int FadeFrames = Frames(FadeMs);

    /// <summary>Slide length in frames at 60 fps (18).</summary>
    public static readonly int SlideFrames = Frames(SlideMs);

    /// <summary>Rows a block slides up while entering (kept small — cell grid).</summary>
    public const int SlideMaxRows = 2;

    /// <summary>Approval warn-glow cycle length in frames (≈500 ms round trip).</summary>
    public static readonly int PulseFrames = 2 * Frames(AnimationTokens.NormalMs);

    private static readonly (byte Index, byte R, byte G, byte B)[] PaletteRgb =
    [
        (1, 0xFF, 0x6B, 0x6B), // error
        (2, 0x7F, 0xD9, 0x62), // success
        (3, 0xFF, 0xB4, 0x54), // warning
        (4, 0x39, 0xBA, 0xE6), // accent
        (5, 0xD2, 0xA6, 0xFF), // tool
        (6, 0xF2, 0x96, 0x68), // system
    ];

    private static int Frames(int ms) => Math.Max(1, (int)Math.Round(ms / MsPerFrame));

    /// <summary>EasingEaseOut — cubic ease-out, mirrors the HDS curve.</summary>
    public static double EaseOut(double t)
    {
        double c = Math.Clamp(t, 0.0, 1.0);
        return 1.0 - ((1.0 - c) * (1.0 - c) * (1.0 - c));
    }

    /// <summary>EasingEaseIn — cubic ease-in.</summary>
    public static double EaseIn(double t)
    {
        double c = Math.Clamp(t, 0.0, 1.0);
        return c * c * c;
    }

    /// <summary>
    /// Settled-or-animating progress in [0..1] (ease-out applied). An elapsed
    /// time ≤ 0 — the first frame a block is seen, or any tick captured before
    /// its append marker — resolves to 1 so single-frame renders stay settled.
    /// </summary>
    public static double Progress(long startTick, long nowTick, int durationFrames)
    {
        if (durationFrames <= 0 || nowTick <= startTick)
        {
            return 1.0;
        }

        return EaseOut((double)(nowTick - startTick) / durationFrames);
    }

    /// <summary>
    /// Approval warn-glow amount in [0..1] — a sine round-trip over
    /// <see cref="PulseFrames" />; 0 until <paramref name="birthTick" />.
    /// </summary>
    public static double WarnPulse(long birthTick, long nowTick)
    {
        if (birthTick < 0 || nowTick <= birthTick)
        {
            return 0.0;
        }

        long phase = (nowTick - birthTick) % PulseFrames;
        double w = Math.Sin((phase / (double)PulseFrames) * (Math.PI * 2));
        return Math.Max(0.0, w);
    }

    /// <summary>Linear RGB channel interpolation (mirrors ColorTransition.Interpolate).</summary>
    public static PackedColor Lerp(PackedColor from, PackedColor to, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        var a = RgbChannelsOf(from);
        var b = RgbChannelsOf(to);
        return PackedColor.Rgb(
            Step(a.R, b.R, clamped),
            Step(a.G, b.G, clamped),
            Step(a.B, b.B, clamped));

        static byte Step(byte x, byte y, double k) => (byte)(x + (y - x) * k);
    }

    /// <summary>
    /// Alpha-blended copy of a cell style for entrance fades: foreground and
    /// background both ease in from the panel surface. Styles pass through
    /// unchanged at α ≥ 1 (bit-identical, keeping settled frames diff-free).
    /// </summary>
    public static CellStyle WithAlpha(CellStyle style, double alpha)
    {
        if (alpha >= 1.0 || style.IsPlain)
        {
            return style;
        }

        double a = Math.Clamp(alpha, 0.0, 1.0);
        var surface = ChatPalette.Panel;
        var fg = Lerp(surface, style.Fg, a);
        var bg = style.Bg.IsDefault ? style.Bg : Lerp(surface, style.Bg, a);
        return new CellStyle(fg, bg, style.Attrs);
    }

    /// <summary>Status-accent crossfade ramp in [0..1] since the mode flip.</summary>
    public static double AccentRamp(long flippedTick, long nowTick) =>
        Progress(flippedTick, nowTick, FadeFrames);

    /// <summary>
    /// Alpha-blends an already-painted buffer region toward the panel surface
    /// (entrance fades / status crossfades). Bounded callers only — runs per
    /// cell during transition frames and is skipped entirely once settled.
    /// </summary>
    public static void BlendRegion(ScreenBuffer buffer, Rect region, double alpha)
    {
        for (int y = region.Y; y < region.Bottom; y++)
        {
            for (int x = region.X; x < region.Right; x++)
            {
                var faded = WithAlpha(buffer.Get(x, y).Style, alpha);
                buffer.SetStyleAt(x, y, in faded);
            }
        }
    }

    private static (byte R, byte G, byte B) RgbChannelsOf(PackedColor color)
    {
        if (color.IsRgb)
        {
            return color.RgbChannels;
        }

        byte index = color.Index;
        for (int i = 0; i < PaletteRgb.Length; i++)
        {
            if (PaletteRgb[i].Index == index)
            {
                return (PaletteRgb[i].R, PaletteRgb[i].G, PaletteRgb[i].B);
            }
        }

        return ChatPalette.Text.RgbChannels;
    }
}
