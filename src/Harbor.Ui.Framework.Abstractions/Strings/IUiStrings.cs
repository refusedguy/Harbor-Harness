namespace Harbor.Ui.Framework.Strings;

/// <summary>
///     The UI string catalogue as an injectable service (issue #1573): the same
///     lookup, drift-guard and fallback contract the static
///     <see cref="UiStrings" /> forwards exposed, held by a singleton in the
///     composition root instead of a process-global static type.
/// </summary>
/// <remarks>
///     <para>
///         Lookup rules are unchanged from the catalogue home decision
///         (<c>docs/adr/ADR-015-i18n-string-catalogue-home.md</c>, issue #434):
///         <c>en</c> is the source of truth, a key missing from the requested
///         satellite renders its <c>en</c> text, and only a key present in no
///         catalogue at all comes back as itself.
///     </para>
///     <para>
///         <see cref="CurrentLocale" /> rides along so a consumer that only asks
///         for strings never needs <see cref="ILocaleService" /> separately (the
///         <c>IThemeService</c> aggregate shape in
///         <c>Harbor.Ui.Framework.Services</c>). Depend on the narrow role you
///         resolve.
///     </para>
/// </remarks>
public interface IUiStrings : ILocaleService
{
    /// <summary>
    ///     Looks up <paramref name="key" /> in the resolved current locale
    ///     (<see cref="ILocaleService.CurrentLocale" />), falling back to <c>en</c>.
    /// </summary>
    string Get(string key);

    /// <summary>
    ///     Looks up <paramref name="key" /> in <paramref name="locale" />, falling
    ///     back to <c>en</c>. A key missing from the requested satellite renders its
    ///     <c>en</c> text; a key in no catalogue at all comes back as itself because
    ///     there is no <c>en</c> text to render.
    /// </summary>
    string Get(string key, string locale);

    /// <summary>
    ///     Enumerates the neutral (<c>en</c>) entries — the audit seam the
    ///     <c>UiStrings.Keys</c> drift guard walks. Production lookups go through
    ///     <see cref="Get(string)" />.
    /// </summary>
    IReadOnlyDictionary<string, string> GetEnglishEntries();
}
