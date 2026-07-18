using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Command palette view code-behind. Handles keyboard navigation
///     (↑/↓ to move, Enter to invoke, Esc to close) and focuses the query box on load.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    /// <summary>Construct the command palette view.</summary>
    public CommandPaletteView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => QueryBox.Focus();
    }

    private CommandPaletteViewModel? Vm => DataContext as CommandPaletteViewModel;

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveDown();
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveUp();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.InvokeSelected();
                ClosePalette();
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    private void QueryBox_KeyDown(object? sender, KeyEventArgs e) => OnKeyDown(e);
    private void ResultsList_KeyDown(object? sender, KeyEventArgs e) => OnKeyDown(e);

    private void ClosePalette()
    {
        if (this.VisualRoot is Window window && window.DataContext is MainViewModel main)
        {
            main.IsCommandPaletteOpen = false;
        }
    }
}
