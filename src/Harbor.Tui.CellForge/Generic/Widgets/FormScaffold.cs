using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>One key/value row of a <see cref="DescriptionList"/>.</summary>
/// <param name="Key">The row key, painted in the muted key column.</param>
/// <param name="Value">The row value, painted after the aligned key column.</param>
public readonly record struct DescriptionItem(string Key, string Value);

/// <summary>
/// Label slot of a form row: a primary text plus an optional secondary hint
/// such as <c>(required)</c>. The slot is host state; the scaffold only paints
/// it in the form's label column.
/// </summary>
/// <param name="Text">Primary label text.</param>
/// <param name="Secondary">Optional hint appended after the primary text.</param>
public readonly record struct FormLabel(string Text, string Secondary = "")
{
    /// <summary>The text painted in the label column.</summary>
    public string Display => string.IsNullOrEmpty(Secondary)
        ? Text ?? string.Empty
        : (Text ?? string.Empty) + " " + Secondary;
}

/// <summary>
/// Rune-boundary truncation shared by the frame title, the caption and the
/// description columns: it never splits a surrogate pair and counts
/// double-width runes against the cell budget.
/// </summary>
internal static class FormTextFit
{
    public static string Fit(string text, int width)
    {
        if (UnicodeWidth.Width(text.AsSpan()) <= width)
        {
            return text;
        }

        if (width <= 1)
        {
            return width == 1 ? "…" : string.Empty;
        }

        var span = text.AsSpan();
        int cells = 0;
        int i = 0;
        while (i < span.Length)
        {
            _ = Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed);
            if (consumed <= 0)
            {
                break;
            }

            int w = Math.Max(1, UnicodeWidth.Width(rune));
            if (cells + w > width - 1)
            {
                break;
            }

            cells += w;
            i += consumed;
        }

        return text[..i] + "…";
    }
}

/// <summary>
/// Host-neutral description list: key/value rows painted with one aligned
/// key column. It stores rows only; the host owns the data and never needs to
/// calculate column widths itself.
/// </summary>
public sealed class DescriptionList
{
    private readonly List<DescriptionItem> _items = new();

    /// <summary>Rows in host order.</summary>
    public IReadOnlyList<DescriptionItem> Items => _items;

    /// <summary>Number of rows.</summary>
    public int Count => _items.Count;

    /// <summary>Appends one key/value row.</summary>
    public void Add(string key, string value) => _items.Add(new DescriptionItem(key, value));

    /// <summary>Paints the aligned rows, clipped to <paramref name="rect"/>.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || _items.Count == 0)
        {
            return;
        }

        int keyWidth = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            keyWidth = Math.Max(keyWidth, UnicodeWidth.Width((_items[i].Key ?? string.Empty).AsSpan()));
        }

        keyWidth = Math.Min(keyWidth, Math.Max(0, rect.Width - 2));
        int valueX = rect.X + keyWidth + 2;
        int valueWidth = Math.Max(0, rect.Right - valueX);
        var keyStyle = new CellStyle(ChatPalette.Muted);
        var valueStyle = new CellStyle(ChatPalette.Text);

        for (int i = 0; i < _items.Count && i < rect.Height; i++)
        {
            int y = rect.Y + i;
            var item = _items[i];
            if (keyWidth > 0)
            {
                buffer.SetText(rect.X, y, FormTextFit.Fit(item.Key ?? string.Empty, keyWidth), keyStyle);
            }

            if (valueWidth > 0)
            {
                buffer.SetText(valueX, y, FormTextFit.Fit(item.Value ?? string.Empty, valueWidth), valueStyle);
            }
        }
    }
}

/// <summary>
/// One form scaffold: a titled group frame around a vertical field container,
/// a caption <see cref="FormLabel"/> slot, per-row label slots with room for a
/// validation error, and a <see cref="DescriptionList"/> under a separator.
/// </summary>
/// <remarks>
/// <para>
///     The field container is the existing <see cref="GenericForm"/>: it owns
///     focus, key routing and the label/error gutter. This type adds the frame,
///     the caption, the per-row label slot and the description list without
///     duplicating that painter or introducing an extension axis.
/// </para>
/// <para>
///     Values and validation errors come from the host. <see cref="SetError"/>
///     sets an external error for a row and takes precedence over the field's
///     own <see cref="IGenericFormField.Validate"/>; no domain rule lives here.
/// </para>
/// </remarks>
public sealed class FormScaffold
{
    private readonly GenericForm _form = new();
    private readonly List<ScaffoldField> _rows = new();
    private readonly DescriptionList _description = new();

    /// <summary>Title painted on the frame's top border; empty draws no title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Caption label slot painted above the fields.</summary>
    public FormLabel Label { get; set; }

    /// <summary>The titled group frame's content: fields, caption and description rows.</summary>
    public IReadOnlyList<IGenericFormField> Fields => _form.Fields;

