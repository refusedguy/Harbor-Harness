using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
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
            // #2682 Z01: with a typed query, Up/Down first walk matching query
            // history; only when there is no history to walk do they move the
            // result selection (the behaviour exercised by component tests).
            case Key.Down:
                if (!vm.TryNavigateHistory(up: false))
                {
                    vm.MoveDown();
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (!vm.TryNavigateHistory(up: true))
                {
                    vm.MoveUp();
                }
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
        ShellChrome.CloseTopOverlay();
    }

    private void Backdrop_Click(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) || ReferenceEquals(e.Source, this))
        {
            ClosePalette();
        }
    }

    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= ShellLocator.Of(this).Get<IShellChrome>();
}
