using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Harbor.App.Avalonia.ViewModels.Shell;

/// <summary>
///     Root view-model for the experimental Orca shell.
/// </summary>
/// <remarks>
///     <para>
///         Wraps the shared <see cref="MainViewModel"/> (so chat, code editor,
///         session list, command palette etc. all keep working) and adds the
///         Orca-specific projections: the <see cref="Sessions"/> rail VM, the
///         local <see cref="ShellState"/>, and the
///         <see cref="ActiveSessionTitle"/> / <see cref="ActiveModel"/> /
///         <see cref="ActiveWorkdir"/> derived properties shown in the
///         session header bar.
///     </para>
///     <para>
///         <b>TEA boundary:</b> the only state added on top of
///         <see cref="MainViewModel"/> is <see cref="ShellState"/> (layout
///         chrome — rail width, right panel, mode) and the projected rail VM.
///         All chat/session/agent state still flows through the shared
///         <c>UiStore</c> + <c>TuiEffectHost</c>.
///     </para>
/// </remarks>
public sealed partial class OrcaShellViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private bool _disposed;

    /// <summary>Construct the Orca shell VM wrapping <paramref name="main"/>.</summary>
    public OrcaShellViewModel(MainViewModel main)
    {
        _main = main;
        ShellState = new AvaloniaShellState();
        Sessions = new LeftRailViewModel(main.Sessions);
        Chat = main.Chat;
        CodeEditor = main.CodeEditor;

        // Listen to MainViewModel property changes so the session header bar
        // (ActiveSessionTitle / ActiveModel / ActiveWorkdir) tracks the active
        // session + model label. We also subscribe to Sessions.ActiveSession
        // changes via the LeftRailViewModel.
        main.PropertyChanged += OnMainPropertyChanged;
        Sessions.PropertyChanged += OnSessionsPropertyChanged;
        // Force initial projection.
        Sessions.ReprojectAll();
        UpdateActiveHeader();
    }

    /// <summary>Local shell state (rail width, right panel, mode).</summary>
    public AvaloniaShellState ShellState { get; }

    /// <summary>Left-rail view-model (dense session rows).</summary>
    public LeftRailViewModel Sessions { get; }

    /// <summary>Chat view-model (reused from MainViewModel).</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Code editor view-model (reused from MainViewModel).</summary>
    public CodeEditorViewModel CodeEditor { get; }

    /// <summary>Title shown in the session header bar.</summary>
    [ObservableProperty]
    private string _activeSessionTitle = "New session";

    /// <summary>Model label shown in the session header bar.</summary>
    [ObservableProperty]
    private string _activeModel = "—";

    /// <summary>Workdir label shown in the session header bar.</summary>
    [ObservableProperty]
    private string _activeWorkdir = string.Empty;

    /// <summary>True when the chat tab is the active main mode.</summary>
    [ObservableProperty]
    private bool _isChatMode = true;

    /// <summary>True when the code editor tab is the active main mode.</summary>
    [ObservableProperty]
    private bool _isCodeMode;

    /// <summary>Toggle sidebar visibility (Ctrl+B) — delegates to MainViewModel.</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        ShellState.LeftRailCollapsed = !ShellState.LeftRailCollapsed;
    }

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
        _main.SwitchViewCommand.Execute(IsCodeMode ? "code" : "chat");
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainViewModel.ModelLabel), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainViewModel.ActiveView), StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(UpdateActiveHeader);
        }
    }

    private void OnSessionsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(LeftRailViewModel.ActiveSession), StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(UpdateActiveHeader);
        }
    }

    /// <summary>
    ///     Refresh <see cref="ActiveSessionTitle"/>, <see cref="ActiveModel"/>,
    ///     <see cref="ActiveWorkdir"/> from the currently active session + the
    ///     MainViewModel's model label.
    /// </summary>
    public void UpdateActiveHeader()
    {
        var activeRow = Sessions.ActiveSession;
        ActiveSessionTitle = activeRow?.Title ?? "New session";
        ActiveModel = !string.IsNullOrEmpty(_main.ModelLabel) ? _main.ModelLabel
            : (activeRow?.ModelName ?? "—");
        ActiveWorkdir = activeRow?.Workdir ?? string.Empty;

        // Keep IsChatMode/IsCodeMode in sync with MainViewModel.ActiveView.
        bool isCode = string.Equals(_main.ActiveView, "code", StringComparison.OrdinalIgnoreCase);
        IsChatMode = !isCode;
        IsCodeMode = isCode;
        ShellState.ActiveMode = isCode ? "Code" : "Chat";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnMainPropertyChanged;
        Sessions.PropertyChanged -= OnSessionsPropertyChanged;
        Sessions.Dispose();
    }
}
