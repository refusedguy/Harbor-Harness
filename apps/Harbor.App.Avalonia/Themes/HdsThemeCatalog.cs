using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Harbor.App.Avalonia.Themes;

/// <summary>
///     The HDS palettes the Avalonia app can apply, read out of the theme
///     dictionaries themselves. This is the app's one palette index: it turns
///     each <c>Themes/Hds/&lt;Name&gt;.axaml</c> into an
///     <see cref="HdsThemePreview" /> (surface / accent / text brushes + the
///     <see cref="ThemeVariant" /> the palette declares) so the Settings screen
///     can show a live thumbnail of a palette the user is only <em>considering</em>.
/// </summary>
/// <remarks>
///     <para>
///         #673 — this replaces a table hand-copied into
///         <c>ThemeSettingsViewModel</c>: fifteen <c>Color.Parse</c> literals that
///         were the same values <c>CatppuccinMocha.axaml</c> declares, plus a
///         hand-written "which palettes are dark" array. Both were second
///         sources of truth for the design system and both had already drifted —
///         the dark array named three palettes while the folder holds a fourth
///         (<c>HarborDesignTokens</c>), which the Settings previews therefore
///         never showed. Reading the dictionary removes the possibility: a colour
///         can no longer be typed twice.
///     </para>
///     <para>
///         The dictionaries are loaded with the same <see cref="ResourceInclude" />
///         mechanism <c>ThemeService.ApplyHds</c> uses to swap slot 1, so a
///         thumbnail is rendered from the very dictionary that applying the
///         palette would install.
///     </para>
///     <para>
///         <b>Why <see cref="PaletteNames" /> is a list and not a directory scan.</b>
///         A compiled Avalonia app embeds no XAML source (only the compiled
///         resources), and <c>avares://</c> offers no directory enumeration, so
///         "which files are in this folder" cannot be answered at runtime. The
///         list therefore holds <em>names only</em> — no colour, no variant, both
///         of which come from the dictionary — and
///         <c>HdsThemeCatalogParityTests</c> binds it back to the folder, so a
///         palette file without an entry here (or an entry without a file) fails
///         CI instead of silently not showing up in Settings.
///     </para>
/// </remarks>
public static class HdsThemeCatalog
{
    /// <summary>
    ///     Key each palette declares its <see cref="ThemeVariant" /> under.
    ///     Its value is a <c>ThemeVariant</c> key: <c>"Dark"</c> or <c>"Light"</c>.
    /// </summary>
    public const string VariantKey = "HdsThemeVariant";

    private const string PaletteFolder = "avares://Harbor.App.Avalonia/Themes/Hds/";

    // Same token names the rest of the UI resolves through DynamicResource —
    // the thumbnail is a preview of those roles, not a private palette.
    private const string SurfaceKey = "AppBackgroundBrush";
    private const string AccentKey = "AccentBrush";
    private const string TextKey = "TextBrush";

    /// <summary>
    ///     Assembly base URI for the app's <c>avares://</c> resources — the
    ///     resolution root every palette include is loaded against.
    /// </summary>
    public static readonly Uri BaseUri = new("avares://Harbor.App.Avalonia/", UriKind.Absolute);

    private static readonly string DarkKey = ThemeVariant.Dark.Key.ToString() ?? "Dark";
    private static readonly string LightKey = ThemeVariant.Light.Key.ToString() ?? "Light";

    /// <summary>
    ///     Every palette the app ships, in the order Settings lists them.
    ///     Names only — see the type remarks for why this cannot be a directory
    ///     scan, and for the test that keeps it equal to the folder.
    /// </summary>
    public static IReadOnlyList<string> PaletteNames { get; } =
    [
        "CatppuccinMocha",
        "HarborDesignTokens",
        "Lumen",
        "Mono",
        "Paper",
        "Vapor",
    ];

    /// <summary>
    ///     Read one preview per palette out of the palette dictionaries. A
    ///     method rather than a property on purpose: it materialises a fresh
    ///     collection per call, and a property that copies a collection is
    ///     indistinguishable from a cached one at the call site (S2365).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately not cached. Six compiled dictionaries load in
    ///         well under a millisecond, and the only caller
    ///         (<c>ThemeSettingsViewModel</c>, built when the Settings dialog
    ///         opens) snapshots the list into its collection once. A cache would
    ///         add a process-wide mutable that a *failed* load could poison
    ///         forever — and a failed load is exactly what happens when the
    ///         view-model is constructed before the Avalonia resource system is
    ///         up, which is what the DI smoke tests do.
    ///     </para>
    ///     <para>
    ///         A palette whose dictionary does not resolve the three brushes and
    ///         the variant key is reported on stderr and left out; the headless
    ///         tests in <c>HdsThemeCatalogParityTests</c> fail if that ever
    ///         happens with a runtime that can load resources, so the degraded
    ///         path cannot become the normal one.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<HdsThemePreview> ReadAll() =>
        PaletteNames.Select(TryLoad).OfType<HdsThemePreview>().ToArray();

