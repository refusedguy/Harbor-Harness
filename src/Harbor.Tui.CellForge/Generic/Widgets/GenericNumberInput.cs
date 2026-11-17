using System.Globalization;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Direction of a single numeric step.</summary>
public enum NumberStepAction
{
    /// <summary>Decrease the value by <see cref="GenericNumberInput.Step"/>.</summary>
    Decrement,

    /// <summary>Increase the value by <see cref="GenericNumberInput.Step"/>.</summary>
    Increment,
}

/// <summary>
/// Generic numeric input over cells (gpui-kit <c>NumberInput</c>, issue #2363):
/// a <see cref="GenericTextField"/> carries the editable digits while this
/// primitive owns the numeric contract: min/max bounds, a fixed step, a
/// display formatter and Up/Down stepping. No chat, form or plugin coupling;
/// it is a neutral primitive future dialogs and forms compose from.
/// </summary>
public sealed class GenericNumberInput
{
    private readonly GenericTextField _field = new();

    /// <summary>Creates an input at <paramref name="value"/> with the given contract.</summary>
    public GenericNumberInput(
        double value = 0,
        double? min = null,
        double? max = null,
        double step = 1,
        Func<double, string>? formatter = null)
    {
        Step = step;
        Min = min;
        Max = max;
        Formatter = formatter;
        SetValue(value);
    }

    /// <summary>Inclusive lower bound; null means unbounded.</summary>
    public double? Min { get; set; }

    /// <summary>Inclusive upper bound; null means unbounded.</summary>
    public double? Max { get; set; }

    /// <summary>Fixed amount applied by one increment or decrement. Must be positive.</summary>
    public double Step { get; set; }

    /// <summary>
    /// Optional display formatter. It affects painting only: the backing field
    /// keeps a parseable invariant-culture number so stepping and <see cref="Value"/>
    /// stay independent of how the value is shown.
    /// </summary>
    public Func<double, string>? Formatter { get; set; }

    /// <summary>Whether the increment/decrement glyphs are painted.</summary>
    public bool ShowStepper { get; set; } = true;

    /// <summary>Field label painted by the host form; empty draws no label.</summary>
    public string Label
    {
        get => _field.Label;
        set => _field.Label = value;
    }

    /// <summary>Ghost hint painted when no value is present.</summary>
    public string Placeholder
    {
        get => _field.Placeholder;
        set => _field.Placeholder = value;
    }

    /// <summary>Whether the cursor row renders (host focus state).</summary>
    public bool IsFocused
    {
        get => _field.IsFocused;
        set => _field.IsFocused = value;
    }

    /// <summary>One-row desired height.</summary>
    public int DesiredHeight => 1;

    /// <summary>The raw, parseable field text.</summary>
    public string Text => _field.Text;

    /// <summary>UTF-16 cursor index into <see cref="Text"/>.</summary>
    public int Cursor => _field.Cursor;

    /// <summary>Parsed current value; unparseable text reads as zero.</summary>
    public double Value => Parse(Text) ?? 0;

    /// <summary>Replaces the value, clamped to <see cref="Min"/> and <see cref="Max"/>.</summary>
    public void SetValue(double value)
    {
        double bounded = Clamp(value);
        _field.SetText(bounded.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Applies one step. Returns false when the value cannot move.</summary>
    public bool ApplyStep(NumberStepAction action)
    {
        string? next = StepValue(Text, action, Step, Min, Max);
        if (next is null)
        {
            return false;
        }

        _field.SetText(next);
        return true;
    }

    /// <summary>Increments by <see cref="Step"/>; false when already at the maximum.</summary>
    public bool Increment() => ApplyStep(NumberStepAction.Increment);

    /// <summary>Decrements by <see cref="Step"/>; false when already at the minimum.</summary>
    public bool Decrement() => ApplyStep(NumberStepAction.Decrement);

    /// <summary>True when a step in <paramref name="action"/> would move the value.</summary>
    public bool CanStep(NumberStepAction action) => StepValue(Text, action, Step, Min, Max) is not null;

    /// <summary>
    /// Routes Up/Down to stepping and delegates editing keys to the backing
    /// text field. Enter/Escape/Tab stay with the host focus chain.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Up:
                return Increment();
            case KeyCode.Down:
                return Decrement();
            default:
                return _field.HandleKey(key);
        }
    }

