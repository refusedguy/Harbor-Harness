using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Converters;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Resolves a resource-key string (e.g. "ChatUserBrush") to the registered
///     <see cref="IBrush" />. Used by the chat template + status bar.
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

        // Avalonia 12: TryGetResource searches merged dictionaries too.
        // Direct indexer (Resources[key]) only checks the top-level dictionary.
        if (Application.Current.TryGetResource(key, null, out object? resource) && resource is IBrush)
            return (IBrush)resource;

        // Fallback: direct indexer
        return Application.Current.Resources[key] as IBrush;
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
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());

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
        return BindingOperations.DoNothing;
    }
}

/// <summary>
///     Inverse of <see cref="EqualityConverter" />: returns true when the bound
///     value does NOT equal the parameter. Used for "Back" button visibility
///     (visible when CurrentStep != 1).
/// </summary>
public sealed class InequalityConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly InequalityConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !Equals(value?.ToString(), parameter?.ToString());

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Returns "Finish" when the bound numeric step equals <c>TotalSteps</c>
///     (5), otherwise "Next". Used for the onboarding wizard's nav button.
/// </summary>
public sealed class FinishLabelConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly FinishLabelConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        const int totalSteps = 5;
        return value is int step && step >= totalSteps ? "Finish" : "Next";
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Resolves a brush for the onboarding progress stepper dot at position
///     <c>ConverterParameter</c> based on the bound <c>CurrentStep</c> value:
///     <list type="bullet">
///         <item>step &lt; parameter → <c>StepperPendingBrush</c> (not yet reached)</item>
///         <item>step == parameter → <c>StepperActiveBrush</c> (current step, highlighted)</item>
///         <item>step &gt; parameter → <c>StepperDoneBrush</c> (completed)</item>
///     </list>
///     Returns a brush (resolved from app resources) directly, so the
///     Ellipse can bind <c>Fill</c> without needing a second converter.
/// </summary>
public sealed class StepToStepperBrushConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
    public static readonly StepToStepperBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int currentStep) return null;
        if (!int.TryParse(parameter?.ToString(), out int dotStep)) return null;

        string key = currentStep switch
        {
            _ when currentStep > dotStep => "StepperDoneBrush",
            _ when currentStep == dotStep => "StepperActiveBrush",
            _ => "StepperPendingBrush"
        };
        return Application.Current?.Resources[key] as IBrush;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.StatusToBrushKey" /> as an Avalonia
///     <see cref="IValueConverter" />. Resolves the returned resource key to
///     an <see cref="IBrush" /> via <see cref="BrushKeyConverter" /> so the
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
///     Wraps <see cref="StatusMappers.ToolCallStatusToBrushKey" /> as an
///     Avalonia <see cref="IValueConverter" />. Bound to a
///     <see cref="ToolCallStatus" /> enum value, returns the matching
///     <see cref="IBrush" /> from app resources.
/// </summary>
public sealed class ToolCallStatusToBrushConverter : IValueConverter
{
    public static readonly ToolCallStatusToBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ToolCallStatus status) return null;
        string key = StatusMappers.ToolCallStatusToBrushKey(status);
        return BrushKeyConverter.Instance.Convert(key, targetType, parameter, culture);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.SessionStatusToText" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a <c>SessionStatus</c> enum
///     value, returns the short display label.
/// </summary>
public sealed class SessionStatusToTextConverter : IValueConverter
{
    public static readonly SessionStatusToTextConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionStatus status) return null;
        return StatusMappers.SessionStatusToText(status);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.SessionStatusToBrushKey" /> as an
///     Avalonia <see cref="IValueConverter" />. Bound to a
///     <c>SessionStatus</c> enum value, returns the matching
///     <see cref="IBrush" /> for the session list row's status dot.
/// </summary>
public sealed class SessionStatusToBrushConverter : IValueConverter
{
    public static readonly SessionStatusToBrushConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionStatus status) return null;
        string key = StatusMappers.SessionStatusToBrushKey(status);
        return BrushKeyConverter.Instance.Convert(key, targetType, parameter, culture);
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Wraps <see cref="StatusMappers.TimeAgo" /> as an Avalonia
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
///     Wraps <see cref="StatusMappers.TokensToCompact" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a long token count,
///     returns "1.2K" / "12K" / "1.4M".
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
///     Wraps <see cref="StatusMappers.CostToUsd" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a decimal cost,
///     returns "$0.0123".
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

/// <summary>
///     Inverts a boolean. Used for <c>IsVisible</c> bindings where the
///     source flag is "is hidden" but the view needs "is visible".
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}

/// <summary>
///     Returns <c>false</c> for null/empty strings, <c>true</c> otherwise.
///     Used for placeholder visibility ("no messages yet" only shown when
///     transcript is empty).
/// </summary>
public sealed class StringNullOrEmptyToBoolConverter : IValueConverter
{
    public static readonly StringNullOrEmptyToBoolConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.IsNullOrEmpty(value?.ToString());

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
