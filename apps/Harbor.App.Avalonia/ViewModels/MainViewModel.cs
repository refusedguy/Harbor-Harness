using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels.Board;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Shell view-model. Holds the active view tab, sidebar visibility, status bar,
///     and the central <see cref="UiStore" /> subscription. Top-level keyboard shortcuts
///     (Ctrl+P, Ctrl+Shift+P, Ctrl+B, Ctrl+Shift+T) are wired in MainWindow.axaml.cs
///     and dispatch to commands here.
/// </summary>
internal sealed partial class MainViewModel : StoreSubscriberViewModel
{
    private readonly TuiEffectHost _effects;
    private readonly IServiceProvider _services;
    private readonly IOverlayStack _overlayStack;
    private readonly IThemeService _theme;
    private readonly IToastService _toasts;
    private bool _disposed;

    [ObservableProperty]
    private ShellStatus _shellStatus;

    [ObservableProperty]
    private int _activeSessionCount = 1;

    [ObservableProperty]
    private string _activeView = "chat";

    [ObservableProperty]
    private string _agentLabel = "code";

    [ObservableProperty]
    private decimal _costUsd;
    private decimal _baseCost;
    private decimal _displayCost;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private bool _isDiffOpen;

    [ObservableProperty]
    private bool _isFocusSessionOpen;

    [ObservableProperty]
    private bool _isModelPickerOpen;

    [ObservableProperty]
    private bool _isProviderBrowserOpen;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isRightDrawerOpen;

    [ObservableProperty]
    private string? _activeDiffText;

    [ObservableProperty]
    private string? _activeDiffTitle;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private bool _isTokenUsageOpen;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private string _modelLabel = "—";

    [ObservableProperty]
    private string _providerLabel = "ollama";

    [ObservableProperty]
    private string _statusText = "idle";

    [ObservableProperty]
    private long _tokensIn;

    [ObservableProperty]
    private long _tokensOut;

    public ObservableCollection<double> TokenHistory { get; } = new();

    public IOverlayStack OverlayStack => _overlayStack;

    [ObservableProperty]
    private bool _hasOverlay;

    [RelayCommand(CanExecute = nameof(CanPopOverlay))]
    private void OverlayPop()
    {
        _overlayStack.PopTop();
    }

    private bool CanPopOverlay() => _overlayStack.Current is not null;

    private readonly DispatcherTimer _durationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime? _runningStartTime;

    /// <summary>Construct the shell view-model.</summary>
    public MainViewModel(
        IServiceProvider services,
        ILogger<MainViewModel> logger,
        TuiEffectHost effects,
        IDispatcherAdapter dispatcher,
        IThemeService theme,
        IToastService toasts,
        ShellStatus shellStatus,
        IOverlayStack? overlayStack = null)
        : base(dispatcher, logger)
    {
        _services = services;
        _effects = effects;
        _theme = theme;
        _toasts = toasts;
        _shellStatus = shellStatus;
        _overlayStack = overlayStack ?? new OverlayStackService();
        _durationTimer.Tick += OnDurationTick;

        Chat = services.GetRequiredService<ChatViewModel>();
        Sessions = services.GetRequiredService<SessionListViewModel>();
        CodeEditor = services.GetRequiredService<CodeEditorViewModel>();
        Diff = services.GetRequiredService<DiffViewModel>();
        TokenUsage = services.GetRequiredService<TokenUsageViewModel>();
        ProviderBrowser = services.GetRequiredService<ProviderBrowserViewModel>();
        ProviderModelPicker = services.GetRequiredService<ProviderModelPickerViewModel>();
        Settings = services.GetRequiredService<SettingsViewModel>();
        CommandPalette = services.GetRequiredService<CommandPaletteViewModel>();
        FocusSession = services.GetRequiredService<FocusSessionViewModel>();
        Board = services.GetRequiredService<BoardViewModel>();

        Settings.Picker = ProviderModelPicker;

        ProviderModelPicker.ModelSelected += () =>
        {
            Dispatcher.Post(() => IsModelPickerOpen = false);
        };

        _toasts.Show("Harbor ready — press Ctrl+P for the command palette.", ToastKind.Info);

        _overlayStack.Popped += id =>
        {
            if (id is not null)
                CloseOverlay(id);
        };
        _overlayStack.Changed += (_, _) => HasOverlay = _overlayStack.Current is not null;
        HasOverlay = _overlayStack.Current is not null;
    }

