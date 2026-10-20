// OneWayConverterBase.cs — #1539: the one place a converter's singleton and
// its one-way contract live.
//
// Why this exists: twenty converter classes in this app repeated the same two
// pieces of boilerplate — a `public static readonly X Instance = new();`
// singleton and a `ConvertBack` that threw `NotSupportedException`. The
// singleton moves here, so a converter class is left with the one method that
// is actually its own: `Convert`.
//
// The three converters that are NOT on this base are not oversights:
//   * EqualityConverter and InverseBoolConverter have a real ConvertBack
//     (one pushes the parameter back to a radio-button source, the other
//     inverts symmetrically), so they are two-way and implement IValueConverter
//     directly.
//   * BoolToWidthConverter carries the rail's expanded/collapsed widths as
//     instance state set from XAML, so it cannot be a shared singleton.

using System.Globalization;
using Avalonia.Data.Converters;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Base for the app's one-way value converters: the ones that read a bound
///     value and produce a display value, and are never written back.
/// </summary>
/// <typeparam name="TSelf">
///     The concrete converter type. It is the type argument so that
///     <see cref="Instance" /> is typed as the converter itself and so that
///     every closed generic type gets its own single instance.
/// </typeparam>
/// <remarks>
///     <para>
///         <see cref="ConvertBack" /> is deliberately not
///         <see langword="virtual" />: a converter that needs a real two-way
///         conversion is not a one-way converter, and it should implement
///         <see cref="IValueConverter" /> directly rather than inherit a base
///         that promises the wrong thing.
///     </para>
///     <para>
///         <b>Why <see cref="Instance" /> is a property and not a field:</b>
///         the XAML compiler resolves <c>{x:Static views:MyConverter.Instance}</c>
///         by looking for a static member on the named type and then walking the
///         base types for a <em>property</em>. An inherited static field is not
///         found that way; an inherited static property is.
///     </para>
/// </remarks>
public abstract class OneWayConverterBase<TSelf> : IValueConverter
    where TSelf : OneWayConverterBase<TSelf>, new()
{
    /// <summary>
    ///     The single instance of this converter, shared by every binding that
    ///     names it. A converter holds no per-binding state, so there is
    ///     nothing for a second instance to add.
    /// </summary>
    public static TSelf Instance { get; } = new();

    /// <inheritdoc />
    public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
