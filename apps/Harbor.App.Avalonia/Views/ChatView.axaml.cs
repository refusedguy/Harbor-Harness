using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;
namespace Harbor.App.Avalonia.Views;
/// <summary>
///     Chat view code-behind. Forwards Enter / Ctrl+Enter from the input box to the
///     <see cref="ChatViewModel.SendCommand" />.
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
    public static readonly StyledProperty<bool> ShowInputAreaProperty =
        AvaloniaProperty.Register<ChatView, bool>(
            nameof(ShowInputArea),
            true);

    public ChatView()
    {
        InitializeComponent();
    }

    public bool ShowInputArea
    {
        get => this.GetValue(ShowInputAreaProperty);
        set => this.SetValue(ShowInputAreaProperty, value);
    }

    private ChatViewModel? Vm => this.DataContext as ChatViewModel;

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        bool isPlainEnter = e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool isCtrlEnter = e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (isPlainEnter || isCtrlEnter)
        {
            e.Handled = true;

            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                InputBox.Focus();
            }
        }
    }

    private const double MaxPullDistance = 120.0;
    private const double PullThreshold = 0.5;
    private const int ReleaseDelayMs = 300;
    private bool _isPulling;
    private double _pullDistance;
    private DispatcherTimer? _releaseTimer;

    private void ChatScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (ChatScrollViewer is not { } scrollViewer) return;

        bool atTop = scrollViewer.Offset.Y <= 0;
        bool scrollingUp = e.Delta.Y < 0;

        if (atTop && scrollingUp)
        {
            e.Handled = true;

            if (!_isPulling)
            {
                _isPulling = true;
                vm.ShowPullIndicator = true;
            }

            _pullDistance = Math.Min(MaxPullDistance, _pullDistance + Math.Abs(e.Delta.Y) * 0.8);
            vm.PullOffset = _pullDistance;
            vm.PullProgress = _pullDistance / MaxPullDistance;
            vm.ContentScale = 1.0 - (vm.PullProgress * 0.05);

            _releaseTimer?.Stop();
            _releaseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(ReleaseDelayMs), DispatcherPriority.Background, (s, _) => OnPullReleased());
            _releaseTimer.Start();
        }
        else if (_isPulling)
        {
            CancelPull();
        }
    }

    private void OnPullReleased()
    {
        _releaseTimer?.Stop();
        _releaseTimer = null;

        if (Vm is not { } vm) return;
        _isPulling = false;

        if (vm.PullProgress >= PullThreshold && !vm.IsLoadingHistory && vm.CanLoadOlder)
        {
            _ = AnimateSnapBackAndLoadAsync();
        }
        else
        {
            _ = AnimateSnapBackAsync();
        }
    }

    private async Task AnimateSnapBackAsync()
    {
        if (Vm is not { } vm) return;

        const int frameDelay = 16;
        const double snapSpeed = 10.0;

        while (vm.PullOffset > 0.5)
        {
            vm.PullOffset = Math.Max(0, vm.PullOffset - snapSpeed);
            vm.PullProgress = vm.PullOffset / MaxPullDistance;
            vm.ContentScale = 1.0 - (vm.PullProgress * 0.05);
            await Task.Delay(frameDelay);
        }

        vm.PullOffset = 0;
        vm.PullProgress = 0;
        vm.ContentScale = 1.0;
        vm.ShowPullIndicator = false;
        _pullDistance = 0;
    }

    private async Task AnimateSnapBackAndLoadAsync()
    {
        if (Vm is not { } vm) return;

        await AnimateSnapBackAsync();
        if (vm.LoadOlderMessagesCommand.CanExecute(null))
        {
            vm.LoadOlderMessagesCommand.Execute(null);
        }

        if (ChatScrollViewer is { } scrollViewer)
        {
            scrollViewer.ScrollToHome();
        }
    }

    private void CancelPull()
    {
        _releaseTimer?.Stop();
        _releaseTimer = null;
        _isPulling = false;
        _pullDistance = 0;

        if (Vm is { } vm)
        {
            vm.PullOffset = 0;
            vm.PullProgress = 0;
            vm.ContentScale = 1.0;
            vm.ShowPullIndicator = false;
        }
    }
}
