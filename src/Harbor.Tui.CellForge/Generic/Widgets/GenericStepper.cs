using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Position of one item relative to the selected step.</summary>
public enum StepStatus
{
    /// <summary>A step before the current one.</summary>
    Completed,

    /// <summary>The selected step.</summary>
    Current,

    /// <summary>A step after the current one.</summary>
    Upcoming,
}

/// <summary>
/// External state of a <see cref="GenericStepper"/>: the selected step and the
/// number of steps. State lives with the host; the stepper never mutates it.
/// </summary>
/// <param name="Current">Zero-based selected step; negative or out-of-range reads as no selection.</param>
/// <param name="Count">Number of steps.</param>
public readonly record struct GenericStepperState(int Current, int Count)
{
    /// <summary>Clamps a requested step into <c>[0, Count - 1]</c>; empty steppers yield 0.</summary>
    public int ClampedCurrent => Count <= 0 ? 0 : Math.Clamp(Current, 0, Count - 1);

    /// <summary>True when a step at <paramref name="index"/> exists and is selected.</summary>
    public bool IsCurrent(int index) => Count > 0 && index == Current;

    /// <summary>Returns a copy with the step moved by <paramref name="delta"/>, clamped.</summary>
    public GenericStepperState Move(int delta) => Select(ClampedCurrent + delta);

    /// <summary>Returns a copy with the step set to <paramref name="index"/>, clamped.</summary>
    public GenericStepperState Select(int index) => this with
    {
        Current = Count <= 0 ? 0 : Math.Clamp(index, 0, Count - 1),
    };
}

/// <summary>
/// Generic segmented step-by-step progress over cells (gpui-kit
/// <c>Stepper</c>, issue #2363). It paints a marker per step plus connectors,
/// with the status of each segment derived from an externally owned
/// <see cref="GenericStepperState"/>. Unlike a progress bar it carries no
/// ratio and no completion logic. "What step am I on" belongs to the host.
/// </summary>
public static class GenericStepper
{
    /// <summary>Marker for a completed step.</summary>
    public const char CompletedGlyph = '●';

    /// <summary>Marker for the selected step.</summary>
    public const char CurrentGlyph = '◉';

    /// <summary>Marker for a step that has not been reached.</summary>
    public const char UpcomingGlyph = '○';

    /// <summary>Connector painted between two markers.</summary>
    public const char ConnectorGlyph = '─';

    /// <summary>Status of the step at <paramref name="index"/> for <paramref name="state"/>.</summary>
    public static StepStatus StatusAt(int index, GenericStepperState state)
    {
        if (state.Count <= 0 || (uint)index >= (uint)state.Count)
        {
            return StepStatus.Upcoming;
        }

        if (index < state.Current)
        {
            return StepStatus.Completed;
        }

        return index == state.Current ? StepStatus.Current : StepStatus.Upcoming;
    }

    /// <summary>Marker glyph for a step's status.</summary>
    public static char GlyphFor(StepStatus status) => status switch
    {
        StepStatus.Completed => CompletedGlyph,
        StepStatus.Current => CurrentGlyph,
        _ => UpcomingGlyph,
    };

    /// <summary>
    /// Paints one row of markers and connectors into the first row of
    /// <paramref name="rect"/>. Each marker occupies one cell; a connector
    /// fills the cell between adjacent markers. Labels are not painted.
    /// </summary>
    public static void Paint(ScreenBuffer buffer, Rect rect, GenericStepperState state)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || state.Count <= 0)
        {
            return;
        }

        var completedStyle = new CellStyle(ChatPalette.Success);
        var currentStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var upcomingStyle = ChatPalette.Dim;
        var connectorStyle = new CellStyle(ChatPalette.Muted);

        int x = rect.X;
        for (int i = 0; i < state.Count && x < rect.X + rect.Width; i++)
        {
            StepStatus status = StatusAt(i, state);
            CellStyle style = status switch
            {
                StepStatus.Completed => completedStyle,
                StepStatus.Current => currentStyle,
                _ => upcomingStyle,
            };
            buffer.SetText(x, rect.Y, new string(GlyphFor(status), 1), style);
            x++;

            bool isLast = i + 1 == state.Count;
            if (!isLast && x < rect.X + rect.Width)
            {
                buffer.SetText(x, rect.Y, new string(ConnectorGlyph, 1), connectorStyle);
                x++;
            }
        }
    }
}
