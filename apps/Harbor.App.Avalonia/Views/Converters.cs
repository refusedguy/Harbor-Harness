using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Resolves a resource-key string (e.g. "ChatUserBrush") to the registered
///     <see cref="IBrush"/>. Used by the chat template + status bar.
/// </summary>
public sealed class BrushKeyConverter : IValueConverter
{
    /// <summary>Singleton instance for use as a static resource.</summary>
    public static readonly BrushKeyConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return null;
        if (global::Avalonia.Application.Current is null) return null;
        return global::Avalonia.Application.Current.Resources[key] as IBrush;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Returns true when the bound value equals the converter parameter. Used to
///     toggle visibility of the chat/code views based on <c>ActiveView</c>.
/// </summary>
public sealed class EqualityConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly EqualityConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Equals(value?.ToString(), parameter?.ToString());
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
