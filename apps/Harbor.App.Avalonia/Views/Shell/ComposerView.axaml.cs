using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views.Shell;

/// <summary>
///     Orca composer — code-behind.
/// </summary>
/// <remarks>
///     Forwards Enter (no Shift) from the input box to the
///     <see cref="ChatViewModel.SendCommand"/>. Shift+Enter inserts a newline
///     (the TextBox's <c>AcceptsReturn=True</c> default behaviour).
/// </remarks>
public partial class ComposerView : UserControl
{
    /// <summary>Construct the composer.</summary>
    public ComposerView()
    {
        InitializeComponent();
        Loaded += (_, _) => InputBox.Focus();
    }

    private ChatViewModel? Vm => DataContext as ChatViewModel;

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        // Plain Enter (no Shift, no Ctrl) sends. Shift+Enter inserts newline
        // (default TextBox behaviour). Ctrl+Enter also sends.
        bool isPlainEnter = e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isCtrlEnter = e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (isPlainEnter || isCtrlEnter)
        {
            // Mark handled BEFORE invoking the command — prevents the TextBox
            // from inserting a stray newline character.
            e.Handled = true;

            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                InputBox.Focus();
            }
        }
    }
}
