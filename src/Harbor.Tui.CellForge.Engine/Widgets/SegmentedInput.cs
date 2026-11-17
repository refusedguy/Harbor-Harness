// #2364: segmented input primitive (gpui-kit OtpInput + TimeField, contract only).
// One primitive covers both: a list of segments, an active index, auto-advance on
// fill, and paste distribution across segments. OTP and time are configurations,
// not separate widgets. State lives OUTSIDE the engine in an immutable
// SegmentedInputState record; every edit returns a new state, so the host owns
// undo/render/serialization. BCL-only and AOT-safe like the other engine widgets;
// no cross-assembly imports, no extension axis (#555).

using System.Globalization;
using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>Editing rules for one segment of a <see cref="SegmentedInput"/>.</summary>
/// <param name="Capacity">Maximum number of characters the segment holds.</param>
/// <param name="IsAllowed">Whether a character may be entered into the segment.</param>
/// <param name="IsValid">Optional value-level check (range, format, ...).</param>
/// <param name="Normalize">Optional canonicalizer applied after every edit (e.g. a to AM).</param>
/// <param name="Required">Whether the segment must be filled for the input to be complete.</param>
public sealed record SegmentSpec(
    int Capacity,
    Func<char, bool> IsAllowed,
    Func<string, bool>? IsValid = null,
    Func<string, string>? Normalize = null,
    bool Required = true)
{
    /// <summary>One decimal digit per segment (the OTP case).</summary>
    public static SegmentSpec Digits(int capacity = 1) => new(
        capacity,
        static c => c is >= '0' and <= '9',
        v => v.Length == capacity && IsAllDigits(v));

    /// <summary>A decimal number constrained to the inclusive range [min, max].</summary>
    public static SegmentSpec Range(int min, int max, int capacity = 2) => new(
        capacity,
        static c => c is >= '0' and <= '9',
        v => v.Length == capacity
            && IsAllDigits(v)
            && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n >= min
            && n <= max);

    /// <summary>AM/PM selector: typing a/p is canonicalized to AM/PM.</summary>
    public static SegmentSpec Meridiem() => new(
        2,
        static c => c is 'a' or 'A' or 'p' or 'P',
        static v => v is "AM" or "PM",
        static v => v.Length == 0
            ? v
            : (char.ToLowerInvariant(v[0]) == 'p' ? "PM" : "AM"));

    private static bool IsAllDigits(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}

/// <summary>Immutable description of a segmented input's shape and navigation rules.</summary>
public sealed class SegmentedInputConfig
{
    /// <summary>Creates a config; <paramref name="separators"/> has one entry per gap.</summary>
    public SegmentedInputConfig(
        IReadOnlyList<SegmentSpec> segments,
        IReadOnlyList<string> separators,
        bool autoAdvance = true,
        bool wrapNavigation = false)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(separators);
        if (segments.Count == 0)
        {
            throw new ArgumentException("At least one segment is required.", nameof(segments));
        }

        if (separators.Count != segments.Count - 1)
        {
            throw new ArgumentException(
                "Separators count must be one less than segments count.",
                nameof(separators));
        }

        for (int i = 0; i < segments.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(segments[i]);
            if (segments[i].Capacity < 1)
            {
                throw new ArgumentException("Segment capacity must be at least 1.", nameof(segments));
            }
        }

        Segments = segments;
        Separators = separators;
        AutoAdvance = autoAdvance;
        WrapNavigation = wrapNavigation;
    }

    public IReadOnlyList<SegmentSpec> Segments { get; }
    public IReadOnlyList<string> Separators { get; }
    public bool AutoAdvance { get; }
    public bool WrapNavigation { get; }
}

/// <summary>
/// State owned by the host, never by <see cref="SegmentedInput"/>. Arrays are
/// copied on every transition, so an old state stays valid after an edit.
/// </summary>
/// <param name="Values">Current text of each segment.</param>
/// <param name="ActiveIndex">Index of the focused segment.</param>
/// <param name="Cursor">Insertion position inside the active segment.</param>
public sealed record SegmentedInputState(string[] Values, int ActiveIndex, int Cursor)
{
    public string Value(int index) => Values[index];
}

