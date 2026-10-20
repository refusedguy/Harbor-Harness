// OneWayConverterBaseTests.cs — issue #1539.
//
// WHAT THIS FILE IS FOR
// --------------------
// #1539 said that twenty converter classes repeated the same two pieces of
// boilerplate: a `public static readonly X Instance = new();` singleton and a
// `ConvertBack` that threw `NotSupportedException`. The fix moved both into
// `OneWayConverterBase<TSelf>`, so a converter class is left with `Convert`.
//
// Boilerplate that comes back is invisible: a new converter that declares its
// own `Instance` and its own throwing `ConvertBack` compiles, works, and puts
// the duplication back. So this file holds the shape down:
//
//   * every converter in the app is either on the one-way base, or is one of
//     the three named exceptions, each with the reason it cannot be on it;
//   * every one-way converter really does share ONE instance — a second
//     instance per binding is the allocation the base exists to remove;
//   * a one-way converter really does refuse to convert back;
//   * the two-way converters still convert back, and the stateful one still
//     keeps its own state.
//
// The XAML half of the same contract — that no view declares a converter
// instance of its own — is a source scan over the app, and it lives in
// tests/Harbor.Architecture.Tests/AvaloniaConverterInstanceRules.cs, next to
// the other guards over apps/Harbor.App.Avalonia.

