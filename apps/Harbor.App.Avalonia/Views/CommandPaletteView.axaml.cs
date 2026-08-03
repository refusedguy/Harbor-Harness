using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.ViewModels.Shell;
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
        this.AttachedToVisualTree += (_, _) => QueryBox.Focus();
    }

    private CommandPaletteViewModel? Vm => this.DataContext as CommandPaletteViewModel;

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
            case Key.Escape:
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
            main.IsCommandPaletteOpen = false;
    }

    /// <summary>
    ///     Click on the backdrop (the semi-transparent UserControl background)
    ///     closes the palette — same behaviour as Esc. The inner Border with
    ///     the actual palette card has a Background set so its clicks don't
    ///     bubble up to the UserControl.
    /// </summary>
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
            return window.DataContext switch
            {
                MainViewModel vm => vm,
                OrcaShellViewModel orca => orca.Main,
                _ => null
            };
        }

        return null;
    }
}
