using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Harbor.App.Avalonia.ViewModels.Terminal;

namespace Harbor.App.Avalonia.Views.Terminal;

/// <summary>
///     Floating PTY terminal window: 1–2 panes arranged single / side-by-side
///     / stacked. The layout is rebuilt imperatively from the view-model state
///     — panes keep their own PTY, history and working directory.
/// </summary>
public partial class FloatingTerminalPaneWindow : Window
{
    public FloatingTerminalPaneWindow()
    {
        InitializeComponent();
        Closed += (_, _) => DisposePanes();
    }

    private FloatingTerminalViewModel? Vm => DataContext as FloatingTerminalViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Panes.CollectionChanged += OnPanesChanged;
        }

        RebuildLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingTerminalViewModel.Layout))
        {
            RebuildLayout();
        }
    }

    private void OnPanesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildLayout();

    private void RebuildLayout()
    {
        FloatingTerminalViewModel? vm = Vm;
        if (vm is null) return;

        PaneGrid.Children.Clear();
        PaneGrid.RowDefinitions.Clear();
        PaneGrid.ColumnDefinitions.Clear();
        EmptyState.IsVisible = vm.Panes.Count == 0;
        if (vm.Panes.Count == 0) return;

        TerminalPaneViewModel first = vm.Panes[0];
        if (vm.Layout == TerminalLayout.Single || vm.Panes.Count == 1)
        {
            PaneGrid.Children.Add(MakePane(first, 0, 0));
            return;
        }

        TerminalPaneViewModel second = vm.Panes[1];
        if (vm.Layout == TerminalLayout.SplitHorizontal)
        {
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PaneGrid.Children.Add(MakePane(first, 0, 0));
            PaneGrid.Children.Add(MakePane(second, 0, 1));
            return;
        }

        // SplitVertical — stacked top/bottom.
        PaneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        PaneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        PaneGrid.Children.Add(MakePane(first, 0, 0));
        PaneGrid.Children.Add(MakePane(second, 1, 0));
    }

    private static TerminalPaneControl MakePane(TerminalPaneViewModel vm, int row, int column)
    {
        return new TerminalPaneControl
        {
            DataContext = vm,
            [Grid.RowProperty] = row,
            [Grid.ColumnProperty] = column,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    private void DisposePanes()
    {
        if (Vm is not { } vm) return;
        foreach (TerminalPaneViewModel pane in vm.Panes)
        {
            pane.Dispose();
        }
    }
}
