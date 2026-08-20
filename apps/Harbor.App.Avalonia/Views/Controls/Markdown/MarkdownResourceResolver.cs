using Avalonia;
using Avalonia.Media;
namespace Harbor.App.Avalonia.Views.Controls.Markdown;
/// <summary>
///     Resolves <c>IBrush</c> / <c>FontFamily</c> resources for the
///     Markdown renderer. Extracted from <c>MarkdownRenderer.axaml.cs</c>
///     (Task R31 god-object decomposition) so the block / inline
///     renderers don't each carry their own copy of the same lookup
///     logic.
/// </summary>
/// <remarks>
///     <para>
///         All lookups go through <c>global::Avalonia.Application.Current.Resources.TryGetResource</c>
///         so they pick up theme-variant changes (Mocha dark / Latte light)
///         automatically. When a key is missing or the resource isn't the
///         expected type, the fallback is returned so the renderer never
///         crashes on a missing resource — the worst case is a slightly
///         off-color Run.
///     </para>
/// </remarks>
internal static class MarkdownResourceResolver
{
    /// <summary>
    ///     Look up a brush resource by key. Returns <paramref name="fallback" />
    ///     when the key is missing or the resource isn't an <see cref="IBrush" />.
    /// </summary>
    public static IBrush TryFindBrush(string key, IBrush fallback)
    {
        if (global::Avalonia.Application.Current?.Resources.TryGetResource(key, null, out object? r) == true && r is IBrush b)
        {
            return b;
        }
        return fallback;
    }

    /// <summary>
    ///     Look up a brush resource by key. Returns <see cref="Brushes.Gray" />
    ///     when missing — convenience overload for callers that don't have
    ///     a strong opinion about the fallback colour.
    /// </summary>
    public static IBrush TryFindStaticBrush(string key) => TryFindBrush(key, Brushes.Gray);

    /// <summary>
    ///     Look up a font-family resource by key. Returns <paramref name="fallback" />
    ///     when the key is missing or the resource isn't a <see cref="FontFamily" />.
    /// </summary>
    public static FontFamily TryFindFont(string key, FontFamily fallback)
    {
        if (global::Avalonia.Application.Current?.Resources.TryGetResource(key, null, out object? r) == true && r is FontFamily f)
        {
            return f;
        }
        return fallback;
    }
}
