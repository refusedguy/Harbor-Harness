using System.Globalization;
using System.Resources;

namespace Harbor.Ui.Framework.Strings;

/// <summary>
///     The DI singleton behind <see cref="IUiStrings" /> (issue #1573): owns the
///     <see cref="ResourceManager" /> for the embedded <c>en</c> truth and the
///     satellites, and resolves the render locale exactly as the static
///     <c>UiStrings</c> always did — <c>HARBOR_LOCALE</c> wins, unknown values
///     mean <c>en</c>, otherwise the ambient
///     <see cref="CultureInfo.CurrentUICulture" /> donates its language when it
///     is a shipped locale.
/// </summary>
/// <remarks>
///     <para>
///         Instances share no mutable state — the manager and the cached
///         <see cref="CultureInfo" />s are immutable — so two instances resolve
///         independently. That is what makes the service testable without
///         touching env or ambient culture: pin a locale in the constructor.
///     </para>
///     <para>
///         The pure resolution rules remain on
///         <see cref="UiStrings.ResolveLocale" />; this type only supplies the
///         inputs (env + ambient culture) that made the static shape awkward.
///     </para>
/// </remarks>
public sealed class UiStringsCatalog : IUiStrings
{
    // The base name derives from the catalogue type (namespace + name), which is
    // what the SDK embeds the .resx pair under. A string literal drifted once
    // (MissingManifestResourceException in CI); typeof() cannot.
    private readonly ResourceManager _manager = new(typeof(UiStrings));

    private readonly CultureInfo _english = CultureInfo.GetCultureInfo("en");
    private readonly CultureInfo _russian = CultureInfo.GetCultureInfo("ru");

    private readonly string? _localeOverride;

    /// <summary>
    ///     Resolves the locale at access time, as the process always has:
    ///     <c>HARBOR_LOCALE</c>, else ambient, else <c>en</c>.
    /// </summary>
    public UiStringsCatalog()
    {
    }

    /// <summary>
    ///     Pins the locale instead of reading <c>HARBOR_LOCALE</c>/the ambient
    ///     culture. The substitution seam the issue asks for: a test constructs
    ///     the catalogue pinned and never mutates process state. The value is
    ///     normalized like any other tag; an unknown locale falls back to
    ///     <c>en</c>.
    /// </summary>
    /// <param name="localeOverride">Locale tag to pin (e.g. <c>"ru"</c>).</param>
    public UiStringsCatalog(string? localeOverride)
    {
        _localeOverride = localeOverride;
    }

    /// <inheritdoc />
    public string CurrentLocale =>
        _localeOverride is null
            ? UiStrings.ResolveLocale(Environment.GetEnvironmentVariable(UiStrings.EnvVar))
            : UiStrings.ResolveLocale(_localeOverride);

    /// <inheritdoc />
    public string Get(string key) => Get(key, CurrentLocale);

    /// <inheritdoc />
    public string Get(string key, string locale)
    {
        Harbor.Abstractions.Extensions.Ensure.NotBlank(key);
        string tag = UiStrings.Normalize(locale) ?? UiStrings.DefaultLocale;
        if (!tag.Equals(UiStrings.DefaultLocale, StringComparison.Ordinal))
        {
            string? translated = _manager.GetString(key, CultureFor(tag));
            if (translated is not null)
            {
                return translated;
            }
        }

        return _manager.GetString(key, _english) ?? key;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetEnglishEntries()
    {
        ResourceSet? set = _manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
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

    private CultureInfo CultureFor(string tag) => tag switch
    {
        "ru" => _russian,
        _ => _english,
    };
}
