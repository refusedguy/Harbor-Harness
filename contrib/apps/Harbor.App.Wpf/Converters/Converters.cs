using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Harbor.Ui.Framework.Converters;
namespace Harbor.App.Wpf.Converters;
/// <summary>
///     Resolves a resource-key string (e.g. "ChatUserBrush") to the
///     registered <see cref="Brush" /> from <c>Application.Current.Resources</c>.
///     Mirrors the Avalonia <c>BrushKeyConverter</c> so the same
///     <c>StatusMappers.*BrushKey</c> strings can be reused across both
///     desktop frameworks.
/// </summary>
public sealed class BrushKeyConverter : IValueConverter
{
    /// <summary>Singleton instance for use as a static resource.</summary>
    public static readonly BrushKeyConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return null;
        if (Application.Current is null) return null;
        return Application.Current.Resources[key] as Brush;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Returns <c>Visibility.Visible</c> for non-null/non-empty strings,
///     <c>Visibility.Collapsed</c> otherwise. Used by ChatBubble to hide
///     the timestamp row when no timestamp is set.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value?.ToString())
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.StatusToBrushKey" /> as a WPF
///     <see cref="IValueConverter" />. Resolves the returned resource key
///     to a <see cref="Brush" /> via <see cref="BrushKeyConverter" /> so the
///     status-bar accent can be bound directly to a status string.
/// </summary>
public sealed class StatusTextToBrushConverter : IValueConverter
{
    public static readonly StatusTextToBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = StatusMappers.StatusToBrushKey(value?.ToString());
        return BrushKeyConverter.Instance.Convert(key, targetType, parameter, culture);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.TimeAgo" /> as a WPF
///     <see cref="IValueConverter" />. Bound to a UTC <c>DateTime</c>,
///     returns "5m ago" / "2h ago" / "Mar 5".
/// </summary>
public sealed class TimeAgoConverter : IValueConverter
{
    public static readonly TimeAgoConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTime dt => StatusMappers.TimeAgo(dt.ToUniversalTime()),
            DateTimeOffset dto => StatusMappers.TimeAgo(dto.UtcDateTime),
            _ => string.Empty
        };
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.TokensToCompact" /> as a WPF
///     <see cref="IValueConverter" />.
/// </summary>
public sealed class TokensToCompactConverter : IValueConverter
{
    public static readonly TokensToCompactConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            long l => StatusMappers.TokensToCompact(l),
            int i => StatusMappers.TokensToCompact(i),
            _ => "0"
        };
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.CostToUsd" /> as a WPF
///     <see cref="IValueConverter" />.
/// </summary>
public sealed class CostToUsdConverter : IValueConverter
{
    public static readonly CostToUsdConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            decimal d => StatusMappers.CostToUsd(d),
            double dd => StatusMappers.CostToUsd((decimal)dd),
            _ => "$0.0000"
        };
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