    /// <summary>Active chat view-model.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Session list view-model.</summary>
    public SessionListViewModel Sessions { get; }

    /// <summary>Code editor view-model.</summary>
    public CodeEditorViewModel CodeEditor { get; }

    /// <summary>Diff view-model.</summary>
    public DiffViewModel Diff { get; }

    /// <summary>Token-usage chart view-model.</summary>
    public TokenUsageViewModel TokenUsage { get; }

    public FocusSessionViewModel FocusSession { get; }

    /// <summary>Board (mission control) view-model.</summary>
    public BoardViewModel Board { get; }

    /// <summary>Provider browser view-model.</summary>
    public ProviderBrowserViewModel ProviderBrowser { get; }

    /// <summary>
    ///     Provider + model picker view-model. Backs the
    ///     <c>ProviderModelPicker</c> control shown when the user clicks the
    ///     status-bar model label.
    /// </summary>
    public ProviderModelPickerViewModel ProviderModelPicker { get; }

    /// <summary>Settings view-model.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Command palette view-model.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    public ShellState ShellState { get; } = new();

    public string RightDrawerTab
    {
        get => ShellState.RightDrawerTab;
        set => ShellState.RightDrawerTab = value;
    }

    /// <summary>Toast notifications visible right now.</summary>
    public ObservableCollection<ToastNotification> Toasts { get; } = new();

    public string StatusBrushKey => StatusMappers.StatusToBrushKey(StatusText);
    public string TokensInText => StatusMappers.TokensToCompact(TokensIn);
    public string TokensOutText => StatusMappers.TokensToCompact(TokensOut);
    public string CostText => StatusMappers.CostToUsd(CostUsd);
    public string RunningDurationText => _runningStartTime is { } start ? FormatDuration(DateTime.UtcNow - start) : string.Empty;
    public string AnimatedCostText => StatusMappers.CostToUsd(_displayCost);
    public bool ShowAnimatedCost => _runningStartTime is not null;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    private void OnDurationTick(object? sender, EventArgs e)
    {
        if (_runningStartTime is not { } start)
        {
            _durationTimer.Stop();
            return;
        }

        var elapsed = DateTime.UtcNow - start;
        var targetCost = _baseCost;
        var smoothCost = targetCost + (decimal)(elapsed.TotalSeconds * 0.0001);
        _displayCost = smoothCost;

        OnPropertyChanged(nameof(RunningDurationText));
        OnPropertyChanged(nameof(AnimatedCostText));
    }

    protected override void OnStoreChanged(UiState state)
    {
        var wasRunning = IsRunning;

        StatusText = state.Status;
        ProviderLabel = string.IsNullOrEmpty(state.Provider) ? "—" : state.Provider;
        ModelLabel = string.IsNullOrEmpty(state.Model) ? "—" : state.Model;
        AgentLabel = string.IsNullOrEmpty(state.AgentName) ? "—" : state.AgentName;
        TokensIn = state.Cost.TokensIn;
        TokensOut = state.Cost.TokensOut;
        CostUsd = state.Cost.CostUsd;
        IsRunning = state.IsAgentRunning;
        ActiveSessionCount = Math.Max(1, Sessions.Sessions.Count);
        MessageCount = state.Lines.Length;
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(TokensInText));
        OnPropertyChanged(nameof(TokensOutText));
        OnPropertyChanged(nameof(CostText));
        OnPropertyChanged(nameof(RunningDurationText));
        OnPropertyChanged(nameof(AnimatedCostText));
        OnPropertyChanged(nameof(ShowAnimatedCost));

