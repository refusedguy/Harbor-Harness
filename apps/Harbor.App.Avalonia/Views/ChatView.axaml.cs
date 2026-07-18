using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views;

/// <summary>
///     Chat view code-behind. Forwards Enter / Ctrl+Enter from the input box to the
///     <see cref="ChatViewModel.SendCommand"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Input fix (Task U2):</b> The plain-Enter send path was fragile because
///         the original handler called <c>vm.SendCommand.Execute(null)</c> without
///         first checking <c>CanExecute</c>, and the TextBox's <c>AcceptsReturn=True</c>
///         meant that any handler exception would leave a stray newline in the input.
///         We now (1) check <c>CanExecute</c>, (2) set <c>e.Handled = true</c> BEFORE
///         invoking the command (so the TextBox never inserts the newline), and
///         (3) re-focus the input after sending so the user can immediately type
///         the next message.
///     </para>
///     <para>
///         The view also auto-focuses the input box on load so the user can start
///         typing immediately — a small ORCA-style polish that makes the chat feel
///         responsive.
///     </para>
///     <para>
///         <b>ShowInputArea (Task F2):</b> styled property that toggles the
///         visibility of the input Border at the bottom of the chat view.
///         Defaults to <c>true</c> (classic MainWindow path). The Orca shell
///         sets it to <c>false</c> so the Orca <c>ComposerView</c> can take
///         over the input role without duplicating the input area.
///     </para>
/// </remarks>
public partial class ChatView : UserControl
{
    /// <summary>
    ///     Defines the <see cref="ShowInputArea"/> styled property.
    ///     Default <c>true</c> so the classic shell keeps its inline input.
    /// </summary>
    public static readonly StyledProperty<bool> ShowInputAreaProperty =
        AvaloniaProperty.Register<ChatView, bool>(
            nameof(ShowInputArea),
            defaultValue: true);

    /// <summary>
    ///     Gets or sets a value indicating whether the chat view's own input
    ///     area is visible. Set <c>false</c> when an external composer (e.g.
    ///     the Orca <c>ComposerView</c>) takes over the input role.
    /// </summary>
    public bool ShowInputArea
    {
        get => GetValue(ShowInputAreaProperty);
        set => SetValue(ShowInputAreaProperty, value);
    }

    /// <summary>Construct the chat view.</summary>
    public ChatView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Focus the input on first load — ORCA pattern. Only focus when
            // the input area is visible (Orca shell hides it; focusing a
            // hidden control is a no-op but spurious).
            if (ShowInputArea)
            {
                InputBox.Focus();
            }
        };
    }

    private ChatViewModel? Vm => DataContext as ChatViewModel;

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        // Plain Enter (no Shift, no Ctrl) sends. Shift+Enter inserts newline (default).
        // Ctrl+Enter also sends (alternative convention).
        bool isPlainEnter = e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isCtrlEnter  = e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (isPlainEnter || isCtrlEnter)
        {
            // Mark handled BEFORE invoking the command — this prevents the
            // TextBox from inserting a newline character. Without this, the
            // newline would race the InputText = string.Empty in Send().
            e.Handled = true;

            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                // Refocus so the user can immediately type the next message.
                InputBox.Focus();
            }
        }
    }
}
