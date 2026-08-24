using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Harbor.App.Avalonia.Navigation;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels.Board;
using Harbor.Desktop.Abstractions.Messages;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Animation;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

public sealed record ShellInfrastructure(
    IDispatcherAdapter Dispatcher,
    ILogger Logger,
    IThemeService ThemeService,
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
    private readonly IThemeService _theme;
    private readonly IToastService _toasts;
    private readonly AvaloniaContentHost _contentHost;
    private readonly IMessenger _messenger;
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
        IOverlayStack? overlayStack = null)
        : base(shell.Dispatcher, shell.Logger)
    {
        _contentHost = (AvaloniaContentHost)contentHost;
        // Palette-driven navigation (shellChrome.Navigate → TryNavigate)
        // bypasses SwitchViewCommand; mirror the route into ActiveView so the
        // tab strip and IsVisible bindings follow (CommandPalette_Enter test).
        _contentHost.RouteNavigated += route => Dispatcher.Post(() => ActiveView = route);
        _effects = shell.EffectHost;
        _theme = shell.ThemeService;
        _toasts = shell.ToastService;
        _shellStatus = shell.ShellStatus;
        _overlayController = shell.OverlayController;
        _costAnimator = shell.CostAnimator;
        _commandPalette = commandPalette;
        _messenger = shell.Messenger;

        _overlayController.Register("palette", v => IsCommandPaletteOpen = v);
        _overlayController.Register("settings", v => IsSettingsOpen = v);
        _overlayController.Register("providerBrowser", v => IsProviderBrowserOpen = v);
        _overlayController.Register("modelPicker", v => IsModelPickerOpen = v);
        _overlayController.Register("diff", v => IsDiffOpen = v);
        _overlayController.Register("tokenUsage", v => IsTokenUsageOpen = v);
        _overlayController.Register("focusSession", v => IsFocusSessionOpen = v);
        _overlayController.Register("sessionsFlyout", v => IsSessionsFlyoutOpen = v);

        HasOverlay = _overlayController.HasOverlay;
        _costAnimator.Tick += () => OnPropertyChanged(nameof(AnimatedCostText));

        // C2: declare state→VM projections ONCE, in the constructor. They
        // were previously re-registered inside OnStoreChanged on EVERY
        // transition AND never applied (ApplySelectors was not called), so
        // MessageCount / StatusText / token labels stayed at their initial
        // values forever while the raw ShellStatus writes moved — the status
        // bar showed "0 msgs" after messages were sent.
        Select(s => s.Status, v => StatusText = v);
        Select(s => s.Provider, v => ProviderLabel = string.IsNullOrEmpty(v) ? "—" : v);
        Select(s => s.Model, v => ModelLabel = string.IsNullOrEmpty(v) ? "—" : v);
        Select(s => s.AgentName, v => AgentLabel = string.IsNullOrEmpty(v) ? "—" : v);
        Select(s => s.Cost.TokensIn, v => TokensIn = v);
        Select(s => s.Cost.TokensOut, v => TokensOut = v);
        Select(s => s.Cost.CostUsd, v => CostUsd = v);
        Select(s => s.IsAgentRunning, v => IsRunning = v);
        Select(s => Math.Max(1, _contentHost.Sessions.Sessions.Count), v => ActiveSessionCount = v);
        Select(s => s.Lines.Length, v => MessageCount = v);

        _messenger.Register<ModelPickedMessage>(this, (_, _) =>
        {
            Dispatcher.Post(() => _overlayController.Close("modelPicker"));
        });

        Settings.Picker = _contentHost.ProviderModelPicker;

        _toasts.Show("Harbor ready — press Ctrl+P for the command palette.", ToastKind.Info);
    }

    public ChatViewModel Chat => _contentHost.Chat;
    public SessionListViewModel Sessions => _contentHost.Sessions;
    public CodeEditorViewModel CodeEditor => _contentHost.CodeEditor;
    public Harbor.Desktop.Abstractions.ViewModels.DiffViewModel Diff => _contentHost.Diff;
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
    public string CostText => StatusMappers.CostToUsd(CostUsd);
    public string RunningDurationText => _runningStartTime is { } start ? FormatDuration(DateTime.UtcNow - start) : string.Empty;
    public string AnimatedCostText => StatusMappers.CostToUsd(_displayCost);
    public bool ShowAnimatedCost => _runningStartTime is not null;
    public IThemeService ThemeService => _theme;

    protected override void OnStoreChanged(UiState state)
    {
        var wasRunning = IsRunning;

        // C2: apply the projections registered once in the constructor.
        ApplySelectors(state);

        var statusBar = StatusProjector.ProjectStatusBar(state);

        TokenHistory.Add(TokensIn + TokensOut);
        while (TokenHistory.Count > 60)
            TokenHistory.RemoveAt(0);

        if (IsRunning && !wasRunning)
        {
            _runningStartTime = DateTime.UtcNow;
            _displayCost = state.Cost.CostUsd;
            _costAnimator.Start(CostUsd);
            OnPropertyChanged(nameof(RunningDurationText));
            OnPropertyChanged(nameof(AnimatedCostText));
            OnPropertyChanged(nameof(ShowAnimatedCost));
        }
        else if (!IsRunning && wasRunning)
        {
            _runningStartTime = null;
            _displayCost = state.Cost.CostUsd;
            _costAnimator.Stop();
            OnPropertyChanged(nameof(RunningDurationText));
            OnPropertyChanged(nameof(AnimatedCostText));
            OnPropertyChanged(nameof(ShowAnimatedCost));
        }

        ShellStatus.Status = state.Status;
        ShellStatus.Provider = string.IsNullOrEmpty(state.Provider) ? "—" : state.Provider;
        ShellStatus.Model = string.IsNullOrEmpty(state.Model) ? "—" : state.Model;
        ShellStatus.AgentName = string.IsNullOrEmpty(state.AgentName) ? "—" : state.AgentName;
        ShellStatus.TokensIn = state.Cost.TokensIn;
        ShellStatus.TokensOut = state.Cost.TokensOut;
        ShellStatus.CostUsd = state.Cost.CostUsd;
        ShellStatus.IsAgentRunning = state.IsAgentRunning;
        ShellStatus.ActiveSessionCount = Math.Max(1, ActiveSessionCount);
        ShellStatus.MessageCount = state.Lines.Length;

        _contentHost.TokenUsage.RecordUsage(state);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

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
    private void SwitchView(string view)
    {
        ActiveView = view;
        // A2 (sprint 4.5): the sessions board reads the session store on
        // demand — refresh it when its tab becomes visible so the user never
        // sees a stale/empty board after chatting in another tab.
        if (view == "board")
        {
            _ = _contentHost.Board.RefreshCommand.ExecuteAsync(null);
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
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
            {
                Dispatcher.Post(() => Toasts.Remove(toast));
            }, TaskScheduler.Default);
        });
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

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        base.Dispose();
    }
}
