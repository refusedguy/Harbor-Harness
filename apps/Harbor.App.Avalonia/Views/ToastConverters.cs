using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Harbor.Ui.Framework.Services;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Converts a <see cref="ToastKind" /> to an <see cref="Geometry" /> (StreamGeometry
///     path data) for use in a XAML <see cref="Path" /> element. No emoji glyphs —
///     crisp at any DPI. Path data matches the icons in Themes/Hds/Icons.axaml.
/// </summary>
public sealed class ToastIconConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly ToastIconConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ToastKind kind)
        {
            return kind switch
            {
                ToastKind.Success => Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z"),
                ToastKind.Warning => Geometry.Parse("M1 21h22L12 2 1 21zm3.5-3h15l-7-12-7 12zm.5-1h13-13zm7-2h-1v-3h1v3zm0-4h-1V9h1v1z"),
                ToastKind.Error => Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z"),
                _ => Geometry.Parse("M12 4C7.59 4 4 7.59 4 12s3.59 8 8 8 8-3.59 8-8-3.59-8-8-8zm1 13h-2v-2h2v2zm0-4h-2V7h2v6z")
            };
        }
        return Geometry.Parse("M12 4C7.59 4 4 7.59 4 12s3.59 8 8 8 8-3.59 8-8-3.59-8-8-8zm1 13h-2v-2h2v2zm0-4h-2V7h2v6z");
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
        return global::Avalonia.Application.Current?.Resources[key] as IBrush;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
