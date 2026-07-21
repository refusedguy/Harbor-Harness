namespace Harbor.Desktop.Abstractions.Models;
/// <summary>
///     One entry in the command palette. Title + subtitle drive fuzzy-match
///     scoring (see <c>Harbor.Desktop.Shared.Services.FuzzySearchService</c>);
///     <see cref="Action" /> is invoked when the user selects the entry.
/// </summary>
/// <param name="Title">Primary text shown large.</param>
/// <param name="Subtitle">Secondary text shown muted (e.g. keyboard shortcut or category).</param>
/// <param name="Icon">Optional icon glyph or asset path; platform decides how to render.</param>
/// <param name="Action">Callback invoked when the user activates this entry.</param>
public sealed record CommandPaletteItem(
    string Title,
    string? Subtitle,
    string? Icon,
    Action Action)
{
    /// <summary>Build a <see cref="CommandPaletteItem" /> with no subtitle and no icon.</summary>
    public static CommandPaletteItem Create(string title, Action action)
        => new(title, null, null, action);
}
