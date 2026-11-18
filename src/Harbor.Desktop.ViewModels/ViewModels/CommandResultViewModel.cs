namespace Harbor.Desktop.ViewModels;
/// <summary>
///     One command-palette result row. Pure record — no UI-framework
///     dependency — extracted from <c>CommandPaletteViewModel.cs</c> so the
///     shape can be reused by WPF/MAUI/Blazor palette views.
/// </summary>
/// <param name="Kind">Category — "command", "slash", "file", or "session".</param>
/// <param name="Label">Primary text shown large.</param>
/// <param name="Hint">Secondary text shown muted (e.g. shortcut or category).</param>
/// <param name="Action">Callback invoked when the user activates this entry.</param>
public sealed record CommandResultViewModel(string Kind, string Label, string Hint, Action Action)
{
    /// <summary>
    ///     #2682 Z09: the hint as shown in a result row. Built-in commands
    ///     carry a technical key (<c>CodeEditorView</c>) in <see cref="Hint" />;
    ///     humanize it for display while the raw value stays searchable. Slash
    ///     rows carry an already-readable description, so they pass through.
    /// </summary>
    public string DisplayHint => Kind == "command" ? CommandNameHumanizer.Humanize(Hint) : Hint;

    /// <summary>Icon glyph based on kind.</summary>
    public string Icon => Kind switch
    {
        "command" => "⚡",
        "slash" => "/",
        "file" => "📄",
        "session" => "💬",
        _ => "•"
    };
}
