namespace Harbor.Ui.Framework.Strings;

/// <summary>
///     Resolves the locale the UI renders in. Issue #1573: locale access moves
///     off the process-global static <see cref="UiStrings.CurrentLocale" /> onto
///     an injectable service so any consumer depends on an interface it can
///     substitute in tests (with a pinned locale) instead of mutating
///     <c>HARBOR_LOCALE</c> or the ambient culture.
/// </summary>
/// <remarks>
///     Registered as one singleton per host in the composition root — locale is
///     process-scoped by definition, and a second instance would let renderers
///     disagree mid-session. The pure resolution rules stay on
///     <see cref="UiStrings.ResolveLocale" />; implementations only choose where
///     the <c>HARBOR_LOCALE</c> value and the ambient culture come from.
/// </remarks>
public interface ILocaleService
{
    /// <summary>Locale the UI renders in: <c>HARBOR_LOCALE</c>, else ambient, else <c>en</c>.</summary>
    string CurrentLocale { get; }
}
