using System;
using Harbor.Tui.CellForge.Generic.Widgets;

namespace Harbor.Tui.CellForge.Generic.Forms;

/// <summary>
///     Generic single-line input that can hide its display behind a mask rune.
///     The underlying text is always the real value; only painting is masked,
///     so the same primitive serves a plain field and a hidden field.
/// </summary>
/// <remarks>
///     Editing, caret motion and horizontal scroll come from
///     <see cref="GenericTextField"/>. This row keeps no scenario state and
///     leaves validation to the container, which owns the full value snapshot.
/// </remarks>
public sealed class MaskedTextInput : IGenericFormField
{
    private readonly GenericTextField _inner = new();

    /// <summary>Creates an input from a field description.</summary>
    /// <param name="descriptor">Description supplying label, mask and length.</param>
    public MaskedTextInput(FieldDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _inner.Label = descriptor.Label;
        _inner.Mask = descriptor.Mask;
        _inner.MaxLength = descriptor.MaxLength;
        _inner.Placeholder = descriptor.Placeholder;
    }

    /// <summary>Description this input was built from.</summary>
    public FieldDescriptor Descriptor { get; }

    /// <inheritdoc />
    public string Label => _inner.Label;

    /// <summary>The unmasked value.</summary>
    public string Text => _inner.Text;

    /// <inheritdoc />
    public int DesiredHeight => 1;

    /// <summary>Whether the caret row is painted.</summary>
    public bool IsFocused
    {
        get => _inner.IsFocused;
        set => _inner.IsFocused = value;
    }

    /// <summary>Replaces the value and clamps the caret.</summary>
    /// <param name="value">New value.</param>
    public void SetText(string value) => _inner.SetText(value ?? string.Empty);

    /// <summary>Paints the visible slice into <paramref name="rect"/>.</summary>
    /// <param name="buffer">Destination grid.</param>
    /// <param name="rect">Cell rectangle of this row.</param>
    public void Paint(ScreenBuffer buffer, Rect rect) => _inner.Paint(buffer, rect);

    /// <summary>Routes an editing key to the underlying text field.</summary>
    /// <param name="key">Key event.</param>
    /// <returns>True when the key changed the field.</returns>
    public bool HandleKey(in KeyEvent key) => _inner.HandleKey(in key);

    /// <inheritdoc />
    public string? Validate() => null;
}
