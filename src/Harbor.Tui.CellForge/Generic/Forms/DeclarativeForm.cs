using System;
using System.Collections.Generic;
using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Generic.Forms;

/// <summary>
///     Form container built entirely from <see cref="FieldDescriptor"/> rows.
///     It owns values, focus, validation and error painting, and works the same
///     way for any host scenario.
/// </summary>
/// <remarks>
///     <para>
///         The state a host reads is a plain string snapshot
///         (<see cref="Values"/>), which a State-to-Tree pipeline can project
///         without this type knowing that pipeline. No store, reducer or
///         widget registry is duplicated here.
///     </para>
///     <para>
///         Editing and focus are reused from the existing widget catalog
///         (<see cref="GenericForm"/> and <see cref="MaskedTextInput"/>); this
///         container only adds the declarative schema, the shared rule
///         snapshot and <see cref="FieldErrorDisplay"/> rows.
///     </para>
/// </remarks>
public sealed class DeclarativeForm
{
    private readonly FieldDescriptor[] _descriptors;
    private readonly MaskedTextInput[] _inputs;
    private readonly GenericForm _focus = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _external = new(StringComparer.Ordinal);
    private readonly FieldErrorDisplay _display = new();

    /// <summary>Builds a form from host descriptors.</summary>
    /// <param name="descriptors">Rows in paint and navigation order.</param>
    public DeclarativeForm(IEnumerable<FieldDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var list = new List<FieldDescriptor>(descriptors);
        if (list.Count == 0)
        {
            throw new ArgumentException("A form needs at least one field.", nameof(descriptors));
        }

        _descriptors = list.ToArray();
        _inputs = new MaskedTextInput[_descriptors.Length];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _descriptors.Length; i++)
        {
            FieldDescriptor descriptor = _descriptors[i];
            if (!ids.Add(descriptor.Id))
            {
                throw new ArgumentException("Duplicate field id: " + descriptor.Id, nameof(descriptors));
            }

            var input = new MaskedTextInput(descriptor);
            _inputs[i] = input;
            _values[descriptor.Id] = input.Text;
            _focus.Add(input);
        }
    }

    /// <summary>Row descriptions in host order.</summary>
    public IReadOnlyList<FieldDescriptor> Descriptors => _descriptors;

    /// <summary>Latest value snapshot, keyed by field id.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Focused row index, or -1 when the form is empty.</summary>
    public int FocusedIndex => _focus.FocusedIndex;

    /// <summary>Moves focus by a signed delta with wraparound.</summary>
    /// <param name="delta">Signed row offset.</param>
    public void MoveFocus(int delta) => _focus.MoveFocus(delta);

    /// <summary>Routes navigation and editing keys.</summary>
    /// <param name="key">Key event.</param>
    /// <returns>True when the key was consumed.</returns>
    public bool HandleKey(in KeyEvent key)
    {
        bool consumed = _focus.HandleKey(in key);
        SyncValues();
        return consumed;
    }

    /// <summary>Sets the value of one row by id; unknown ids are ignored.</summary>
    /// <param name="id">Field id.</param>
    /// <param name="value">New value.</param>
    public void SetValue(string id, string value)
    {
        for (int i = 0; i < _inputs.Length; i++)
        {
            if (!string.Equals(_descriptors[i].Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            _inputs[i].SetText(value);
            _values[id] = _inputs[i].Text;
            _ = _external.Remove(id);
            return;
        }
    }

    /// <summary>Sets or clears a host-provided error for one row.</summary>
    /// <param name="id">Field id.</param>
    /// <param name="error">Error text, or null to clear.</param>
    public void SetExternalError(string id, string? error)
    {
        if (!_values.ContainsKey(id))
        {
            return;
        }

        if (string.IsNullOrEmpty(error))
        {
            _ = _external.Remove(id);
        }
        else
        {
            _external[id] = error;
        }
    }

    /// <summary>Validates every row and returns one message per row.</summary>
    /// <returns>Null entries mark valid rows.</returns>
    public string?[] ValidateAll()
    {
        SyncValues();
        var errors = new string?[_descriptors.Length];
        for (int i = 0; i < errors.Length; i++)
        {
            errors[i] = ValidateRow(i);
        }

        return errors;
    }

    /// <summary>True when every declared rule passes.</summary>
    /// <returns>True when all rows are valid.</returns>
    public bool IsValid()
    {
        SyncValues();
        for (int i = 0; i < _descriptors.Length; i++)
        {
            if (ValidateRow(i) is not null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Current error rows, one per invalid field.</summary>
    /// <returns>Error rows in field order.</returns>
    public IReadOnlyList<FieldError> Errors()
    {
        SyncValues();
        var errors = new List<FieldError>();
        for (int i = 0; i < _descriptors.Length; i++)
        {
            if (ValidateRow(i) is { } message)
            {
                errors.Add(new FieldError(_descriptors[i].Id, message));
            }
        }

        return errors;
    }

    /// <summary>Total painted rows: one per field plus one per visible error.</summary>
    public int DesiredHeight
    {
        get
        {
            SyncValues();
            int height = _descriptors.Length;
            for (int i = 0; i < _descriptors.Length; i++)
            {
                if (ValidateRow(i) is not null)
                {
                    height++;
                }
            }

            return height;
        }
    }

    /// <summary>Paints labels, inputs and their error rows.</summary>
    /// <param name="buffer">Destination grid.</param>
    /// <param name="rect">Area to paint into.</param>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        SyncValues();
        int labelWidth = 0;
        for (int i = 0; i < _descriptors.Length; i++)
        {
            labelWidth = Math.Max(labelWidth, UnicodeWidth.Width(_descriptors[i].Label.AsSpan()));
        }

        labelWidth = Math.Min(labelWidth, Math.Max(0, rect.Width - 2));
        int fieldX = rect.X + (labelWidth > 0 ? labelWidth + 2 : 0);
        int fieldWidth = rect.Width - (fieldX - rect.X);
        var labelStyle = new CellStyle(ChatPalette.Muted);
        var focusedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);

        int y = rect.Y;
        for (int i = 0; i < _descriptors.Length && y < rect.Y + rect.Height; i++)
        {
            bool focused = i == FocusedIndex;
            if (labelWidth > 0)
            {
                string label = Fit(_descriptors[i].Label, labelWidth);
                buffer.SetText(rect.X, y, label, focused ? focusedStyle : labelStyle);
            }

            if (fieldWidth > 0)
            {
                _inputs[i].IsFocused = focused;
                _inputs[i].Paint(buffer, new Rect(fieldX, y, fieldWidth, 1));
            }

            y++;
            if (ValidateRow(i) is { } error && error.Length > 0 && y < rect.Y + rect.Height && fieldWidth > 0)
            {
                _display.Paint(
                    buffer,
                    new Rect(fieldX, y, fieldWidth, 1),
                    new[] { new FieldError(_descriptors[i].Id, error) });
                y++;
            }
        }
    }

    private void SyncValues()
    {
        for (int i = 0; i < _inputs.Length; i++)
        {
            _values[_descriptors[i].Id] = _inputs[i].Text;
        }
    }

    private string? ValidateRow(int index)
    {
        FieldDescriptor descriptor = _descriptors[index];
        if (_external.TryGetValue(descriptor.Id, out string? external))
        {
            return external;
        }

        var context = new FieldValidationContext(descriptor.Id, _inputs[index].Text, _values);
        for (int i = 0; i < descriptor.Validators.Count; i++)
        {
            string? error = descriptor.Validators[i].Validate(in context);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..width];
}
