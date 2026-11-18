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
/// <param name="KeyText">Optional global shortcut shown as a <c>Kbd</c> chip (#2682 Z06).</param>
public sealed record CommandResultViewModel(
    string Kind,
    string Label,
    string Hint,
    Action Action,
    string KeyText = "")
{
    /// <summary>Icon glyph based on kind.</summary>
    public string Icon => Kind switch
    {
        "command" => "⚡",
        "slash" => "/",
        "file" => "📄",
        "session" => "💬",
        _ => "•"
    };

    /// <summary>Whether this row has a binding worth a chip (#2682 Z06).</summary>
    public bool HasKeyText => !string.IsNullOrEmpty(KeyText);
}
