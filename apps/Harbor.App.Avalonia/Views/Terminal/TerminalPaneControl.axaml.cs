using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.App.Avalonia.ViewModels.Terminal;

namespace Harbor.App.Avalonia.Views.Terminal;

/// <summary>
///     One PTY terminal pane: read-only monospace output history plus a
///     one-line input box wired to Enter / Up / Down. Auto-scrolls to the
///     bottom whenever new output lands.
/// </summary>
public partial class TerminalPaneControl : UserControl
{
    public TerminalPaneControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            if (Vm is { } vm)
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
            }
        };
    }

    private TerminalPaneViewModel? Vm => DataContext as TerminalPaneViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            ScrollToBottom();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalPaneViewModel.OutputText))
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom()
    {
        // Avalonia's TextBox has no ScrollToEnd; moving the caret to the end scrolls it into view.
        OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                Vm.Submit();
                e.Handled = true;
                break;
            case Key.Up:
                Vm.HistoryPrevious();
                e.Handled = true;
                break;
            case Key.Down:
                Vm.HistoryNext();
                e.Handled = true;
                break;
        }
    }
}
