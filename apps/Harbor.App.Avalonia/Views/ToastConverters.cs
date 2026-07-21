using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Converts a <see cref="ToastKind" /> to an emoji icon glyph.
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
                ToastKind.Success => "✓",
                ToastKind.Warning => "⚠",
                ToastKind.Error => "✕",
                _ => "ℹ"
            };
        }
        return "ℹ";
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Converts a <see cref="ToastKind" /> directly to an <see cref="IBrush" /> resolved from
///     the application's resource dictionary.
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
            ToastKind.Success => "SuccessBrush",
            ToastKind.Warning => "WarningBrush",
            ToastKind.Error => "ErrorBrush",
            _ => "AccentBrush"
        };
        return Application.Current?.Resources[key] as IBrush;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
