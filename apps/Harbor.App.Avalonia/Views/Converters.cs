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
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Two-way mode: when the bound control (e.g. RadioButton.IsChecked)
        // becomes true, push the parameter back to the source. When it becomes
        // false, return Binding.DoNothing so the source is left untouched —
        // this prevents radio-button groups from racing each other.
        if (value is bool b && b)
        {
            return parameter?.ToString();
        }
        return global::Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
///     Inverse of <see cref="EqualityConverter"/>: returns true when the bound
///     value does NOT equal the parameter. Used for "Back" button visibility
///     (visible when CurrentStep != 1).
/// </summary>
public sealed class InequalityConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly InequalityConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !Equals(value?.ToString(), parameter?.ToString());
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Returns "Finish ✓" when the bound numeric step equals <c>TotalSteps</c>
///     (5), otherwise "Next →". Used for the onboarding wizard's nav button.
/// </summary>
public sealed class FinishLabelConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly FinishLabelConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        const int totalSteps = 5;
        return value is int step && step >= totalSteps ? "Finish ✓" : "Next →";
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
