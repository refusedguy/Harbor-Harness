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
///     #755. This used to carry its own copies of the four path strings and call
///     <c>Geometry.Parse</c> per conversion, with a doc comment claiming "Path
///     data matches the icons in Themes/Hds/Icons.axaml" — three of the four did,
///     and the fourth (<c>IcInfo</c>) had drifted: its dot and bar were swapped
///     relative to the dictionary. Resolving the key instead of the data is what
///     makes the claim true by construction rather than by review.
/// </remarks>
public sealed class ToastIconConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly ToastIconConverter Instance = new();

    /// <summary>The generic info glyph — also the fallback for an unknown kind.</summary>
    private const string InfoIconResourceKey = "IcInfo";

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
        kind switch
        {
            ToastKind.Success => "IcCheck",
            ToastKind.Warning => "IcWarning",
            ToastKind.Error => "IcError",
            _ => InfoIconResourceKey,
        };

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
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

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Converts a <see cref="ToastKind" /> to an <see cref="IBrush" /> resolved from
///     the application's resource dictionary via HDS theme tokens.
/// </summary>
public sealed class ToastBrushConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly ToastBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            ToastKind.Success => "StateSuccessBrush",
            ToastKind.Warning => "StateWarningBrush",
            ToastKind.Error => "StateErrorBrush",
            _ => "AccentPrimaryBrush"
        };
        return ThemeBrushResolver.Resolve(key);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