        TokenHistory.Add(TokensIn + TokensOut);
        while (TokenHistory.Count > 60)
            TokenHistory.RemoveAt(0);

        if (IsRunning && !wasRunning)
        {
            _runningStartTime = DateTime.UtcNow;
            _baseCost = state.Cost.CostUsd;
            _displayCost = state.Cost.CostUsd;
            _durationTimer.Start();
        }
        else if (!IsRunning && wasRunning)
        {
            _runningStartTime = null;
            _displayCost = state.Cost.CostUsd;
            _durationTimer.Stop();
        }
        else if (IsRunning)
        {
            _baseCost = state.Cost.CostUsd;
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

        TokenUsage.RecordUsage(state);
    }

    /// <summary>Toggle sidebar visibility (Ctrl+B).</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    /// <summary>Toggle theme (Ctrl+Shift+T).</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

    /// <summary>Open command palette (Ctrl+P / Ctrl+Shift+P).</summary>
    [RelayCommand]
    private void OpenCommandPalette()
    {
        IsCommandPaletteOpen = true;
        _overlayStack.Push("palette");
    }

    /// <summary>Open settings dialog.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        _overlayStack.Push("settings");
    }

    /// <summary>Open provider browser.</summary>
    [RelayCommand]
    private void OpenProviderBrowser()
    {
        IsProviderBrowserOpen = true;
        _overlayStack.Push("providerBrowser");
    }

    /// <summary>
    ///     Open the provider/model picker flyout. Wired to a click handler on
    ///     the status-bar's <c>ModelLabel</c> TextBlock so the user can swap
    ///     models without leaving the chat view.
    /// </summary>
    [RelayCommand]
    private void OpenModelPicker()
    {
        IsModelPickerOpen = true;
        _overlayStack.Push("modelPicker");
    }

    /// <summary>Open diff view.</summary>
    [RelayCommand]
    private void OpenDiff()
    {
        IsDiffOpen = true;
        _overlayStack.Push("diff");
    }

    /// <summary>Open token usage chart.</summary>
    [RelayCommand]
    private void OpenTokenUsage()
    {
        IsTokenUsageOpen = true;
        _overlayStack.Push("tokenUsage");
    }

    [RelayCommand]
    private void ToggleFocusSession()
    {
        IsFocusSessionOpen = !IsFocusSessionOpen;
        if (IsFocusSessionOpen)
        {
            FocusSession.Title = Sessions.ActiveSession?.Title ?? "Current Session";
            FocusSession.Model = ModelLabel;
            FocusSession.Provider = ProviderLabel;
            FocusSession.Agent = AgentLabel;
            FocusSession.MessageCount = MessageCount;
            FocusSession.TokensIn = TokensIn;
            FocusSession.TokensOut = TokensOut;
            _overlayStack.Push("focusSession");
        }
    }

    /// <summary>Switch active main view to one of: chat, code, diff.</summary>
    /// <param name="view">View name.</param>
    [RelayCommand]
    private void SwitchView(string view) => ActiveView = view;

    /// <summary>Toggle the right drawer. Pass a tab name to open that tab, or null to close.</summary>
    /// <param name="tab">Tab name (Files, Terminal, History) or null.</param>
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

    /// <summary>Push a toast to the visible toast collection.</summary>
    /// <param name="toast">Toast notification.</param>
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

    private void CloseOverlay(string id)
    {
        switch (id)
        {
            case "palette":         IsCommandPaletteOpen = false; break;
            case "settings":        IsSettingsOpen = false; break;
            case "providerBrowser": IsProviderBrowserOpen = false; break;
            case "modelPicker":     IsModelPickerOpen = false; break;
            case "diff":            IsDiffOpen = false; break;
            case "tokenUsage":      IsTokenUsageOpen = false; break;
            case "focusSession":    IsFocusSessionOpen = false; break;
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _durationTimer.Tick -= OnDurationTick;
        _durationTimer.Stop();
        base.Dispose();
    }
}