/// <summary>Result of <see cref="SegmentedInput.Validate"/>.</summary>
/// <param name="IsValid">True when every required segment is filled and valid.</param>
/// <param name="InvalidSegment">Index of the first failing segment, or -1.</param>
/// <param name="Error">Stable, human-readable reason; empty when valid.</param>
public readonly record struct SegmentedValidation(bool IsValid, int InvalidSegment, string Error);

/// <summary>
/// Pure state machine for segmented text entry. All methods take a state and
/// return a new state; the engine itself holds no mutable data. Handles character
/// entry, auto-advance, multi-segment paste, arrow/Tab navigation, backspace and
/// validation. Profiles (<see cref="Otp"/>, <see cref="Time24"/>,
/// <see cref="Time12"/>) are just configurations.
/// </summary>
public sealed class SegmentedInput
{
    /// <summary>Creates an engine over the given config.</summary>
    public SegmentedInput(SegmentedInputConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public SegmentedInputConfig Config { get; }

    /// <summary>OTP profile: N one-digit segments, optional grouping separator.</summary>
    public static SegmentedInput Otp(int digits = 6, string groupSeparator = "")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 1);
        var specs = new SegmentSpec[digits];
        for (int i = 0; i < digits; i++)
        {
            specs[i] = SegmentSpec.Digits();
        }

        var separators = new string[Math.Max(0, digits - 1)];
        for (int i = 0; i < separators.Length; i++)
        {
            separators[i] = groupSeparator.Length > 0 && (i + 1) % 3 == 0
                ? groupSeparator
                : string.Empty;
        }

