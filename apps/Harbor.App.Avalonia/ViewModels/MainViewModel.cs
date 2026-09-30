using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CSharpFunctionalExtensions;
using Harbor.App.Avalonia.Navigation;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels.Board;
using Harbor.Abstractions.Tools;
using Harbor.Desktop.Abstractions.Messages;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Animation;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

public sealed record ShellInfrastructure(
    IDispatcherAdapter Dispatcher,
    ILogger Logger,
    IThemeApplier ThemeApplier,
    IToastService ToastService,
    TuiEffectHost EffectHost,
    OverlayController OverlayController,
    CostAnimator CostAnimator,
    IMessenger Messenger,
    ShellStatus ShellStatus);

public sealed partial class MainViewModel : StoreSubscriberViewModel
{
    private static readonly Dictionary<string, string> OverlayIdToFlagProperty = new()
    {
        ["palette"] = nameof(IsCommandPaletteOpen),
        ["settings"] = nameof(IsSettingsOpen),
        ["providerBrowser"] = nameof(IsProviderBrowserOpen),
        ["modelPicker"] = nameof(IsModelPickerOpen),
        ["diff"] = nameof(IsDiffOpen),
        ["tokenUsage"] = nameof(IsTokenUsageOpen),
        ["focusSession"] = nameof(IsFocusSessionOpen),
    };

    private static readonly Dictionary<string, Action<MainViewModel, bool>> OverlayFlagSetters = new();

