using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Neutral form-field contract (epic #1195 component model): label, height,
/// cell paint, key routing and validation. Implemented by the stateful
/// generic widgets (<see cref="GenericTextField"/>, <see cref="GenericSelectionList"/>);
/// display-only widgets (progress, table) stay out — a form row must accept keys.
/// </summary>
public interface IGenericFormField
{
    /// <summary>Row label; empty draws no label column.</summary>
    string Label { get; }

    /// <summary>Painted row count.</summary>
    int DesiredHeight { get; }

    /// <summary>Paints into <paramref name="rect"/> (exactly <see cref="DesiredHeight"/> rows).</summary>
    void Paint(ScreenBuffer buffer, Rect rect);

    /// <summary>Handles a key; true when consumed.</summary>
    bool HandleKey(in KeyEvent key);

    /// <summary>Error text, or null when the field is valid.</summary>
    string? Validate();
}

/// <summary>
/// Generic form container over cells (epic #1195): vertical stack of
/// <see cref="IGenericFormField"/> rows with a Tab/Shift+Tab focus chain,
/// per-row error line and whole-form validation. No chat coupling — approval
/// dialogs and setup wizards keep their chat primitives; this is the neutral
/// container future generic screens compose from.
/// </summary>
public sealed class GenericForm
{
    private readonly List<IGenericFormField> _fields = new();
    private int _focused;

    /// <summary>Rows in host order.</summary>
    public IReadOnlyList<IGenericFormField> Fields => _fields;

    /// <summary>Focused row index (-1 when empty).</summary>
    public int FocusedIndex => _fields.Count == 0 ? -1 : Math.Clamp(_focused, 0, _fields.Count - 1);

    /// <summary>Number of rows.</summary>
    public int Count => _fields.Count;

    /// <summary>Total painted rows including one error line per invalid field.</summary>
    public int DesiredHeight
    {
        get
        {
            int h = 0;
            for (int i = 0; i < _fields.Count; i++)
            {
                h += Math.Max(1, _fields[i].DesiredHeight);
                if (_fields[i].Validate() is not null)
                {
                    h += 1;
                }
            }

            return h;
        }
    }

    /// <summary>Appends a row.</summary>
    public void Add(IGenericFormField field)
    {
        Ensure.NotNull(field);
        _fields.Add(field);
    }

    /// <summary>Moves focus by a signed delta (wraps around).</summary>
    public void MoveFocus(int delta)
    {
        if (_fields.Count == 0)
        {
            return;
        }

        _focused = ((_focused + delta) % _fields.Count + _fields.Count) % _fields.Count;
    }

    /// <summary>Routes navigation keys; everything else goes to the focused row.</summary>
    /// <returns>True when consumed.</returns>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release || _fields.Count == 0)
        {
            return false;
        }

        bool shift = (key.Modifiers & KeyModifiers.Shift) != 0;
        switch (key.Key)
        {
            case KeyCode.Tab:
                MoveFocus(shift ? -1 : 1);
                return true;
            case KeyCode.Up when shift:
                MoveFocus(-1);
                return true;
            case KeyCode.Down when shift:
                MoveFocus(1);
                return true;
            default:
                return _fields[FocusedIndex].HandleKey(in key);
        }
    }

    /// <summary>Error per row (null entries are valid).</summary>
    public string?[] ValidateAll()
    {
        var errors = new string?[_fields.Count];
        for (int i = 0; i < _fields.Count; i++)
        {
            errors[i] = _fields[i].Validate();
        }

        return errors;
    }

    /// <summary>True when every row validates.</summary>
    public bool IsValid()
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            if (_fields[i].Validate() is not null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Paints labeled rows top to bottom with error lines under invalid rows.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _fields.Count == 0)
        {
            return;
        }

        int labelWidth = 0;
        for (int i = 0; i < _fields.Count; i++)
        {
            labelWidth = Math.Max(labelWidth, UnicodeWidth.Width(_fields[i].Label.AsSpan()));
        }

        int fieldX = rect.X + (labelWidth > 0 ? labelWidth + 2 : 0);
        int fieldW = rect.Width - (fieldX - rect.X);
        if (fieldW <= 0)
        {
            return;
        }

        var labelStyle = new CellStyle(ChatPalette.Muted);
        var focusedLabelStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var errorStyle = new CellStyle(ChatPalette.Error);

        int y = rect.Y;
        for (int i = 0; i < _fields.Count && y < rect.Y + rect.Height; i++)
        {
            var field = _fields[i];
            int rows = Math.Max(1, field.DesiredHeight);
            bool focused = i == FocusedIndex;

            if (labelWidth > 0 && field.Label.Length > 0)
            {
                buffer.SetText(rect.X, y, field.Label, focused ? focusedLabelStyle : labelStyle);
            }

            for (int r = 0; r < rows && y + r < rect.Y + rect.Height; r++)
            {
                // Focus marker in the gutter between label and field.
                if (labelWidth > 0)
                {
                    buffer.SetText(rect.X + labelWidth, y + r, focused ? "›" : " ", focused ? focusedLabelStyle : labelStyle);
                }
            }

            field.Paint(buffer, new Rect(fieldX, y, fieldW, rows));
            y += rows;

            string? error = field.Validate();
            if (error is not null && y < rect.Y + rect.Height)
            {
                buffer.SetText(fieldX, y, ("! " + error).AsSpan(0, Math.Min(error.Length + 2, fieldW)), errorStyle);
                y++;
            }
        }
    }
}
