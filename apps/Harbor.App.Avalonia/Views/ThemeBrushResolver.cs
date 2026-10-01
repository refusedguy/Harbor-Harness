// ThemeBrushResolver.cs — #948: the one place app code turns a theme key into a
// brush.
//
// Why this exists rather than five inline lookups: every brush the app defines
// lives in a merged ResourceInclude. App.axaml declares Application.Resources
// with a MergedDictionaries block and no direct entries, and ThemeService.ApplyHds
// swaps an element *inside* MergedDictionaries rather than writing to the
// dictionary — so the top level is empty for the whole life of the app.
//
// In Avalonia 12.1.0 the indexer reads only that top level:
//
//     public object? this[object key] {
//         get { TryGetValue(key, out var value); return value; }   // bool discarded
//     }
//
// (src/Avalonia.Base/Controls/ResourceDictionary.cs:35-41), and TryGetValue
// (:239-275) touches `_inner` alone. The walk over MergedDictionaries lives in
// TryGetResource (:188-237), which the indexer never calls.
//
// So the top-level indexer was a guaranteed miss in this app, and a miss is
// null — not a throw, not a default colour. `as IBrush` on that null is null,
// XAML gets a null brush, and Avalonia paints no brush at all. Five call sites
// were silently returning nothing (see #948).

using Avalonia.Media;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Resolves an HDS theme key (e.g. <c>StateSuccessBrush</c>) to its
///     <see cref="IBrush" />, walking the merged dictionaries.
/// </summary>
/// <remarks>
///     <para>
///         Returns <see langword="null" /> when there is no
///         <see cref="global::Avalonia.Application" />, when the key is not
///         declared, or when the declared value is not an
///         <see cref="IBrush" />. It never throws and never substitutes a
///         default: a caller that needs a guaranteed brush must say so, because a
///         silent null is what made this class of defect invisible.
///     </para>
/// </remarks>
internal static class ThemeBrushResolver
{
    /// <summary>
    ///     Resolves <paramref name="key" /> against the application's resource
    ///     chain, or <see langword="null" /> if it does not resolve to a brush.
    /// </summary>
    public static IBrush? Resolve(string key)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null)
            return null;

        return app.TryGetResource(key, null, out object? resource) ? resource as IBrush : null;
    }
}