    /// <summary>Paints the value and, when enabled, the step glyphs into one row.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int glyphWidth = ShowStepper && rect.Width >= 4 ? 2 : 0;
        int textWidth = rect.Width - glyphWidth;
        var textRect = new Rect(rect.X, rect.Y, textWidth, rect.Height);

        if (Formatter is null)
        {
            _field.Paint(buffer, textRect);
        }
        else
        {
            PaintFormatted(buffer, textRect);
        }

        if (glyphWidth == 0)
        {
            return;
        }

        // gpui-kit's default order: decrement at the left of the control pair,
        // increment at the right. Both sit at the right edge of the field.
        PaintGlyph(buffer, rect.X + textWidth, rect.Y, DecrementGlyph, CanStep(NumberStepAction.Decrement));
        PaintGlyph(buffer, rect.X + textWidth + 1, rect.Y, IncrementGlyph, CanStep(NumberStepAction.Increment));
    }

    /// <summary>Glyph painted for the decrement control.</summary>
    public const char DecrementGlyph = '▼';

    /// <summary>Glyph painted for the increment control.</summary>
    public const char IncrementGlyph = '▲';

    private static double? Parse(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private double Clamp(double value)
    {
        if (Min is { } min && value < min)
        {
            value = min;
        }

        if (Max is { } max && value > max)
        {
            value = max;
        }

        return value;
    }

    private void PaintFormatted(ScreenBuffer buffer, Rect rect)
    {
        string display = Formatter!(Value) ?? string.Empty;
        int width = Math.Min(display.Length, rect.Width);
        if (width > 0)
        {
            buffer.SetText(rect.X, rect.Y, display.AsSpan(0, width), new CellStyle(ChatPalette.Text));
        }

        if (!IsFocused || rect.Width <= 0)
        {
            return;
        }

        int cursorX = rect.X + Math.Max(0, Math.Min(display.Length, rect.Width - 1));
        if ((uint)cursorX >= (uint)buffer.Cols || (uint)rect.Y >= (uint)buffer.Rows)
        {
            return;
        }

        var style = new CellStyle(ChatPalette.Text, attrs: StyleAttr.Reverse);
        int rune = buffer.Get(cursorX, rect.Y).Rune;
        buffer.SetRune(cursorX, rect.Y, rune == 0 || rune == ' ' ? new Rune('_') : new Rune(rune), in style);
    }

    private static void PaintGlyph(ScreenBuffer buffer, int x, int y, char glyph, bool enabled)
    {
        if ((uint)x >= (uint)buffer.Cols || (uint)y >= (uint)buffer.Rows)
        {
            return;
        }

        var style = enabled
            ? new CellStyle(ChatPalette.Accent)
            : ChatPalette.Dim;
        buffer.SetText(x, y, new string(glyph, 1), style);
    }

    /// <summary>
    /// Steps a numeric string while preserving decimal precision and
    /// directional bounds. Port of gpui-kit's <c>step_value</c> behaviour:
    /// <c>0.1 + 0.2 = 0.3</c> (not <c>0.30000000000000004</c>), clamping to a
    /// bound keeps that bound's precision, and a value that cannot move in the
    /// requested direction yields null (so callers can refuse the key).
    /// </summary>
    public static string? StepValue(
        string value,
        NumberStepAction action,
        double step,
        double? min = null,
        double? max = null)
    {
        if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0)
        {
            return null;
        }

        double? current = Parse(value ?? string.Empty);
        double number = current ?? 0;
        double result = action == NumberStepAction.Increment ? number + step : number - step;
        int digits = Math.Max(FractionDigits(value ?? string.Empty), FractionDigits(step.ToString("R", CultureInfo.InvariantCulture)));

        if (min is { } lower && result < lower)
        {
            result = lower;
            digits = Math.Max(digits, FractionDigits(lower.ToString("R", CultureInfo.InvariantCulture)));
        }

        if (max is { } upper && result > upper)
        {
            result = upper;
            digits = Math.Max(digits, FractionDigits(upper.ToString("R", CultureInfo.InvariantCulture)));
        }

        if (current is { } before)
        {
            bool moved = action == NumberStepAction.Increment ? result > before : result < before;
            if (!moved)
            {
                return null;
            }
        }

        return result.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static int FractionDigits(string text)
    {
        int dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }
}