        return new SegmentedInput(new SegmentedInputConfig(specs, separators));
    }

    /// <summary>24-hour time profile: HH:MM[:SS].</summary>
    public static SegmentedInput Time24(bool includeSeconds = false)
    {
        var specs = new List<SegmentSpec> { SegmentSpec.Range(0, 23), SegmentSpec.Range(0, 59) };
        if (includeSeconds)
        {
            specs.Add(SegmentSpec.Range(0, 59));
        }

        var separators = new string[specs.Count - 1];
        Array.Fill(separators, ":");
        return new SegmentedInput(new SegmentedInputConfig(specs, separators));
    }

    /// <summary>12-hour time profile: HH:MM AM/PM.</summary>
    public static SegmentedInput Time12() => new(new SegmentedInputConfig(
        new[] { SegmentSpec.Range(1, 12), SegmentSpec.Range(0, 59), SegmentSpec.Meridiem() },
        new[] { ":", " " }));

    /// <summary>Empty state with the first segment active.</summary>
    public SegmentedInputState Create()
    {
        var values = new string[Config.Segments.Count];
        Array.Fill(values, string.Empty);
        return new SegmentedInputState(values, 0, 0);
    }

    /// <summary>State preloaded from values, sanitized through each segment's rules.</summary>
    public SegmentedInputState CreateFrom(IReadOnlyList<string> values, int activeIndex = 0)
    {
        var state = Create();
        for (int i = 0; i < values.Count && i < Config.Segments.Count; i++)
        {
            state = SetSegment(state, i, values[i] ?? string.Empty);
        }

        return MoveTo(state, activeIndex);
    }

    /// <summary>Focuses a segment (clamped) with the cursor at its end.</summary>
    public SegmentedInputState SetActive(SegmentedInputState state, int index) => MoveTo(state, index);

    /// <summary>Moves to the next segment, wrapping only when configured.</summary>
    public SegmentedInputState MoveNext(SegmentedInputState state)
    {
        int next = state.ActiveIndex + 1;
        if (next >= Config.Segments.Count)
        {
            next = Config.WrapNavigation ? 0 : Config.Segments.Count - 1;
        }

        return MoveTo(state, next);
    }

    /// <summary>Moves to the previous segment, wrapping only when configured.</summary>
    public SegmentedInputState MovePrevious(SegmentedInputState state)
    {
        int next = state.ActiveIndex - 1;
        if (next < 0)
        {
            next = Config.WrapNavigation ? Config.Segments.Count - 1 : 0;
        }

        return MoveTo(state, next);
    }

    public SegmentedInputState MoveHome(SegmentedInputState state) => MoveTo(state, 0);

    public SegmentedInputState MoveEnd(SegmentedInputState state) => MoveTo(state, Config.Segments.Count - 1);

    /// <summary>Moves the cursor one character left inside the active segment.</summary>
    public SegmentedInputState MoveCursorLeft(SegmentedInputState state) => WithCursor(state, state.Cursor - 1);

    /// <summary>Moves the cursor one character right inside the active segment.</summary>
    public SegmentedInputState MoveCursorRight(SegmentedInputState state) => WithCursor(state, state.Cursor + 1);

    /// <summary>Enters one character into the active segment.</summary>
    public SegmentedInputState InsertChar(SegmentedInputState state, char c) =>
        Insert(state, c, Config.AutoAdvance);

    /// <summary>Removes the character before the cursor, crossing into the previous segment.</summary>
    public SegmentedInputState Backspace(SegmentedInputState state)
    {
        int index = Clamp(state.ActiveIndex);
        string current = state.Values[index];
        int cursor = Math.Clamp(state.Cursor, 0, current.Length);
        if (cursor > 0)
        {
            var values = (string[])state.Values.Clone();
            values[index] = current.Remove(cursor - 1, 1);
            return new SegmentedInputState(values, index, cursor - 1);
        }

        if (index > 0)
        {
            int previous = index - 1;
            string text = state.Values[previous];
            if (text.Length == 0)
            {
                return MoveTo(state, previous);
            }

            var values = (string[])state.Values.Clone();
            values[previous] = text[..^1];
            return new SegmentedInputState(values, previous, values[previous].Length);
        }

        return state;
    }

    /// <summary>Removes the character at the cursor within the active segment.</summary>
    public SegmentedInputState Delete(SegmentedInputState state)
    {
        int index = Clamp(state.ActiveIndex);
        string current = state.Values[index];
        int cursor = Math.Clamp(state.Cursor, 0, current.Length);
        if (cursor >= current.Length)
        {
            return state;
        }

        var values = (string[])state.Values.Clone();
        values[index] = current.Remove(cursor, 1);
        return new SegmentedInputState(values, index, cursor);
    }

    /// <summary>
    /// Distributes a pasted payload across segments from the active one. Separator
    /// characters and whitespace are ignored; each accepted character fills the
    /// current segment and advances into the next until the payload is exhausted
    /// or the last segment is full.
    /// </summary>
    public SegmentedInputState Paste(SegmentedInputState state, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return state;
        }

        var current = state;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || IsSeparatorCharacter(c))
            {
                continue;
            }

            var next = Insert(current, c, advanceWhenFull: true);
            if (!ReferenceEquals(next, current))
            {
                current = next;
            }
        }

        return current;
    }

    /// <summary>Replaces one segment's text, keeping only allowed characters.</summary>
    public SegmentedInputState SetSegment(SegmentedInputState state, int index, string value)
    {
        index = Clamp(index);
        var spec = Config.Segments[index];
        var text = new StringBuilder();
        for (int i = 0; i < value.Length && text.Length < spec.Capacity; i++)
        {
            if (spec.IsAllowed(value[i]))
            {
                text.Append(value[i]);
            }
        }

        string resolved = spec.Normalize is null ? text.ToString() : spec.Normalize(text.ToString());
        var values = (string[])state.Values.Clone();
        values[index] = resolved;
        return new SegmentedInputState(values, index, resolved.Length);
    }

    /// <summary>Clears all segments and focuses the first one.</summary>
    public SegmentedInputState Clear(SegmentedInputState state) => Create();

    /// <summary>First failing segment with a stable message, or a valid result.</summary>
    public SegmentedValidation Validate(SegmentedInputState state)
    {
        for (int i = 0; i < Config.Segments.Count; i++)
        {
            var spec = Config.Segments[i];
            string text = state.Values[i];
            if (spec.Required && text.Length != spec.Capacity)
            {
                return new SegmentedValidation(false, i, $"Segment {i} is incomplete.");
            }

            if (spec.IsValid is not null && !spec.IsValid(text))
            {
                return new SegmentedValidation(false, i, $"Segment {i} is invalid.");
            }
        }

        return new SegmentedValidation(true, -1, string.Empty);
    }

    public bool IsComplete(SegmentedInputState state) => Validate(state).IsValid;

    /// <summary>Maps a renderer key to an edit. Ctrl/Alt chords are left to the host.</summary>
    public SegmentedInputState HandleKey(SegmentedInputState state, UiKeyDto key)
    {
        if (key.Has(UiKeyMods.Ctrl) || key.Has(UiKeyMods.Alt))
        {
            return state;
        }

        return key.Kind switch
        {
            UiKeyKind.Char => key.Character.HasValue ? InsertChar(state, key.Character.Value) : state,
            UiKeyKind.Left => state.Cursor > 0 ? MoveCursorLeft(state) : MovePrevious(state),
            UiKeyKind.Right => state.Cursor < state.Values[Clamp(state.ActiveIndex)].Length
                ? MoveCursorRight(state)
                : MoveNext(state),
            UiKeyKind.Home => MoveHome(state),
            UiKeyKind.End => MoveEnd(state),
            UiKeyKind.Backspace => Backspace(state),
            UiKeyKind.Tab => MoveNext(state),
            _ => state,
        };
    }

    /// <summary>Plain text with separators (segments may be empty while editing).</summary>
    public string Render(SegmentedInputState state)
    {
        var text = new StringBuilder();
        for (int i = 0; i < Config.Segments.Count; i++)
        {
            if (i > 0)
            {
                text.Append(Config.Separators[i - 1]);
            }

            text.Append(state.Values[i]);
        }

        return text.ToString();
    }

    /// <summary>Paints the input on one row: active segment bold, others dim.</summary>
    public int Paint(ScreenBuffer buffer, int x, int y, SegmentedInputState state, CellStyle style = default)
    {
        var markup = new StringBuilder();
        for (int i = 0; i < Config.Segments.Count; i++)
        {
            if (i > 0)
            {
                markup.Append(MarkupCells.Escape(Config.Separators[i - 1]));
            }

            markup.Append(i == state.ActiveIndex ? "[bold]" : "[dim]")
                .Append(MarkupCells.Escape(state.Values[i]))
                .Append("[/]");
        }

        return MarkupCells.Paint(buffer, x, y, markup.ToString(), style);
    }

    private int Clamp(int index) => Math.Clamp(index, 0, Config.Segments.Count - 1);

    private SegmentedInputState WithCursor(SegmentedInputState state, int cursor)
    {
        int index = Clamp(state.ActiveIndex);
        int bounded = Math.Clamp(cursor, 0, state.Values[index].Length);
        return new SegmentedInputState((string[])state.Values.Clone(), index, bounded);
    }

    private SegmentedInputState MoveTo(SegmentedInputState state, int index)
    {
        index = Clamp(index);
        string text = state.Values[index];
        return new SegmentedInputState(
            (string[])state.Values.Clone(),
            index,
            Math.Min(text.Length, Config.Segments[index].Capacity));
    }

    private SegmentedInputState Insert(SegmentedInputState state, char c, bool advanceWhenFull)
    {
        int index = Clamp(state.ActiveIndex);
        var spec = Config.Segments[index];
        if (!spec.IsAllowed(c))
        {
            return state;
        }

        string current = state.Values[index];
        int cursor = Math.Clamp(state.Cursor, 0, current.Length);
        string updated;
        if (cursor >= current.Length)
        {
            if (current.Length >= spec.Capacity)
            {
                return state;
            }

            updated = current + c;
        }
        else
        {
            updated = current[..cursor] + c + current[(cursor + 1)..];
        }

        if (spec.Normalize is not null)
        {
            updated = spec.Normalize(updated);
        }

        var values = (string[])state.Values.Clone();
        values[index] = updated;
        var next = new SegmentedInputState(values, index, Math.Min(cursor + 1, updated.Length));
        if (updated.Length >= spec.Capacity && advanceWhenFull && index < Config.Segments.Count - 1)
        {
            return MoveTo(next, index + 1);
        }

        return next;
    }

    private bool IsSeparatorCharacter(char c)
    {
        for (int i = 0; i < Config.Separators.Count; i++)
        {
            string separator = Config.Separators[i];
            for (int j = 0; j < separator.Length; j++)
            {
                if (separator[j] == c)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