    /// <summary>The <c>avares://</c> URI of one palette dictionary.</summary>
    /// <param name="name">Palette name, e.g. <c>Vapor</c>.</param>
    public static Uri PaletteUri(string name) => new(PaletteFolder + name + ".axaml", UriKind.Absolute);

    /// <summary>
    ///     The preview for <paramref name="name" />, or <c>null</c> when no such
    ///     palette exists. Only one dictionary is read, and only for a name the
    ///     catalog knows: <c>ThemeService.ApplyHds</c> takes the same free-form
    ///     name, so an unknown one must not turn into a failed load.
    /// </summary>
    /// <param name="name">Palette name.</param>
    public static HdsThemePreview? Find(string? name) =>
        name is not null && PaletteNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? TryLoad(name)
            : null;

    private static HdsThemePreview? TryLoad(string name)
    {
        try
        {
            var palette = new ResourceInclude(BaseUri) { Source = PaletteUri(name) };

            // Each read is guarded on its own so a palette missing one token is
            // dropped with a message naming that token, instead of failing
            // somewhere downstream with a null brush.
            if (!TryGetVariant(palette, name, out ThemeVariant? variant)) return null;
            if (!TryGetBrush(palette, name, SurfaceKey, out IBrush? surface)) return null;
            if (!TryGetBrush(palette, name, AccentKey, out IBrush? accent)) return null;
            if (!TryGetBrush(palette, name, TextKey, out IBrush? text)) return null;

            return new HdsThemePreview(name, Humanize(name), variant, surface, accent, text);
        }
        catch (Exception ex)
        {
            // A thumbnail is decoration, so a palette that cannot be loaded is
            // reported and left out. Nothing here may escape: the previews are
            // materialised from a view-model constructor, which the DI graph runs
            // while it is still being built (AppHostDiTests resolves MainViewModel
            // with no Avalonia runtime at all, where resource loading is not
            // available and must not throw).
            Console.Error.WriteLine(
                $"[HARBOR_THEME] HDS palette '{name}' failed to load: {ex.Message} — palette not offered (#673).");
            return null;
        }
    }

    private static bool TryGetVariant(
        ResourceInclude palette,
        string name,
        [NotNullWhen(true)] out ThemeVariant? variant)
    {
        variant = null;

        if (!TryGet(palette, VariantKey, out object? declared))
        {
            return false;
        }

        if (declared is not string key)
        {
            Console.Error.WriteLine(
                $"[HARBOR_THEME] HDS palette '{name}' declares {VariantKey} as {declared?.GetType().Name ?? "null"}, " +
                $"expected a ThemeVariant name ({DarkKey} / {LightKey}) — palette not offered (#673).");
            return false;
        }

        if (string.Equals(key, DarkKey, StringComparison.Ordinal))
        {
            variant = ThemeVariant.Dark;
            return true;
        }

        if (string.Equals(key, LightKey, StringComparison.Ordinal))
        {
            variant = ThemeVariant.Light;
            return true;
        }

        Console.Error.WriteLine(
            $"[HARBOR_THEME] HDS palette '{name}' declares {VariantKey}='{key}', which is not a " +
            $"ThemeVariant ({DarkKey} / {LightKey}) — palette not offered (#673).");
        return false;
    }

    private static bool TryGetBrush(
        ResourceInclude palette,
        string name,
        string key,
        [NotNullWhen(true)] out IBrush? brush)
    {
        brush = null;

        if (TryGet(palette, key, out object? value) && value is IBrush resolved)
        {
            brush = resolved;
            return true;
        }

        Console.Error.WriteLine(
            $"[HARBOR_THEME] HDS palette '{name}' does not resolve '{key}' — palette not offered (#673).");
        return false;
    }

    /// <summary>
    ///     One resource out of a palette dictionary, on the variant the palette
    ///     itself declares. <c>ThemeVariant.Default</c> rather than a concrete
    ///     Dark/Light so a themed resource would still resolve for a viewer whose
    ///     variant differs from the one the palette was authored for.
    /// </summary>
    private static bool TryGet(ResourceInclude palette, string key, out object? value) =>
        palette.TryGetResource(key, ThemeVariant.Default, out value);

    /// <summary>
    ///     <c>CatppuccinMocha</c> → <c>Catppuccin Mocha</c>. A palette's label is
    ///     presentation, not a design token, so it is derived from the id here
    ///     rather than declared as another value that has to be kept in sync.
    /// </summary>
    private static string Humanize(string name)
    {
        var label = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            // Word boundary: an upper-case letter that follows a lower-case one
            // ("…noMocha"), or that starts a new word inside a run of capitals
            // ("…HDSTheme" → "HDS Theme"). i > 0 is checked first so the
            // previous-character test never reads name[-1].
            if (i > 0
                && char.IsUpper(name[i])
                && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                label.Append(' ');
            }

            label.Append(name[i]);
        }

        return label.ToString();
    }
}
