using System.Globalization;
using System.Resources;

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
/// </remarks>
public static class UiStrings
{
    /// <summary>Source-of-truth locale, and the fallback every miss resolves to.</summary>
    public const string DefaultLocale = "en";

    /// <summary>Env var that selects the UI locale; wins over the ambient culture.</summary>
    public const string EnvVar = "HARBOR_LOCALE";

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

    private static readonly ResourceManager Manager = new(
        "Harbor.Ui.Framework.Abstractions.Strings.UiStrings",
        typeof(UiStrings).Assembly);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru");

    /// <summary>Locale the UI renders in: <c>HARBOR_LOCALE</c>, else ambient, else <c>en</c>.</summary>
    public static string CurrentLocale =>
        ResolveLocale(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>
    ///     Pure locale resolution behind <see cref="CurrentLocale" />, kept separate so
    ///     tests stay hermetic (no env mutation, no ambient-culture dependence).
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

    /// <summary>
    ///     Looks up <paramref name="key" /> in the current locale
    ///     (<see cref="CurrentLocale" />), falling back to <c>en</c>.
    /// </summary>
    public static string Get(string key) => Get(key, CurrentLocale);

    /// <summary>
    ///     Looks up <paramref name="key" /> in <paramref name="locale" />, falling back
    ///     to <c>en</c>. A key missing from the requested satellite renders its
    ///     <c>en</c> text; a key in no catalogue at all comes back as itself because
    ///     there is no <c>en</c> text to render.
    /// </summary>
    public static string Get(string key, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string tag = Normalize(locale) ?? DefaultLocale;
        if (!tag.Equals(DefaultLocale, StringComparison.Ordinal))
        {
            string? translated = Manager.GetString(key, CultureFor(tag));
            if (translated is not null)
            {
                return translated;
            }
        }

        return Manager.GetString(key, English) ?? key;
    }

    /// <summary>
    ///     Enumerates the neutral (<c>en</c>) entries. Audit seam for the
    ///     <c>Keys</c> drift guard; production lookups go through <see cref="Get(string)" />.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetEnglishEntries()
    {
        ResourceSet? set = Manager.GetResourceSet(English, createIfNotExists: true, tryParents: false);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (set is null)
        {
            return entries;
        }

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                entries[name] = value;
            }
        }

        return entries;
    }

    private static string? Normalize(string? value)
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

    private static CultureInfo CultureFor(string tag) => tag switch
    {
        "ru" => Russian,
        _ => English,
    };
}
