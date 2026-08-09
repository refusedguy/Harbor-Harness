using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Harbor.App.Avalonia.Views.Components;

/// <summary>
///     SegmentedControl — radio-pill button group with VM-bound Items + SelectedItem.
///     Usage in XAML:
///       &lt;views:SegmentedControl Items="One,Two,Three" SelectedItem="{Binding CurrentSegment}" /&gt;
/// </summary>
public partial class SegmentedControl : UserControl
{
    /// <summary>The items displayed as segments (strings, view-models, etc.).</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<SegmentedControl, IEnumerable?>(
            nameof(Items),
            defaultValue: new[] { "One", "Two", "Three" });

    /// <summary>The currently selected item.</summary>
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SegmentedControl, object?>(nameof(SelectedItem));

    public SegmentedControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }
}
