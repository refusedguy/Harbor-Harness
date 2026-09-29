using Avalonia.Media;
using Avalonia.Styling;

namespace Harbor.App.Avalonia.Themes;

/// <summary>
///     One HDS palette as the palette dictionary itself declares it — the
///     Settings screen's live thumbnail data, read from
///     <c>Themes/Hds/&lt;Name&gt;.axaml</c> by <see cref="HdsThemeCatalog" />.
/// </summary>
/// <remarks>
///     <para>
///         This is a projection, not a definition. Every colour on it is the
///         brush object the theme dictionary declares (<c>AppBackgroundBrush</c> /
///         <c>AccentBrush</c> / <c>TextBrush</c>) and every light/dark answer is
///         the <c>ThemeVariant</c> the dictionary names in <c>HdsThemeVariant</c>.
///         Nothing here is a copy: there is no colour literal in this assembly
///         (see <c>ThemeTokenDuplicationGuardTests</c>), and the reason is
///         AGENTS.md rule 9 — the XAML <c>ResourceDictionary</c> is the single
///         source of truth for design tokens.
///     </para>
///     <para>
///         The values are read from the dictionary rather than bound to
///         <c>{DynamicResource}</c> on purpose: a thumbnail has to render the
///         palette the user is <em>considering</em>, not the palette currently
///         applied, so there is no live resource chain to bind to.
///     </para>
/// </remarks>
/// <param name="Name">Palette name — the dictionary's file name without <c>.axaml</c>.</param>
/// <param name="DisplayName">Human-readable label derived from <paramref name="Name" />.</param>
/// <param name="Variant">The <see cref="ThemeVariant" /> the palette declares it is designed for.</param>
/// <param name="Surface">The palette's app-background brush, for the thumbnail's base.</param>
/// <param name="Accent">The palette's accent brush.</param>
/// <param name="Text">The palette's text brush.</param>
public sealed record HdsThemePreview(
    string Name,
    string DisplayName,
    ThemeVariant Variant,
    IBrush Surface,
    IBrush Accent,
    IBrush Text)
{
    /// <summary>
    ///     Whether this palette is a dark one — i.e. whether
    ///     <see cref="Variant" /> is <see cref="ThemeVariant.Dark" />, as the
    ///     palette itself declared it. The C# side holds no list of "which
    ///     palettes are dark": that list was a hand-written array which had
    ///     already missed a palette the folder contains (#673).
    /// </summary>
    public bool IsDark => ReferenceEquals(Variant, ThemeVariant.Dark);
}
