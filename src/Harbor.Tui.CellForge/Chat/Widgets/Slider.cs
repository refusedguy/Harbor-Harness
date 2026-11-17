using System.Globalization;
using Harbor.Ui.Framework.State;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// State of a cell-native range input (#2366). The value is owned by the
/// caller: every key press folds into a new <see cref="SliderState" /> that the
/// owner stores, so the widget itself keeps no mutable state and adds no input
/// channel. <see cref="Min" />, <see cref="Max" /> and <see cref="Step" /> are
/// the only policy; <see cref="Formatter" /> is the readable current result.
/// </summary>
/// <param name="Value">External current value; clamped and snapped for display.</param>
/// <param name="Min">Lower bound. A NaN value falls back to it.</param>
/// <param name="Max">Upper bound. A bound below <paramref name="Min" /> is treated as <paramref name="Min" />.</param>
/// <param name="Step">Positive increment; a non-positive or non-finite step disables snapping.</param>
/// <param name="Formatter">Optional value-to-text; null uses an invariant number.</param>
public readonly record struct SliderState(
    double Value,
    double Min = 0,
    double Max = 100,
    double Step = 1,
    Func<double, string>? Formatter = null);

/// <summary>
/// Keyboard range input on cells (gpui-kit Slider behaviors, #2366): arrows
/// step the value, Home/End jump to the bounds, and the track is the existing
/// <see cref="GaugeBar" /> render. Mouse drag, Rust/GPUI code and event wiring
/// are deliberately out of scope. The owner supplies the value and decides
/// where the folded result goes. Pure and AOT-safe: no reflection, no capture
/// beyond the optional formatter, no allocation on the key path.
/// </summary>
public static class Slider
{
    /// <summary>Whether <paramref name="state" /> has a usable step granularity.</summary>
    private static bool HasStep(SliderState state) =>
        state.Step > 0 && double.IsFinite(state.Step);

    /// <summary>
    /// Clamps <paramref name="value" /> to [Min, Max] and snaps it to the
    /// nearest Min + n × Step grid point when a usable step is declared.
    /// The upper bound always wins over the grid, so a Max off-grid is exact.
    /// </summary>
    public static double Clamp(SliderState state, double value)
    {
        double lo = state.Min;
        double hi = Math.Max(state.Min, state.Max);

        if (double.IsNaN(value) || value <= lo)
        {
            return lo;
        }

        if (value >= hi || !double.IsFinite(lo) || !double.IsFinite(hi) || !HasStep(state))
        {
            return value >= hi ? hi : value;
        }

        double snapped = lo + Math.Round((value - lo) / state.Step, MidpointRounding.AwayFromZero) * state.Step;
        return double.IsFinite(snapped) ? Math.Clamp(snapped, lo, hi) : value;
    }

    /// <summary>The displayed value: <see cref="SliderState.Value" /> clamped and snapped.</summary>
    public static double Normalize(SliderState state) => Clamp(state, state.Value);

    /// <summary>Fill ratio in [0,1]; a degenerate or non-finite span reads as empty.</summary>
    public static double Ratio(SliderState state)
    {
        double lo = state.Min;
        double hi = Math.Max(state.Min, state.Max);
        double span = hi - lo;
        if (span <= 0 || !double.IsFinite(span))
        {
            return 0;
        }

        return GaugeBar.ClampRatio((Normalize(state) - lo) / span);
    }

    /// <summary>Next value one step above the displayed value, never past Max.</summary>
    public static double StepUp(SliderState state) =>
        HasStep(state) ? Clamp(state, Normalize(state) + state.Step) : Normalize(state);

    /// <summary>Next value one step below the displayed value, never below Min.</summary>
    public static double StepDown(SliderState state) =>
        HasStep(state) ? Clamp(state, Normalize(state) - state.Step) : Normalize(state);

    /// <summary>Readable current result: the formatter, or an invariant number.</summary>
    public static string Format(SliderState state)
    {
        double value = Normalize(state);
        return state.Formatter?.Invoke(value)
            ?? value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Folds an existing <see cref="UiKey" /> into a new state. Left/Down and
    /// Right/Up step, Home/End jump to the bounds. Every other key, and any
    /// host command chord (Ctrl/Alt), is left untouched for the outer router.
    /// </summary>
    /// <param name="key">The key press from the existing input pipeline.</param>
    /// <param name="state">The externally owned slider state.</param>
    /// <param name="next">The folded state, or default when the key was not consumed.</param>
    /// <returns>True when the key was consumed.</returns>
    public static bool TryHandleKey(UiKey key, SliderState state, out SliderState next)
    {
        next = default;
        if (key.Mods.IsCommandModifier())
        {
            return false;
        }

        double? value = key.Code switch
        {
            UiKeyCode.Right or UiKeyCode.Up => StepUp(state),
            UiKeyCode.Left or UiKeyCode.Down => StepDown(state),
            UiKeyCode.Home => state.Min,
            UiKeyCode.End => Math.Max(state.Min, state.Max),
            _ => null,
        };

        if (value is null)
        {
            return false;
        }

        next = state with { Value = value.Value };
        return true;
    }

    /// <summary>
    /// Paints the track through <see cref="GaugeBar.Paint" /> with the
    /// formatted value on top. Degenerate rects are a no-op, as in GaugeBar.
    /// </summary>
    public static void Paint(
        ScreenBuffer buffer,
        Rect rect,
        SliderState state,
        CellStyle fill = default,
        CellStyle track = default,
        CellStyle labelOnFill = default,
        CellStyle labelOnTrack = default)
    {
        Ensure.NotNull(buffer);
        GaugeBar.Paint(
            buffer,
            rect,
            new GaugeState(Ratio(state), Format(state)),
            fill,
            track,
            labelOnFill,
            labelOnTrack);
    }
}