    private readonly TuiEffectHost _effects;
    private readonly CommandPaletteViewModel _commandPalette;
    private readonly OverlayController _overlayController;
    private readonly CostAnimator _costAnimator;
    private readonly IThemeApplier _themeApplier;
    private readonly IToastService _toasts;
    private readonly AvaloniaContentHost _contentHost;
    private readonly IMessenger _messenger;
    private readonly ProjectFileTreeScanner _fileTreeScanner;
    private readonly ILogger _logger;
    private readonly Lock _scanGate = new();
    private CancellationTokenSource? _fileTreeScan;
    private bool _disposed;
    private DateTime? _runningStartTime;
    private decimal _displayCost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrushKey))]
    private ShellStatus _shellStatus;

    [ObservableProperty]
    private int _activeSessionCount = 1;

    [ObservableProperty]
    private string _activeView = "chat";

    [ObservableProperty]
    private string _agentLabel = "code";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostText))]
    [NotifyPropertyChangedFor(nameof(RunningDurationText))]
    [NotifyPropertyChangedFor(nameof(AnimatedCostText))]
    [NotifyPropertyChangedFor(nameof(ShowAnimatedCost))]
    [NotifyPropertyChangedFor(nameof(HasCost))]
    [NotifyPropertyChangedFor(nameof(ShowLiveCost))]
    private decimal _costUsd;

    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        internal set => SetProperty(ref _isCommandPaletteOpen, value);
    }

    public bool IsDiffOpen
    {
        get => _isDiffOpen;
        internal set => SetProperty(ref _isDiffOpen, value);
    }

    public bool IsFocusSessionOpen
    {
        get => _isFocusSessionOpen;
        internal set => SetProperty(ref _isFocusSessionOpen, value);
    }

    public bool IsModelPickerOpen
    {
        get => _isModelPickerOpen;
        internal set => SetProperty(ref _isModelPickerOpen, value);
    }

    public bool IsProviderBrowserOpen
    {
        get => _isProviderBrowserOpen;
        internal set => SetProperty(ref _isProviderBrowserOpen, value);
    }

    private bool _isCommandPaletteOpen;
    private bool _isDiffOpen;
    private bool _isFocusSessionOpen;
    private bool _isModelPickerOpen;
    private bool _isProviderBrowserOpen;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isRightDrawerOpen;

    [ObservableProperty]
    private string? _activeDiffText;

    [ObservableProperty]
    private string? _activeDiffTitle;

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        internal set => SetProperty(ref _isSettingsOpen, value);
    }

    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private ObservableCollection<FileTreeNode> _fileTree = new();

    [ObservableProperty]
    private string _projectRootPath = string.Empty;

    private string _rightDrawerTab = "None";

    public bool IsTokenUsageOpen
    {
        get => _isTokenUsageOpen;
        internal set => SetProperty(ref _isTokenUsageOpen, value);
    }

    private bool _isTokenUsageOpen;

    public bool IsSessionsFlyoutOpen
    {
        get => _isSessionsFlyoutOpen;
        internal set => SetProperty(ref _isSessionsFlyoutOpen, value);
    }

    private bool _isSessionsFlyoutOpen;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private string _modelLabel = "—";

    [ObservableProperty]
    private string _providerLabel = "ollama";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrushKey))]
    private string _statusText = "idle";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensInText))]
    [NotifyPropertyChangedFor(nameof(CostText))]
    [NotifyPropertyChangedFor(nameof(RunningDurationText))]
    [NotifyPropertyChangedFor(nameof(AnimatedCostText))]
    [NotifyPropertyChangedFor(nameof(ShowAnimatedCost))]
    private long _tokensIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensOutText))]
    [NotifyPropertyChangedFor(nameof(CostText))]
    [NotifyPropertyChangedFor(nameof(RunningDurationText))]
    [NotifyPropertyChangedFor(nameof(AnimatedCostText))]
    [NotifyPropertyChangedFor(nameof(ShowAnimatedCost))]
    private long _tokensOut;

    public ObservableCollection<double> TokenHistory { get; } = new();

    [ObservableProperty]
    private bool _hasOverlay;

    [RelayCommand(CanExecute = nameof(CanPopOverlay))]
    private void OverlayPop()
    {
        _overlayController.CloseTop();
    }

    private bool CanPopOverlay() => _overlayController.HasOverlay;

    public bool AdvanceDuration()
    {
        _costAnimator.Advance();
        _displayCost = _costAnimator.DisplayCost;
        return _costAnimator.IsRunning;
    }

    public MainViewModel(
        IContentHost contentHost,
        ShellInfrastructure shell,
        CommandPaletteViewModel commandPalette,
        ProjectFileTreeScanner fileTreeScanner,
        IOverlayStack? overlayStack = null)
        : base(shell.Dispatcher, shell.Logger)
    {
        _contentHost = (AvaloniaContentHost)contentHost;
        // Palette-driven navigation (shellChrome.Navigate → TryNavigate)
        // bypasses SwitchViewCommand; mirror the route into ActiveView so the
        // tab strip and IsVisible bindings follow (CommandPalette_Enter test).
        _contentHost.RouteNavigated += route => Dispatcher.Post(() => ActiveView = route);
        _effects = shell.EffectHost;
        _themeApplier = shell.ThemeApplier;
        _toasts = shell.ToastService;
        _shellStatus = shell.ShellStatus;
        _overlayController = shell.OverlayController;
        _costAnimator = shell.CostAnimator;
        _commandPalette = commandPalette;
        _messenger = shell.Messenger;
        _logger = shell.Logger;
        // #492: the file tree is no longer built here. This view-model asks the
        // scanner, the scanner asks the Domain ports, and the only thing left in
        // the view-model is "which scan owns the state right now".
        _fileTreeScanner = fileTreeScanner;

        _overlayController.Register(OverlayIds.Palette, v => IsCommandPaletteOpen = v);
        _overlayController.Register(OverlayIds.Settings, v => IsSettingsOpen = v);
        _overlayController.Register(OverlayIds.ProviderBrowser, v => IsProviderBrowserOpen = v);
        _overlayController.Register(OverlayIds.ModelPicker, v => IsModelPickerOpen = v);
        _overlayController.Register(OverlayIds.Diff, v => IsDiffOpen = v);
        _overlayController.Register(OverlayIds.TokenUsage, v => IsTokenUsageOpen = v);
        _overlayController.Register(OverlayIds.FocusSession, v => IsFocusSessionOpen = v);
        _overlayController.Register(OverlayIds.SessionsFlyout, v => IsSessionsFlyoutOpen = v);

        HasOverlay = _overlayController.HasOverlay;
        _costAnimator.Tick += () => OnPropertyChanged(nameof(AnimatedCostText));

        // C2: declare state→VM projections ONCE, in the constructor. They
        // were previously re-registered inside OnStoreChanged on EVERY
        // transition AND never applied (ApplySelectors was not called), so
        // MessageCount / StatusText / token labels stayed at their initial
        // values forever while the raw ShellStatus writes moved — the status
        // bar showed "0 msgs" after messages were sent.
        Select(s => s.Chat.Status, v => StatusText = v);
        Select(s => s.Chat.Provider, v => ProviderLabel = string.IsNullOrEmpty(v) ? "—" : v);
        Select(s => s.Chat.Model, v => ModelLabel = PrettifyModel(v));
        Select(s => s.Chat.AgentName, v => AgentLabel = string.IsNullOrEmpty(v) ? "—" : v);
        Select(s => s.Chat.Cost.TokensIn, v => TokensIn = v);
        Select(s => s.Chat.Cost.TokensOut, v => TokensOut = v);
        Select(s => s.Chat.Cost.CostUsd, v => CostUsd = v);
        Select(s => s.Chat.IsAgentRunning, v => IsRunning = v);
        Select(s => Math.Max(1, _contentHost.Sessions.Sessions.Count), v => ActiveSessionCount = v);
        Select(s => s.Chat.Lines.Length, v => MessageCount = v);

        _messenger.Register<ModelPickedMessage>(this, (_, _) =>
        {
            Dispatcher.Post(() => _overlayController.Close("modelPicker"));
        });

        Settings.Picker = _contentHost.ProviderModelPicker;

        _toasts.Show("Harbor ready — press Ctrl+P for the command palette.", ToastKind.Info);

        ProjectRootPath = Environment.CurrentDirectory;

        // #569: the constructor cannot await. RefreshFileTreeAsync observes its own
        // failure (it logs and leaves the tree untouched), but the Task was being
        // discarded bare, so a fault raised outside that guard was lost. Started,
        // not abandoned — and the file tree simply stays empty until the scan
        // lands, exactly as before.
        TaskFireAndForget.Forget(
            RefreshFileTreeAsync(),
            ex => _logger.LogError(ex, "Initial file-tree scan failed"));
    }

    public ChatViewModel Chat => _contentHost.Chat;
    public SessionListViewModel Sessions => _contentHost.Sessions;
    public CodeEditorViewModel CodeEditor => _contentHost.CodeEditor;
    public Harbor.Ui.Framework.ViewModels.DiffViewModel Diff => _contentHost.Diff;
    public TokenUsageViewModel TokenUsage => _contentHost.TokenUsage;
    public FocusSessionViewModel FocusSession => _contentHost.FocusSession;
    public BoardViewModel Board => _contentHost.Board;
    public ProviderBrowserViewModel ProviderBrowser => _contentHost.ProviderBrowser;
    public ProviderModelPickerViewModel ProviderModelPicker => _contentHost.ProviderModelPicker;
    public SettingsViewModel Settings => _contentHost.Settings;
    public CommandPaletteViewModel CommandPalette => _commandPalette;

    public string RightDrawerTab
    {
        get => _rightDrawerTab;
        set => SetProperty(ref _rightDrawerTab, value);
    }

    public ObservableCollection<ToastNotification> Toasts { get; } = new();

    public string StatusBrushKey => StatusMappers.StatusToBrushKey(StatusText);
    public string TokensInText => StatusMappers.TokensToCompact(TokensIn);
    public string TokensOutText => StatusMappers.TokensToCompact(TokensOut);
    /// <summary>C2: raw provider ids like "kilo-auto/free" render as the
    /// friendly tail ("kilo free") instead of plumbing jargon.</summary>
    private static string PrettifyModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return "—";
        }

        if (model.StartsWith("kilo-auto/", StringComparison.Ordinal))
        {
            return model.Replace("kilo-auto/", "kilo ", StringComparison.Ordinal);
        }

        return model;
    }

    public string CostText => StatusMappers.CostToUsd(CostUsd);
    public string RunningDurationText => _runningStartTime is { } start ? FormatDuration(DateTime.UtcNow - start) : string.Empty;
    public string AnimatedCostText => StatusMappers.CostToUsd(_displayCost);
    public bool ShowAnimatedCost => _runningStartTime is not null;

    /// <summary>B6: hide the money readout entirely while nothing was spent.</summary>
    public bool HasCost => CostUsd > 0m;
    public bool ShowLiveCost => ShowAnimatedCost && _displayCost > 0m;
    public IThemeApplier ThemeApplier => _themeApplier;

    protected override void OnStoreChanged(UiState state)
    {
        var wasRunning = IsRunning;

        // C2: apply the projections registered once in the constructor.
        ApplySelectors(state);

        // #568: this used to compute `StatusProjector.ProjectStatusBar(state)`
        // into a local that was never read — an ImmutableArray and six
        // segments allocated on every state change, to produce nothing. The
        // status bar this window draws is bound to the Select()-projected
        // properties below (StatusText, AgentLabel, ModelLabel, …), which is
        // also why no .axaml binds ShellStatus.*: that record is written below
        // and read by nothing. Removing the dead call is step 9 of the issue's
        // fourteen, and the first of them that is a pure deletion.
        TokenHistory.Add(TokensIn + TokensOut);
        while (TokenHistory.Count > 60)
            TokenHistory.RemoveAt(0);

        if (IsRunning && !wasRunning)
        {
            _runningStartTime = DateTime.UtcNow;
            _displayCost = state.Chat.Cost.CostUsd;
            _costAnimator.Start(CostUsd);
            OnPropertyChanged(nameof(RunningDurationText));
            OnPropertyChanged(nameof(AnimatedCostText));
            OnPropertyChanged(nameof(ShowAnimatedCost));
        }
        else if (!IsRunning && wasRunning)
        {
            _runningStartTime = null;
            _displayCost = state.Chat.Cost.CostUsd;
            _costAnimator.Stop();
            OnPropertyChanged(nameof(RunningDurationText));
            OnPropertyChanged(nameof(AnimatedCostText));
            OnPropertyChanged(nameof(ShowAnimatedCost));
        }

        ShellStatus.Status = state.Chat.Status;
        ShellStatus.Provider = string.IsNullOrEmpty(state.Chat.Provider) ? "—" : state.Chat.Provider;
        ShellStatus.Model = string.IsNullOrEmpty(state.Chat.Model) ? "—" : state.Chat.Model;
        ShellStatus.AgentName = string.IsNullOrEmpty(state.Chat.AgentName) ? "—" : state.Chat.AgentName;
        ShellStatus.TokensIn = state.Chat.Cost.TokensIn;
        ShellStatus.TokensOut = state.Chat.Cost.TokensOut;
        ShellStatus.CostUsd = state.Chat.Cost.CostUsd;
        ShellStatus.IsAgentRunning = state.Chat.IsAgentRunning;
        ShellStatus.ActiveSessionCount = Math.Max(1, ActiveSessionCount);
        ShellStatus.MessageCount = state.Chat.Lines.Length;

        _contentHost.TokenUsage.RecordUsage(state);
    }

    [RelayCommand]
    public void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    public void ToggleTheme() => _themeApplier.Toggle();

    [RelayCommand]
    private void OpenCommandPalette()
        => _overlayController.Open("palette");

    [RelayCommand]
    private void OpenSettings()
        => _overlayController.Open("settings");

    [RelayCommand]
    private void OpenProviderBrowser()
        => _overlayController.Open("providerBrowser");

    [RelayCommand]
    private void OpenModelPicker()
        => _overlayController.Open("modelPicker");

    [RelayCommand]
    private void OpenDiff()
        => _overlayController.Open("diff");

    [RelayCommand]
    private void OpenTokenUsage()
        => _overlayController.Open("tokenUsage");

    [RelayCommand]
    private void ToggleFocusSession()
    {
        if (IsFocusSessionOpen)
        {
            _overlayController.Close("focusSession");
        }
        else
        {
            FocusSession.Title = _contentHost.Sessions.ActiveSession?.Title ?? "Current Session";
            FocusSession.Model = ModelLabel;
            FocusSession.Provider = ProviderLabel;
            FocusSession.Agent = AgentLabel;
            FocusSession.MessageCount = MessageCount;
            FocusSession.TokensIn = TokensIn;
            FocusSession.TokensOut = TokensOut;
            _overlayController.Open("focusSession");
        }
    }

    [RelayCommand]
    public void SwitchView(string view)
    {
        ActiveView = view;
        // A2 (sprint 4.5): the sessions board reads the session store on
        // demand — refresh it when its tab becomes visible so the user never
        // sees a stale/empty board after chatting in another tab.
        if (view == "board")
        {
            // #569: SwitchView is [RelayCommand] on a void method, so the board
            // refresh cannot be awaited. It was discarded bare.
            TaskFireAndForget.Forget(
                _contentHost.Board.RefreshCommand.ExecuteAsync(null),
                ex => _logger.LogError(ex, "Sessions-board refresh on tab switch failed"));
        }
    }

    [RelayCommand]
    private void ToggleRightDrawer(string? tab)
    {
        if (string.IsNullOrEmpty(tab))
        {
            IsRightDrawerOpen = false;
            return;
        }

        RightDrawerTab = tab;
        IsRightDrawerOpen = !IsRightDrawerOpen;
    }

    public void AddToast(ToastNotification toast)
    {
        Dispatcher.Post(() =>
        {
            Toasts.Add(toast);

            // #569: the 4s auto-dismiss timer used to be a bare
            // `Task.Delay(...).ContinueWith(...)`. Task.Delay itself does not
            // fault, but the continuation body runs outside any handler — a
            // throw there faulted the ContinueWith task with nobody watching.
            TaskFireAndForget.Forget(
                RemoveToastAfterDelayAsync(toast),
                ex => _logger.LogError(ex, "Auto-dismiss timer faulted for toast {Message}", toast.Message));
        });
    }

    private async Task RemoveToastAfterDelayAsync(ToastNotification toast)
    {
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        Dispatcher.Post(() => Toasts.Remove(toast));
    }

    public bool CloseTopOverlay()
    {
        return _overlayController.CloseTop();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    /// <summary>
    ///     Re-scan <see cref="ProjectRootPath" /> and swap the sidebar's tree for
    ///     the result.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#492 — what this method no longer does.</b> It used to contain the
    ///         walk itself: <c>Directory.GetDirectories</c> / <c>Directory.GetFiles</c>
    ///         / <c>new DirectoryInfo</c>, a recursive <c>LoadDirectory</c> with a
    ///         literal depth cap, a private ignore list and a private
    ///         extension→icon <c>switch</c>, all inside a <c>Task.Run</c> with no
    ///         cancellation and no budget. All of that is
    ///         <see cref="ProjectFileTreeScanner" /> + the two Domain ports behind
    ///         it. What remains here is the one thing only the view-model can do:
    ///         decide whether the answer it was handed is still the answer the user
    ///         is looking at.
    ///     </para>
    ///     <para>
    ///         <b>Why there was a cancellation seam to add.</b> The old Task.Run
    ///         had none, so pressing Refresh while a scan was running started a
    ///         second one, both ran to completion, and whichever finished last won
    ///         — including a scan for a root the user had already navigated away
    ///         from. <see cref="ProjectRootPath" /> was also read TWICE inside the
    ///         Task.Run while the UI thread could write it. Now a new request
    ///         cancels the one in flight, the root is captured once per request, and
    ///         a result is applied only if the scan that produced it is still the
    ///         current one for the root the view is pointing at. That is the same
    ///         staleness rule <c>FileTreeSnapshot.Covers</c> applies to the TUI
    ///         sidebar (#667), applied to a collection instead of a store.
    ///     </para>
    ///     <para>
    ///         <b>Threading.</b> The scan is I/O-bound and runs on thread-pool
    ///         threads inside the port. The two mutations of
    ///         <see cref="FileTree" /> below are NOT marshalled by hand: this
    ///         method is entered on the UI thread (the shell command and the view's
    ///         refresh button both call it there) and the <c>await</c> on the scan
    ///         resumes on the captured Avalonia synchronization context, which is
    ///         what keeps the bound <c>ObservableCollection</c> on the thread its
    ///         bindings read it on. Deliberately no <c>ConfigureAwait(false)</c> on
    ///         THAT await — it would move the mutation off the UI thread and break
    ///         the binding rather than speed anything up.
    ///     </para>
    /// </remarks>
    [RelayCommand]
    public async Task RefreshFileTreeAsync()
    {
        string root = ProjectRootPath;
        CancellationTokenSource scan = new();
        CancellationTokenSource? superseded;

        lock (_scanGate)
        {
            superseded = _fileTreeScan;
            _fileTreeScan = scan;
        }

        // Outside the lock: cancelling runs continuations, and doing that under a
        // lock is how a lock becomes a deadlock. The loser also disposes its own
        // source, exactly as FileTreeLoader does (#667).
        if (superseded is not null)
        {
            try
            {
                superseded.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished and released its own source between the swap and
                // here. Losing a cancel race against your own completion is normal.
            }
        }

        try
        {
            Result<FileTreeNode> scanned = await _fileTreeScanner.ScanAsync(root, scan.Token);

            if (scanned.IsSuccess)
            {
                if (IsCurrentScan(scan, root))
                {
                    FileTree.Clear();
                    FileTree.Add(scanned.Value);
                }
                else
                {
                    Logger.LogDebug(
                        "Discarding a file-tree scan of {Path} that arrived after a newer one",
                        root);
                }

                return;
            }

            // Nothing to draw and a reason to draw it. The previous tree stays:
            // replacing it with an empty collection is a lie about the project.
            Logger.LogError("Failed to scan project root {Path}: {Reason}", root, scanned.Error);
        }
        catch (OperationCanceledException ex)
        {
            // Superseded, or the window is closing. Not an error: the newer scan
            // owns the tree, and painting a failure for a walk nobody is waiting
            // for any more is a lie the user would have to read. The exception is
            // still handed to the logger (S6667) because a cancellation that is
            // NOT this one — a token the view-model never owned, say — is worth
            // being able to find in a trace.
            Logger.LogDebug(ex, "File-tree scan of {Path} was superseded", root);
        }
        catch (Exception ex)
        {
            // The port already turns expected failures into results, so anything
            // reaching here is a bug — but it is not allowed to take the shell
            // down, and the previous tree is still the best thing to show.
            Logger.LogError(ex, "File-tree scan of {Path} threw", root);
        }
        finally
        {
            // Every scan disposes its OWN source, whether or not it was still the
            // current one when it finished. Disposing only when still ours leaked
            // the source of every superseded scan, because by then the slot belongs
            // to its replacement.
            lock (_scanGate)
            {
                if (ReferenceEquals(_fileTreeScan, scan))
                {
                    _fileTreeScan = null;
                }
            }

            scan.Dispose();
        }
    }

    /// <summary>
    ///     Whether <paramref name="scan" /> is still the request the sidebar is
    ///     waiting for, and the root it was asked about is still the one on screen.
    /// </summary>
    /// <param name="scan">The source the in-flight scan is holding.</param>
    /// <param name="root">The root that scan was started for.</param>
    private bool IsCurrentScan(CancellationTokenSource scan, string root)
    {
        lock (_scanGate)
        {
            return ReferenceEquals(_fileTreeScan, scan)
                && string.Equals(ProjectRootPath, root, StringComparison.Ordinal);
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // #492: a scan in flight belongs to a window that is going away. Cancelling
        // it here is what stops the walk from finishing into a dead view-model; the
        // scan disposes its own source when it unwinds, so this only cancels.
        CancellationTokenSource? inFlight;
        lock (_scanGate)
        {
            inFlight = _fileTreeScan;
            _fileTreeScan = null;
        }

        if (inFlight is not null)
        {
            try
            {
                inFlight.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It completed and released its source between the take and here.
            }
        }

        base.Dispose();
    }
}
