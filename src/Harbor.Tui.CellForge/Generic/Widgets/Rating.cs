using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// External, clamped state of a <see cref="Rating"/>: a value from 0 to
/// <see cref="Maximum"/>, a star count clamped to 1..<see cref="MaxStars"/>,
/// and a read-only flag. The host owns this value: it feeds a snapshot in
/// through <see cref="Rating.SetState"/> and receives a keyboard edit back
/// through <see cref="Rating.ValueChanged"/>. Keeping the state in a value type
/// means a host can persist or reject a selection without the widget holding a
/// second, hidden copy of the truth.
/// </summary>
public readonly record struct RatingState
{
    /// <summary>Digit keys 1..9 can select directly, so the scale stops at 9.</summary>
    public const int MaxStars = 9;

    /// <summary>The default five-star scale.</summary>
    public const int DefaultMaximum = 5;

    /// <summary>Clamped value.</summary>
    public int Value { get; }

    /// <summary>Clamped star count.</summary>
    public int Maximum { get; }

    /// <summary>Whether keyboard selection is disabled.</summary>
    public bool ReadOnly { get; }

    /// <summary>Creates a state, clamping the value into 0..Maximum and the maximum into 1..9.</summary>
    public RatingState(int value, int maximum = DefaultMaximum, bool readOnly = false)
    {
        Maximum = Math.Clamp(maximum, 1, MaxStars);
        Value = Math.Clamp(value, 0, Maximum);
        ReadOnly = readOnly;
    }

    /// <summary>Copy with a new value; the remaining fields stay put.</summary>
    public RatingState WithValue(int value) => new(value, Maximum, ReadOnly);

    /// <summary>Copy with a new read-only flag; the value stays put.</summary>
    public RatingState WithReadOnly(bool readOnly) => new(Value, Maximum, readOnly);
}

/// <summary>One accepted keyboard edit: the value before and after the key.</summary>
/// <param name="OldValue">Value before the key.</param>
/// <param name="NewValue">Value after the key; differs from <paramref name="OldValue"/>.</param>
public sealed record RatingChange(int OldValue, int NewValue) : EventArgs;

/// <summary>
/// Compact star-rating primitive with keyboard selection and a read-only mode.
/// Generalized from the ad-hoc "scale" strings the chat widgets paint: this type
/// knows no session, task, agent, or chat block, and it adds no plugin axis.
/// </summary>
/// <remarks>
/// <para>
///     The value is external state. A host constructs a <see cref="RatingState"/>
///     (or calls <see cref="SetState"/>) with the value it owns. A key computes a
///     new value, updates the local snapshot so the widget stays responsive on
///     its own, and raises <see cref="ValueChanged"/> so the host can persist it
///     or override it with a fresh <see cref="SetState"/>. No I/O, store, or
///     message is involved.
/// </para>
/// <para>
///     Key handling is a command gate (Press/Repeat only, no modifiers): Left,
///     Down, <c>h</c> and <c>j</c> step down; Right, Up, <c>l</c> and <c>k</c>
///     step up; Home selects one star, End selects the maximum, <c>0</c> clears,
///     and <c>1</c>..<c>9</c> select directly when the digit fits the scale.
///     Read-only refuses every key without consuming it. The file is declared
///     as a command site in the key-gate inventory.
/// </para>
/// </remarks>
public sealed class Rating
{
    /// <summary>Filled star.</summary>
    public const char FilledStar = '★';

    /// <summary>Empty star.</summary>
    public const char EmptyStar = '☆';

    /// <summary>Current externally supplied state.</summary>
    public RatingState State { get; private set; }

    /// <summary>Raised when a key changes the value, for host persistence.</summary>
    public event EventHandler<RatingChange>? ValueChanged;

    /// <summary>Creates a rating over a host-owned state snapshot.</summary>
    public Rating(RatingState state) => State = state;

    /// <summary>Creates a rating with a value and a five-star scale.</summary>
    public Rating(int value = 0, int maximum = RatingState.DefaultMaximum, bool readOnly = false)
        : this(new RatingState(value, maximum, readOnly))
    {
    }

    /// <summary>Replaces the external state. Normalizes through <see cref="RatingState"/>.</summary>
    public void SetState(RatingState state) => State = state;

    /// <summary>Sets only the value, keeping the maximum and read-only flag.</summary>
    public void SetValue(int value) => State = State.WithValue(value);

    /// <summary>
    /// Routes one key. Press and repeat only, with no modifiers. Unmodified
    /// arrows, vim keys, Home/End and digits select a value. Read-only and all
    /// other keys are left to the host. Returns true when consumed.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if ((key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat)
            || !key.Modifiers.IsUnmodified()
            || State.ReadOnly)
        {
            return false;
        }

        int target;
        switch (key.Key)
        {
            case KeyCode.Left:
            case KeyCode.Down:
                target = State.Value - 1;
                break;
            case KeyCode.Right:
            case KeyCode.Up:
                target = State.Value + 1;
                break;
            case KeyCode.Home:
                target = 1;
                break;
            case KeyCode.End:
                target = State.Maximum;
                break;
            case KeyCode.Char:
                return HandleChar(key.Character);
            default:
                return false;
        }

        return Apply(target);
    }

    /// <summary>
    /// Paints one compact row of stars plus a <c>value/maximum</c> label inside
    /// <paramref name="rect"/>. Filled stars use the warning token; read-only
    /// filled stars are dim and the label carries an <c> ro</c> suffix. The
    /// label is clipped when the rect is too narrow for it.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool readOnly = State.ReadOnly;
        var filledStyle = readOnly
            ? ChatPalette.Dim
            : new CellStyle(ChatPalette.Warning, attrs: StyleAttr.Bold);
        CellStyle emptyStyle = ChatPalette.Dim;

        int right = rect.X + rect.Width;
        int x = rect.X;
        for (int i = 0; i < State.Maximum && x < right; i++, x++)
        {
            buffer.SetText(x, rect.Y, (i < State.Value ? FilledStar : EmptyStar).ToString().AsSpan(),
                i < State.Value ? filledStyle : emptyStyle);
        }

        if (x >= right)
        {
            return;
        }

        string label = $" {State.Value}/{State.Maximum}" + (readOnly ? " ro" : string.Empty);
        int take = Math.Min(label.Length, right - x);
        if (take > 0)
        {
            buffer.SetText(x, rect.Y, label.AsSpan(0, take), ChatPalette.Dim);
        }
    }

    private bool HandleChar(Rune rune)
    {
        switch (rune.Value)
        {
            case 'h':
            case 'j':
                return Apply(State.Value - 1);
            case 'l':
            case 'k':
                return Apply(State.Value + 1);
            case '0':
                return Apply(0);
            case >= '1' and <= '9':
                int digit = rune.Value - '0';
                return digit <= State.Maximum && Apply(digit);
            default:
                return false;
        }
    }

    private bool Apply(int target)
    {
        int clamped = Math.Clamp(target, 0, State.Maximum);
        int oldValue = State.Value;
        if (clamped == oldValue)
        {
            // The key is still consumed at the edges: Left on an empty rating
            // and Right on a full rating are handled, not fall-through.
            return true;
        }

        State = State.WithValue(clamped);
        ValueChanged?.Invoke(this, new RatingChange(oldValue, clamped));
        return true;
    }
}
