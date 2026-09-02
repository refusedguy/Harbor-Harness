using System.Windows;
using System.Windows.Input;
using Harbor.App.Wpf.ViewModels;
namespace Harbor.App.Wpf.Views;
/// <summary>
///     Ctrl+P command palette popup.
/// </summary>
public partial class CommandPaletteView : Window
{
    /// <summary>Construct a <see cref="CommandPaletteView" />.</summary>
    public CommandPaletteView()
    {
        InitializeComponent();
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is CommandPaletteViewModel vm)
        {
            vm.CommandInvoked += OnCommandInvoked;
        }
    }

    private void OnCommandInvoked(string obj) => this.Close();

    private void QueryBox_Loaded(object sender, RoutedEventArgs e) => QueryBox.Focus();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (this.DataContext is not CommandPaletteViewModel vm) return;
        switch (e.Key)
        {
            case Key.Escape:
                this.Close();
                e.Handled = true;
                break;
            case Key.Down:
                vm.MoveDownCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.InvokeSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
