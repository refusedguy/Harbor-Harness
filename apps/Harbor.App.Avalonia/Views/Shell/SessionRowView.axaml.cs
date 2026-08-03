using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.ViewModels.Shell;
namespace Harbor.App.Avalonia.Views.Shell;
/// <summary>
///     Orca session row — code-behind. Handles double-click → Rename and
///     hover → reveal delete button. Right-click ContextMenu is declared in XAML.
/// </summary>
public partial class SessionRowView : UserControl
{
    /// <summary>Construct the row template.</summary>
    public SessionRowView()
    {
        InitializeComponent();
    }

    /// <summary>Double-click a session row to invoke Rename via the parent LeftRailViewModel.</summary>
    private void RowBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not Border border) return;
        var row = border.DataContext as SessionRowViewModel;
        if (row is null) return;

        if (border.FindAncestorOfType<ListBox>() is { } listBox &&
            listBox.DataContext is LeftRailViewModel railVm)
        {
            _ = railVm.RenameSessionCommand.ExecuteAsync(row);
        }
    }

    /// <summary>Reveal the × button on hover.</summary>
    private void RowBorder_OnPointerEntered(object? sender, PointerEventArgs e) =>
        DeleteButton!.Opacity = 1;

    /// <summary>Hide the × button when pointer leaves.</summary>
    private void RowBorder_OnPointerExited(object? sender, PointerEventArgs e) =>
        DeleteButton!.Opacity = 0;
}