    /// <summary>The key/value list painted under the fields.</summary>
    public DescriptionList Description => _description;

    /// <summary>Focused field index, or -1 when the form has no fields.</summary>
    public int FocusedIndex => _form.FocusedIndex;

    /// <summary>Total rows: top/bottom frame, caption, field and error rows, separator and description rows.</summary>
    public int DesiredHeight
    {
        get
        {
            int captionRows = string.IsNullOrEmpty(Label.Text) ? 0 : 1;
            int descriptionRows = _description.Count == 0 ? 0 : _description.Count + 1;
            return 2 + captionRows + _form.DesiredHeight + descriptionRows;
        }
    }

    /// <summary>Appends a field and captures its label into a per-row label slot.</summary>
    public void Add(IGenericFormField field)
    {
        Ensure.NotNull(field);
        var row = new ScaffoldField(field, new FormLabel(field.Label));
        _rows.Add(row);
        _form.Add(row);
    }

    /// <summary>Sets the label slot of one field row.</summary>
    public void SetLabel(int index, FormLabel label) => Row(index).Slot = label;

    /// <summary>
    /// Sets an external validation error for one field row; pass
    /// <see langword="null"/> to clear it.
    /// </summary>
    public void SetError(int index, string? error) => Row(index).ExternalError = error;

    /// <summary>Appends a key/value row to the description list.</summary>
    public void AddDescription(string key, string value) => _description.Add(key, value);

    /// <summary>Errors per row, with external errors overriding field validation.</summary>
    public string?[] ValidateAll() => _form.ValidateAll();

    /// <summary>True when every row validates, external errors included.</summary>
    public bool IsValid() => _form.IsValid();

    /// <summary>Routes navigation to the field chain and keys to the focused field.</summary>
    public bool HandleKey(in KeyEvent key) => _form.HandleKey(in key);

    /// <summary>
    /// Paints the titled frame and its content. Degenerate and off-buffer rects
    /// paint nothing; every write clips to the buffer.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        // SquareFrame does not blank the interior: the caller's own content
        // stays visible and this scaffold owns the rows inside the frame.
        PanelChrome.PaintBorderBox(buffer, rect, BoxStyle.SquareFrame);

        if (!string.IsNullOrEmpty(Title))
        {
            int avail = Math.Max(0, rect.Width - 4);
            if (avail > 0)
            {
                string title = FormTextFit.Fit(Title, avail);
                buffer.SetText(rect.X + 2, rect.Y, title, PanelChrome.TitleStyle);
                int after = rect.X + 2 + UnicodeWidth.Width(title.AsSpan());
                if (after < rect.Right - 1)
                {
                    buffer.SetText(after, rect.Y, " ", PanelChrome.BorderStyle);
                }
            }
        }

        Rect inner = PanelChrome.Inset(rect, 1, 1, 1, 1);
        int y = inner.Y;
        if (!string.IsNullOrEmpty(Label.Text) && y < inner.Bottom)
        {
            buffer.SetText(inner.X, y, FormTextFit.Fit(Label.Display, inner.Width), new CellStyle(ChatPalette.Muted));
            y++;
        }

        int descriptionRows = _description.Count == 0 ? 0 : _description.Count + 1;
        int formHeight = Math.Clamp(_form.DesiredHeight, 0, Math.Max(0, inner.Bottom - y - descriptionRows));
        if (formHeight > 0)
        {
            _form.Paint(buffer, new Rect(inner.X, y, inner.Width, formHeight));
            y += formHeight;
        }

        if (descriptionRows == 0)
        {
            return;
        }

        if (y < inner.Bottom)
        {
            PanelChrome.PaintMessageSeparator(buffer, inner.X, y, inner.Width);
            y++;
        }

        if (_description.Count > 0 && y < inner.Bottom)
        {
            _description.Paint(buffer, new Rect(inner.X, y, inner.Width, Math.Min(_description.Count, inner.Bottom - y)));
        }
    }

    private ScaffoldField Row(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _rows[index];
    }

    // Adapts a host field into the form's label column and error slot. The
    // inner field keeps painting and key handling; this wrapper only carries
    // the two pieces of scaffold state the generic painter reads.
    private sealed class ScaffoldField : IGenericFormField
    {
        private readonly IGenericFormField _inner;

        public ScaffoldField(IGenericFormField inner, FormLabel slot)
        {
            _inner = inner;
            Slot = slot;
        }

        public FormLabel Slot { get; set; }

        public string? ExternalError { get; set; }

        public string Label => Slot.Display.Length > 0 ? Slot.Display : _inner.Label;

        public int DesiredHeight => _inner.DesiredHeight;

        public void Paint(ScreenBuffer buffer, Rect rect) => _inner.Paint(buffer, rect);

        public bool HandleKey(in KeyEvent key) => _inner.HandleKey(in key);

        public string? Validate() => ExternalError ?? _inner.Validate();
    }
}