using System.Globalization;
using System.Reflection;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Shell;
using Harbor.Ui.Framework.Services;
using TUnit.Assertions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #1539 — the Avalonia converters: one shared instance each, one way
///     only, and none of them re-declared per view.
/// </summary>
public class OneWayConverterBaseTests
{
    /// <summary>
    ///     The converters that are deliberately NOT on
    ///     <see cref="OneWayConverterBase{TSelf}" />, each with the reason.
    ///     A converter that is not on the base and not named here is a
    ///     duplication that came back.
    /// </summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["Harbor.App.Avalonia.Views.EqualityConverter"] =
            "two-way: ConvertBack pushes the parameter back to the source (radio buttons)",
        ["Harbor.App.Avalonia.Views.InverseBoolConverter"] =
            "two-way: ConvertBack inverts again",
        ["Harbor.App.Avalonia.Views.Shell.BoolToWidthConverter"] =
            "stateful: the rail's expanded/collapsed widths are set per instance from XAML",
    };

    private static readonly Assembly AppAssembly = typeof(BrushKeyConverter).Assembly;

    [Test]
    public async Task Every_Converter_Is_One_Way_Or_A_Named_Exception()
    {
        List<Type> converters = AppAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IValueConverter).IsAssignableFrom(t))
            .ToList();

        await Assert.That(converters).IsNotEmpty()
            .Because("the app has converters; an empty set would make this gate vacuous");

        List<string> unclassified = converters
            .Where(t => !IsOneWay(t))
            .Where(t => !Exceptions.ContainsKey(t.FullName ?? string.Empty))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(unclassified).IsEmpty()
            .Because("a converter must either derive from OneWayConverterBase<T> or be named in "
                + "OneWayConverterBaseTests.Exceptions with the reason it cannot: "
                + string.Join("; ", unclassified));

        // The other direction: an exception that no longer exists is a stale
        // entry, which would let a converter back out of the list unnoticed.
        List<string> stale = Exceptions.Keys
            .Where(fullName => AppAssembly.GetType(fullName) is null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(stale).IsEmpty()
            .Because("these exceptions name a type that no longer exists: " + string.Join("; ", stale));
    }

    [Test]
    public async Task Every_One_Way_Converter_Shares_One_Instance()
    {
        List<Type> converters = OneWayConverterTypes();

        await Assert.That(converters).IsNotEmpty();

        foreach (Type converter in converters)
        {
            PropertyInfo? instance = converter.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            await Assert.That(instance).IsNotNull()
                .Because($"{converter.Name} must expose the singleton the base provides");

            object? first = instance!.GetValue(null);
            object? second = instance.GetValue(null);

            await Assert.That(first).IsNotNull();
            await Assert.That(first).IsSameReferenceAs(second)
                .Because($"{converter.Name}.Instance must be one shared object — a second instance per "
                    + "binding is the allocation the base exists to remove");
            await Assert.That(first!.GetType()).IsEqualTo(converter)
                .Because($"{converter.Name}.Instance must be the converter itself, not the base");
        }
    }

    [Test]
    public async Task Every_One_Way_Converter_Refuses_To_Convert_Back()
    {
        foreach (Type converter in OneWayConverterTypes())
        {
            var instance = (IValueConverter)converter.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!;

            await Assert.That(() => instance.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture))
                .Throws<NotSupportedException>()
                .Because($"{converter.Name} is one-way; the base refuses the write-back");
        }
    }

    [Test]
    public async Task The_Two_Way_Converters_Still_Convert_Back()
    {
        // The radio-button group in the shell: a button that becomes checked
        // pushes its parameter back to the source, one that becomes unchecked
        // leaves the source alone (otherwise the group races itself).
        await Assert.That(EqualityConverter.Instance.ConvertBack(true, typeof(string), "chat", CultureInfo.InvariantCulture))
            .IsEqualTo("chat");
        await Assert.That(EqualityConverter.Instance.ConvertBack(false, typeof(string), "chat", CultureInfo.InvariantCulture))
            .IsSameReferenceAs(BindingOperations.DoNothing);

        // Inverting is symmetric, so this one has a real write-back too.
        await Assert.That((bool?)InverseBoolConverter.Instance.ConvertBack(true, typeof(bool), null, CultureInfo.InvariantCulture))
            .IsFalse();
        await Assert.That((bool?)InverseBoolConverter.Instance.ConvertBack(false, typeof(bool), null, CultureInfo.InvariantCulture))
            .IsTrue();
    }

    [Test]
    public async Task The_Stateful_Converter_Keeps_Its_Own_Widths()
    {
        // The rail's widths come from the XAML that declares the converter, so
        // two instances must not see each other's widths. This is why it is
        // not a singleton.
        var wide = new BoolToWidthConverter { ExpandedWidth = 300, CollapsedWidth = 40 };
        var narrow = new BoolToWidthConverter { ExpandedWidth = 100, CollapsedWidth = 10 };

        await Assert.That(wide.Convert(true, typeof(double), null, CultureInfo.InvariantCulture)).IsEqualTo(300d);
        await Assert.That(wide.Convert(false, typeof(double), null, CultureInfo.InvariantCulture)).IsEqualTo(40d);
        await Assert.That(narrow.Convert(true, typeof(double), null, CultureInfo.InvariantCulture)).IsEqualTo(100d);
        await Assert.That(narrow.Convert(false, typeof(double), null, CultureInfo.InvariantCulture)).IsEqualTo(10d);
    }

    [Test]
    public async Task The_Toast_Brush_Key_Mapping_Is_A_Pure_Function()
    {
        // The mapping is the part worth asserting, and asserting it must not
        // need an Avalonia Application standing up.
        await Assert.That(ToastBrushConverter.BrushKeyFor(ToastKind.Success)).IsEqualTo("StateSuccessBrush");
        await Assert.That(ToastBrushConverter.BrushKeyFor(ToastKind.Warning)).IsEqualTo("StateWarningBrush");
        await Assert.That(ToastBrushConverter.BrushKeyFor(ToastKind.Error)).IsEqualTo("StateErrorBrush");
        await Assert.That(ToastBrushConverter.BrushKeyFor(null)).IsEqualTo("AccentPrimaryBrush");
    }

    [Test]
    public async Task The_Stepper_Brush_Key_Mapping_Is_A_Pure_Function()
    {
        await Assert.That(StepToStepperBrushConverter.BrushKeyFor(currentStep: 3, dotStep: 3)).IsEqualTo("StepperActiveBrush");
        await Assert.That(StepToStepperBrushConverter.BrushKeyFor(currentStep: 4, dotStep: 3)).IsEqualTo("StepperDoneBrush");
        await Assert.That(StepToStepperBrushConverter.BrushKeyFor(currentStep: 2, dotStep: 3)).IsEqualTo("StepperPendingBrush");
    }

    private static List<Type> OneWayConverterTypes() =>
        AppAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && IsOneWay(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    private static bool IsOneWay(Type type)
    {
        Type? baseType = type.BaseType;
        return baseType is { IsGenericType: true }
            && baseType.GetGenericTypeDefinition() == typeof(OneWayConverterBase<>);
    }
}
