namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// A labelled, button-like slot beside a text field. The label is painted
/// between brackets; the id is returned by <see cref="ITextInput.HitTest"/>
/// so a host can route a click without the field knowing any command.
/// </summary>
public sealed record TextInputButton(string Id, string Label);

/// <summary>
/// Affix slots around a field: plain text and an optional button on each
/// side. Text is painted verbatim with a muted style; buttons get their own
/// style and a recorded hit rectangle.
/// </summary>
public sealed record TextInputAffixes
{
    /// <summary>No affixes. Safe to share: the record is immutable in practice.</summary>
    public static readonly TextInputAffixes None = new();

    public string Prefix { get; init; } = string.Empty;

    public string Suffix { get; init; } = string.Empty;

    public TextInputButton? PrefixButton { get; init; }

    public TextInputButton? SuffixButton { get; init; }
}

/// <summary>
/// Generalized text-input contract (#2362). Single-line
/// <see cref="TextInput"/> and <see cref="MultilineTextInput"/> both render an
/// external <see cref="TextInputState"/> through the same mask, validation and
/// affix hooks. The contract carries no key routing, no chat dependency and
/// no plugin registration: a host translates its own input events into edits
/// on the state.
/// </summary>
public interface ITextInput
{
    /// <summary>State owned by the host, not by this widget.</summary>
    TextInputState State { get; }

    /// <summary>Text and button slots painted around the field.</summary>
    TextInputAffixes Affixes { get; set; }

    /// <summary>Field focus, supplied by the host.</summary>
    bool IsFocused { get; set; }

    /// <summary>Rows the widget wants for the current state (autogrow for multiline).</summary>
    int DesiredHeight { get; }

    /// <summary>Error from the state's mask/validation hooks, or null.</summary>
    string? Validate();

    /// <summary>Paints the field into <paramref name="rect"/>.</summary>
    void Paint(ScreenBuffer buffer, Rect rect);

    /// <summary>Returns the id of the affix button under a cell, or null.</summary>
    string? HitTest(int x, int y);
}
