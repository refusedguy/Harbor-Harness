using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Chat view code-behind. Forwards Enter / Ctrl+Enter from the input box to the
///     <see cref="ChatViewModel.SendCommand"/>.
/// </summary>
public partial class ChatView : UserControl
{
    /// <summary>Construct the chat view.</summary>
    public ChatView()
    {
        InitializeComponent();
    }

    private ChatViewModel? Vm => DataContext as ChatViewModel;

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        // Plain Enter (no Shift) sends. Shift+Enter inserts newline (default).
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            vm.SendCommand.Execute(null);
        }
    }
}
