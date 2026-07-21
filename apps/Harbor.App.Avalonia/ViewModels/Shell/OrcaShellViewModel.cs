using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Harbor.App.Avalonia.ViewModels.Shell;
/// <summary>
///     Root view-model for the experimental Orca shell.
/// </summary>
/// <remarks>
///     <para>
///         Wraps the shared <see cref="MainViewModel" /> (so chat, code editor,
///         session list, command palette etc. all keep working) and adds the
///         Orca-specific projections: the <see cref="Sessions" /> rail VM, the
///         local <see cref="ShellState" />, and the
///         <see cref="ActiveSessionTitle" /> / <see cref="ActiveModel" /> /
///         <see cref="ActiveWorkdir" /> derived properties shown in the
///         session header bar.
///     </para>
///     <para>
///         <b>TEA boundary:</b> the only state added on top of
///         <see cref="MainViewModel" /> is <see cref="ShellState" /> (layout
///         chrome — rail width, right panel, mode) and the projected rail VM.
///         All chat/session/agent state still flows through the shared
///         <c>UiStore</c> + <c>TuiEffectHost</c>.
///     </para>
/// </remarks>
public sealed partial class OrcaShellViewModel : ObservableObject, IDisposable
{
    private readonly IDispatcherAdapter _dispatcher;

    /// <summary>Model label shown in the session header bar.</summary>
    [ObservableProperty]
    private string _activeModel = "—";

    /// <summary>Title shown in the session header bar.</summary>
    [ObservableProperty]
    private string _activeSessionTitle = "New session";

    /// <summary>Workdir label shown in the session header bar.</summary>
    [ObservableProperty]
    private string _activeWorkdir = string.Empty;
    private bool _disposed;

    /// <summary>True when the chat tab is the active main mode.</summary>
    [ObservableProperty]
    private bool _isChatMode = true;

    /// <summary>True when the code editor tab is the active main mode.</summary>
    [ObservableProperty]
    private bool _isCodeMode;

    /// <summary>Construct the Orca shell VM wrapping <paramref name="main" />.</summary>
    public OrcaShellViewModel(MainViewModel main, IDispatcherAdapter dispatcher)
    {
        Main = main;
        _dispatcher = dispatcher;
        ShellState = new ShellState();
        Sessions = new LeftRailViewModel(main.Sessions, _dispatcher);
        Chat = main.Chat;
        CodeEditor = main.CodeEditor;

        // Listen to MainViewModel property changes so the session header bar
        // (ActiveSessionTitle / ActiveModel / ActiveWorkdir) tracks the active
        // session + model label. We also subscribe to Sessions.ActiveSession
        // changes via the LeftRailViewModel.
        main.PropertyChanged += OnMainPropertyChanged;
        Sessions.PropertyChanged += OnSessionsPropertyChanged;
        // ShellState.PropertyChanged → forward mode changes (Chat/Code radio
        // buttons in the session header) to the underlying MainViewModel so
        // the ChatView/CodeEditorView visibility follows. Without this, clicking
        // the "Code" radio button only updates ShellState.ActiveMode but
        // MainViewModel.ActiveView (which ChatView/CodeEditorView bind to via
        // IsChatMode/IsCodeMode) stays "chat" and the code editor never shows.
        ShellState.PropertyChanged += OnShellStateChanged;
        // Force initial projection.
        Sessions.ReprojectAll();
        UpdateActiveHeader();
    }

    /// <summary>Local shell state (rail width, right panel, mode).</summary>
    public ShellState ShellState { get; }

    /// <summary>Left-rail view-model (dense session rows).</summary>
    public LeftRailViewModel Sessions { get; }

    /// <summary>Chat view-model (reused from MainViewModel).</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Code editor view-model (reused from MainViewModel).</summary>
    public CodeEditorViewModel CodeEditor { get; }

    /// <summary>
    ///     The wrapped <see cref="MainViewModel" />. Exposed so the Orca
    ///     <c>StatusBarView</c> + modal overlays can bind to the same status /
    ///     command-palette / settings VMs the classic shell uses.
    /// </summary>
    public MainViewModel Main
    {
        get;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Main.PropertyChanged -= OnMainPropertyChanged;
        Sessions.PropertyChanged -= OnSessionsPropertyChanged;
        ShellState.PropertyChanged -= OnShellStateChanged;
        Sessions.Dispose();
    }

    /// <summary>Toggle sidebar visibility (Ctrl+B) — delegates to MainViewModel.</summary>
    [RelayCommand]
    private void ToggleSidebar() => ShellState.LeftRailCollapsed = !ShellState.LeftRailCollapsed;

    /// <summary>Switch the main mode (Chat | Code).</summary>
    /// <param name="mode">Mode name.</param>
    [RelayCommand]
    private void SwitchMode(string mode)
    {
        ShellState.ActiveMode = mode;
        IsChatMode = string.Equals(mode, "Chat", StringComparison.OrdinalIgnoreCase);
        IsCodeMode = string.Equals(mode, "Code", StringComparison.OrdinalIgnoreCase);
        // Forward to the underlying MainViewModel so the ChatView/CodeEditorView
        // visibility (which still binds to MainViewModel.ActiveView) stays in sync.
        Main.SwitchViewCommand.Execute(IsCodeMode ? "code" : "chat");
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainViewModel.ModelLabel), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.ActiveView), StringComparison.Ordinal))
        {
            _dispatcher.Post(UpdateActiveHeader);
        }
    }

    private void OnSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(LeftRailViewModel.ActiveSession), StringComparison.Ordinal))
        {
            _dispatcher.Post(UpdateActiveHeader);
        }
    }

    private void OnShellStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ShellState.ActiveMode), StringComparison.Ordinal))
        {
            // Forward the Chat/Code toggle to the underlying MainViewModel so
            // the ChatView/CodeEditorView IsVisible bindings (which point at
            // IsChatMode/IsCodeMode on this VM) flip in sync.
            _dispatcher.Post(() =>
            {
                string mode = ShellState.ActiveMode;
                bool isCode = string.Equals(mode, "Code", StringComparison.OrdinalIgnoreCase);
                IsChatMode = !isCode;
                IsCodeMode = isCode;
                Main.SwitchViewCommand.Execute(isCode ? "code" : "chat");
            });
        }
    }

    /// <summary>
    ///     Refresh <see cref="ActiveSessionTitle" />, <see cref="ActiveModel" />,
    ///     <see cref="ActiveWorkdir" /> from the currently active session + the
    ///     MainViewModel's model label.
    /// </summary>
    public void UpdateActiveHeader()
    {
        var activeRow = Sessions.ActiveSession;
        ActiveSessionTitle = activeRow?.Title ?? "New session";
        ActiveModel = !string.IsNullOrEmpty(Main.ModelLabel) ? Main.ModelLabel
            : activeRow?.ModelName ?? "—";
        ActiveWorkdir = activeRow?.Workdir ?? string.Empty;

        // Keep IsChatMode/IsCodeMode in sync with MainViewModel.ActiveView.
        bool isCode = string.Equals(Main.ActiveView, "code", StringComparison.OrdinalIgnoreCase);
        IsChatMode = !isCode;
        IsCodeMode = isCode;
        ShellState.ActiveMode = isCode ? "Code" : "Chat";
    }
}
