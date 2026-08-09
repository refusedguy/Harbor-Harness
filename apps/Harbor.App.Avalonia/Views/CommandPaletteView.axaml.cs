using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Command palette view code-behind. Handles keyboard navigation
///     (↑/↓ to move, Enter to invoke) and focuses the query box on load.
///     Escape is handled by <see cref="KeyboardShortcutService" /> via
///     <see cref="IOverlayStack" /> so the palette stays view-agnostic.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
    }

    private CommandPaletteViewModel? Vm => this.DataContext as CommandPaletteViewModel;

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
        if (ResolveMainViewModel() is { } main)
            main.OverlayPopCommand.Execute(null);
    }

    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) || ReferenceEquals(e.Source, this))
        {
            ClosePalette();
        }
    }

    private MainViewModel? ResolveMainViewModel()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            return window.DataContext as MainViewModel;
        }

        return null;
    }
}
