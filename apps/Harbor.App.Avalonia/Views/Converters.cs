using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Converters;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Resolves a resource-key string (e.g. "ChatUserBrush") to the registered
///     <see cref="IBrush" />. Used by the chat template + status bar.
/// </summary>
public sealed class BrushKeyConverter : OneWayConverterBase<BrushKeyConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return null;

        // Avalonia 12: TryGetResource searches merged dictionaries too.
        // Direct indexer (Resources[key]) only checks the top-level dictionary.
        //
        // #948: this converter used to run TryGetResource and then fall back to
        // the indexer. The fallback was dead weight — a miss there is a plain
        // null, so it could never return anything the TryGetResource above had
        // not already found. Both branches now live in ThemeBrushResolver.
        return ThemeBrushResolver.Resolve(key);
    }
}

/// <summary>
///     Returns true when the bound value equals the converter parameter. Used to
///     toggle visibility of the chat/code views based on <c>ActiveView</c>.
/// </summary>
/// <remarks>
///     Not a <see cref="OneWayConverterBase{T}" />: this converter is
///     two-way. When the bound control (e.g. RadioButton.IsChecked) becomes
///     true, the parameter is pushed back to the source, so a radio-button
///     group can drive the bound view.
/// </remarks>
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
public sealed class InequalityConverter : OneWayConverterBase<InequalityConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !Equals(value?.ToString(), parameter?.ToString());
}

/// <summary>
///     Returns "Finish" when the bound numeric step equals <c>TotalSteps</c>
///     (5), otherwise "Next". Used for the onboarding wizard's nav button.
/// </summary>
public sealed class FinishLabelConverter : OneWayConverterBase<FinishLabelConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        const int totalSteps = 5;
        return value is int step && step >= totalSteps ? "Finish" : "Next";
    }
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
public sealed class StepToStepperBrushConverter : OneWayConverterBase<StepToStepperBrushConverter>
{
    /// <summary>
    ///     The resource key that paints the stepper dot at
    ///     <paramref name="dotStep" /> when the wizard is at
    ///     <paramref name="currentStep" />.
    /// </summary>
    /// <remarks>
    ///     Static so the mapping can be asserted with no Avalonia
    ///     <c>Application</c> standing up: the key is the part worth
    ///     asserting, and the resource plumbing around it is not.
    /// </remarks>
    public static string BrushKeyFor(int currentStep, int dotStep) =>
        currentStep switch
        {
            _ when currentStep > dotStep => "StepperDoneBrush",
            _ when currentStep == dotStep => "StepperActiveBrush",
            _ => "StepperPendingBrush"
        };

    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int currentStep) return null;
        if (!int.TryParse(parameter?.ToString(), out int dotStep)) return null;

        return ThemeBrushResolver.Resolve(BrushKeyFor(currentStep, dotStep));
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.StatusToBrushKey" /> as an Avalonia
///     <see cref="IValueConverter" />. Resolves the returned resource key to
///     an <see cref="IBrush" /> via <see cref="BrushKeyConverter" /> so the
///     status-bar accent can be bound directly to a status string.
/// </summary>
public sealed class StatusTextToBrushConverter : OneWayConverterBase<StatusTextToBrushConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = StatusMappers.StatusToBrushKey(value?.ToString());
        return BrushKeyConverter.Instance.Convert(key, targetType, parameter, culture);
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.ToolCallStateToBrushKey" /> as an
///     Avalonia <see cref="IValueConverter" />. Bound to a
///     <see cref="ToolCallState" /> enum value, returns the matching
///     <see cref="IBrush" /> from app resources.
/// </summary>
public sealed class ToolCallStateToBrushConverter : OneWayConverterBase<ToolCallStateToBrushConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ToolCallState state) return null;
        string key = StatusMappers.ToolCallStateToBrushKey(state);
        return BrushKeyConverter.Instance.Convert(key, targetType, parameter, culture);
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.SessionStatusToText" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a <c>SessionStatus</c> enum
///     value, returns the short display label.
/// </summary>
public sealed class SessionStatusToTextConverter : OneWayConverterBase<SessionStatusToTextConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionStatus status) return null;
        return StatusMappers.SessionStatusToText(status);
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.SessionStatusToBrushKey" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a <c>SessionStatus</c> enum
///     value, returns the matching <see cref="IBrush" /> for the session list
///     row's status dot.
/// </summary>
public sealed class SessionStatusToBrushConverter : OneWayConverterBase<SessionStatusToBrushConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SessionStatus status) return null;
        string key = StatusMappers.SessionStatusToBrushKey(status);
        return BrushKeyConverter.Instance.Convert(key, targetType, parameter, culture);
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.TimeAgo" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a UTC <c>DateTime</c>,
///     returns "5m ago" / "2h ago" / "Mar 5".
/// </summary>
public sealed class TimeAgoConverter : OneWayConverterBase<TimeAgoConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTime dt => StatusMappers.TimeAgo(dt.ToUniversalTime()),
            DateTimeOffset dto => StatusMappers.TimeAgo(dto.UtcDateTime),
            _ => string.Empty
        };
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.TokensToCompact" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a long token count,
///     returns "1.2K" / "12K" / "1.4M".
/// </summary>
public sealed class TokensToCompactConverter : OneWayConverterBase<TokensToCompactConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            long l => StatusMappers.TokensToCompact(l),
            int i => StatusMappers.TokensToCompact(i),
            _ => "0"
        };
    }
}

/// <summary>
///     Wraps <see cref="StatusMappers.CostToUsd" /> as an Avalonia
///     <see cref="IValueConverter" />. Bound to a decimal cost,
///     returns "$0.0123".
/// </summary>
public sealed class CostToUsdConverter : OneWayConverterBase<CostToUsdConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            decimal d => StatusMappers.CostToUsd(d),
            double dd => StatusMappers.CostToUsd((decimal)dd),
            _ => "$0.0000"
        };
    }
}

/// <summary>
///     Inverts a boolean. Used for <c>IsVisible</c> bindings where the
///     source flag is "is hidden" but the view needs "is visible".
/// </summary>
/// <remarks>
///     Not a <see cref="OneWayConverterBase{T}" />: inverting is symmetric, so
///     this converter has a real <c>ConvertBack</c> — it inverts again.
/// </remarks>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <summary>Singleton instance.</summary>
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
public sealed class StringNullOrEmptyToBoolConverter : OneWayConverterBase<StringNullOrEmptyToBoolConverter>
{
    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.IsNullOrEmpty(value?.ToString());
}
