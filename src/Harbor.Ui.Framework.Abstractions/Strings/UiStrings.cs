using System.Globalization;
using Harbor.Abstractions.Env;

namespace Harbor.Ui.Framework.Strings;

/// <summary>
///     Shared UI string catalogue (issue #434, slice 2: second locale + fallback).
///     The decision to home the catalogue here is
///     <c>docs/adr/ADR-015-i18n-string-catalogue-home.md</c>: this project is already
///     <c>Layer.Domain</c>, so TUI renderers, both apps and <c>contrib</c>-Blazor can
///     reach it with zero new matrix edges while <c>Harbor.Abstractions</c> stays frozen.
/// </summary>
/// <remarks>
///     <para>
///         <c>en</c> is the source of truth (<c>UiStrings.resx</c>, embedded neutral);
///         per-locale satellites (<c>UiStrings.&lt;locale&gt;.resx</c>) carry translations.
///         Avalonia projects the same keys into per-locale <c>ResourceDictionary</c> XAML
///         (AGENTS.md rule 9), Blazor adapts via <c>IStringLocalizer</c>, TUI/CLI look up
///         directly here.
///     </para>
///     <para>
///         Missing keys render <c>en</c>, never a raw resource key: a key translated in
///         no satellite still resolves through the neutral resources. Only a key that
///         exists in no catalogue at all comes back as itself, because there is no
///         <c>en</c> text to render for it.
///     </para>
///     <para>
///         Locale selection: <c>HARBOR_LOCALE</c> wins when set (unknown values mean
///         <c>en</c>); otherwise the ambient <see cref="CultureInfo.CurrentUICulture" />
///         donates its language when it is a shipped locale, else <c>en</c>.
///         Slice 2 ships <c>en</c> + <c>ru</c> and extracts 14 representative keys;
///         the remaining inventory occurrences stay pending (see
///         <c>docs/I18N_STRING_INVENTORY.md</c>).
///     </para>
///     <para>
///         Issue #1573: this static type no longer owns state. It is a forwarder
///         over one shared <see cref="UiStringsCatalog" /> (the migration shim the
///         issue sketch allows) for call sites the inventory extraction has not
///         converted yet — the catalogue itself lives behind
///         <see cref="IUiStrings" />, a DI singleton in the composition root,
///         substitutable in tests with a pinned locale instead of env/culture
///         mutation. <see cref="ResolveLocale" /> stays pure here.
///     </para>
/// </remarks>
public static class UiStrings
{
    /// <summary>Source-of-truth locale, and the fallback every miss resolves to.</summary>
    public const string DefaultLocale = "en";

    /// <summary>Env var that selects the UI locale; wins over the ambient culture.</summary>
    public const string EnvVar = HarborEnv.Locale;

    /// <summary>Locales with a shipped satellite, in preference order.</summary>
    public static readonly IReadOnlyList<string> SupportedLocales = ["en", "ru"];

    /// <summary>
    ///     Every key in the <c>en</c> source of truth. The drift test pins that this list
    ///     matches the neutral resources one-for-one, so a key added to the
    ///     <c>.resx</c> without a list entry (or vice versa) fails instead of
    ///     silently leaving a string un-auditable.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "Onboarding_Welcome",
        "Onboarding_PickModel",
        "Settings_Title",
        "Settings_ToggleTheme",
        "Palette_Title",
        "Palette_Prompt",
        "StatusBar_Idle",
        "ToolCall_Running",
        "ToolCall_Failed",
        "Toast_SaveFailed",
        "Error_FileNotFound",
        "Cli_Usage",
        "Cli_NewSession",
        "Cli_NoSessions",
    ];

    // Forwarder target (issue #1573): the shared default instance the migrated
    // seams resolve through DI, minus the indirection. Stateless — two
    // UiStringsCatalog instances disagree on nothing.
    private static readonly UiStringsCatalog Default = new();

    /// <summary>Locale the UI renders in: <c>HARBOR_LOCALE</c>, else ambient, else <c>en</c>.</summary>
    /// <remarks>
    ///     Issue #1573 forwarder: resolves through the shared catalogue
    ///     instance; new concrete call sites should resolve
    ///     <see cref="ILocaleService" /> from the host instead.
    /// </remarks>
    public static string CurrentLocale => Default.CurrentLocale;

    /// <summary>
    ///     Looks up <paramref name="key" /> in the current locale
    ///     (<see cref="CurrentLocale" />), falling back to <c>en</c>.
    /// </summary>
    /// <remarks>
    ///     Issue #1573 forwarder: the same lookup on the injectable
    ///     <see cref="IUiStrings" />, which the composition root registers as a
    ///     singleton. Kept until the inventory migration converts every call
    ///     site; new code should depend on the interface.
    /// </remarks>
    public static string Get(string key) => Default.Get(key);

    /// <summary>
    ///     Looks up <paramref name="key" /> in <paramref name="locale" />, falling back
    ///     to <c>en</c>. A key missing from the requested satellite renders its
    ///     <c>en</c> text; a key in no catalogue at all comes back as itself because
    ///     there is no <c>en</c> text to render.
    /// </summary>
    /// <remarks>Issue #1573 forwarder, as <see cref="Get(string)" />.</remarks>
    public static string Get(string key, string locale) => Default.Get(key, locale);

    /// <summary>
    ///     Enumerates the neutral (<c>en</c>) entries. Audit seam for the
    ///     <c>Keys</c> drift guard; production lookups go through <see cref="Get(string)" />.
    ///     Reads the <see cref="CultureInfo.InvariantCulture" /> set: the neutral
    ///     resources are the invariant set, not an <c>en</c> satellite (none ships).
    /// </summary>
    /// <remarks>Issue #1573 forwarder over the shared catalogue instance.</remarks>
    public static IReadOnlyDictionary<string, string> GetEnglishEntries() => Default.GetEnglishEntries();

    /// <summary>
    ///     Pure locale resolution behind <see cref="CurrentLocale" />, kept separate so
    ///     tests stay hermetic (no env mutation, no ambient-culture dependence).
    ///     Issue #1573 pulled the <c>HARBOR_LOCALE</c>/<see cref="CultureInfo.CurrentUICulture" />
    ///     reads out of the lookup into <see cref="UiStringsCatalog" />; this pure
    ///     half is unchanged.
    /// </summary>
    /// <param name="envValue">Value of <c>HARBOR_LOCALE</c>; <see langword="null" /> when unset.</param>
    /// <param name="uiCulture">Ambient UI culture; defaults to <see cref="CultureInfo.CurrentUICulture" />.</param>
    public static string ResolveLocale(string? envValue, CultureInfo? uiCulture = null)
    {
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return Normalize(envValue) ?? DefaultLocale;
        }

        string tag = (uiCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return Normalize(tag) ?? DefaultLocale;
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string tag = value.Trim();
        int separator = tag.IndexOfAny(['-', '_']);
        if (separator >= 0)
        {
            tag = tag[..separator];
        }

        tag = tag.ToLowerInvariant();
        return SupportedLocales.Contains(tag, StringComparer.Ordinal) ? tag : null;
    }
}
