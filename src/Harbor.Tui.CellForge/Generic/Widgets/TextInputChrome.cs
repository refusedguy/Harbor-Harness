namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Affix painting and hit rectangles shared by both text-input widgets
/// (#2362). The chrome reserves columns at the ends of a row, paints the
/// prefix and suffix slots there, and remembers where it painted buttons so a
/// later click can be routed by id. It holds no editing state.
/// </summary>
internal sealed class TextInputChrome
{
    private readonly List<(string Id, Rect Rect)> _buttons = new();

    public TextInputAffixes Affixes { get; set; } = TextInputAffixes.None;

    /// <summary>Display cells reserved on the left of the field.</summary>
    public int PrefixCells => ButtonCells(Affixes.PrefixButton) + Cells(Affixes.Prefix);

    /// <summary>Display cells reserved on the right of the field.</summary>
    public int SuffixCells => Cells(Affixes.Suffix) + ButtonCells(Affixes.SuffixButton);

    /// <summary>Clears hit rectangles before a fresh paint.</summary>
    public void Reset() => _buttons.Clear();

    /// <summary>Paints the left slots at the start of <paramref name="row"/>.</summary>
    public void PaintPrefix(ScreenBuffer buffer, Rect row)
    {
        var textStyle = new CellStyle(ChatPalette.Muted);
        var buttonStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        int x = row.X;
        if (Affixes.PrefixButton is { } button)
        {
            string label = "[" + button.Label + "]";
            buffer.SetText(x, row.Y, label.AsSpan(), buttonStyle);
            _buttons.Add((button.Id, new Rect(x, row.Y, Cells(label), 1)));
            x += Cells(label);
        }

        if (Affixes.Prefix.Length > 0)
        {
            buffer.SetText(x, row.Y, Affixes.Prefix.AsSpan(), textStyle);
        }
    }

    /// <summary>Paints the right slots at the end of <paramref name="row"/>.</summary>
    public void PaintSuffix(ScreenBuffer buffer, Rect row)
    {
        var textStyle = new CellStyle(ChatPalette.Muted);
        var buttonStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        int x = row.X + row.Width - SuffixCells;
        if (Affixes.Suffix.Length > 0)
        {
            buffer.SetText(x, row.Y, Affixes.Suffix.AsSpan(), textStyle);
            x += Cells(Affixes.Suffix);
        }

        if (Affixes.SuffixButton is { } button)
        {
            string label = "[" + button.Label + "]";
            buffer.SetText(x, row.Y, label.AsSpan(), buttonStyle);
            _buttons.Add((button.Id, new Rect(x, row.Y, Cells(label), 1)));
        }
    }

    /// <summary>Returns the id of the button covering a cell, or null.</summary>
    public string? HitTest(int x, int y)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            Rect rect = _buttons[i].Rect;
            if (x >= rect.X && x < rect.X + rect.Width && y == rect.Y)
            {
                return _buttons[i].Id;
            }
        }

        return null;
    }

    private static int Cells(string s) => s.Length == 0 ? 0 : UnicodeWidth.Width(s.AsSpan());

    private static int ButtonCells(TextInputButton? button) =>
        button is null || button.Label.Length == 0 ? 0 : Cells("[" + button.Label + "]");
}
