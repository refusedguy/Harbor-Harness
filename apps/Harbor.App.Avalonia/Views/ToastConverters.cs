using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Harbor.Ui.Framework.Services;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Converts a <see cref="ToastKind" /> to the HDS glyph declared in
///     Themes/Hds/Icons.axaml, for use in a XAML <see cref="Path" /> element. No
///     emoji glyphs — crisp at any DPI.
/// </summary>
/// <remarks>
///     <para>
///         #755. This used to carry its own copies of the four path strings and call
///         <c>Geometry.Parse</c> per conversion, with a doc comment claiming "Path
///         data matches the icons in Themes/Hds/Icons.axaml" — three of the four did,
///         and the fourth (<c>IcInfo</c>) had drifted: its dot and bar were swapped
///         relative to the dictionary. Resolving the key instead of the data is what
///         makes the claim true by construction rather than by review.
///     </para>
///     <para>
///         #1543. The kind-to-key mapping left this file too: it now reads the
///         shared table (<see cref="ToastPresentations" />), which is the same table
///         the terminal renderer reads for the toast's glyph. What is left here is
///         the Avalonia-shaped adapter — resolve the key against the merged
///         dictionaries — and nothing that decides what a kind looks like.
///     </para>
/// </remarks>
public sealed class ToastIconConverter : OneWayConverterBase<ToastIconConverter>
{
    /// <summary>
    ///     The HDS resource key that paints <paramref name="kind" />, or the
    ///     generic info glyph for a value that is not a <see cref="ToastKind" />.
    /// </summary>
    /// <remarks>
    ///     Public and static for the same reason as
    ///     <c>FileTypeToGeometryConverter.IconResourceKeyFor</c>: the mapping is
    ///     the part worth asserting, and asserting it must not need an Avalonia
    ///     <c>Application</c>.
    /// </remarks>
    public static string IconResourceKeyFor(ToastKind? kind) =>
        ToastPresentations.IconResourceKey(kind ?? ToastKind.Info);

    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // TryGetResource, not the direct indexer: it walks the merged
        // dictionaries, which is where Icons.axaml lives (App.axaml cascade
        // slot [2]). Same lookup, and same reason, as Views/Converters.cs.
        if (global::Avalonia.Application.Current is null) return null;
        return global::Avalonia.Application.Current.TryGetResource(
                   IconResourceKeyFor(value as ToastKind?), null, out object? resource)
            ? resource
            : null;
    }
}

/// <summary>
///     Converts a <see cref="ToastKind" /> to an <see cref="IBrush" /> resolved from
///     the application's resource dictionary via HDS theme tokens.
/// </summary>
/// <remarks>
///     <para>
///         Static for the same reason as
///         <c>ToastIconConverter.IconResourceKeyFor</c>: the mapping is the
///         part worth asserting, and asserting it must not need an Avalonia
///         <c>Application</c>.
///     </para>
///     <para>
///         #1543. The key comes from the shared table
///         (<see cref="ToastPresentations" />), so the brush a toast wears and the
///         glyph it leads with are two columns of one row rather than two switches
///         that can disagree.
///     </para>
/// </remarks>
public sealed class ToastBrushConverter : OneWayConverterBase<ToastBrushConverter>
{
    /// <summary>
    ///     The HDS resource key that paints <paramref name="kind" />, or the
    ///     accent brush for a value that is not a <see cref="ToastKind" />.
    /// </summary>
    /// <remarks>
    ///     Static for the same reason as
    ///     <c>ToastIconConverter.IconResourceKeyFor</c>: the mapping is the
    ///     part worth asserting, and asserting it must not need an Avalonia
    ///     <c>Application</c>.
    /// </remarks>
    public static string BrushKeyFor(ToastKind? kind) =>
        ToastPresentations.BrushResourceKey(kind ?? ToastKind.Info);

    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return ThemeBrushResolver.Resolve(BrushKeyFor(value as ToastKind?));
    }
}
