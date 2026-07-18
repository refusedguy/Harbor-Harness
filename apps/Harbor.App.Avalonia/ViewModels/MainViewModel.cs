using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Shell view-model. Holds the active view tab, sidebar visibility, status bar,
///     and the central <see cref="UiStore"/> subscription. Top-level keyboard shortcuts
///     (Ctrl+P, Ctrl+Shift+P, Ctrl+B, Ctrl+Shift+T) are wired in MainWindow.axaml.cs
///     and dispatch to commands here.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MainViewModel> _logger;
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly AvaloniaDispatcherAdapter _dispatcher;
    private readonly ThemeService _theme;
    private readonly ToastService _toasts;
    private bool _disposed;

    private readonly EventHandler<Harbor.Ui.Framework.State.UiState> _onStoreChanged;

    /// <summary>Construct the shell view-model.</summary>
    public MainViewModel(
        IServiceProvider services,
        ILogger<MainViewModel> logger,
        UiStore store,
        TuiEffectHost effects,
        AvaloniaDispatcherAdapter dispatcher,
        ThemeService theme,
        ToastService toasts)
    {
        _services = services;
        _logger = logger;
        _store = store;
        _effects = effects;
        _dispatcher = dispatcher;
        _theme = theme;
        _toasts = toasts;

        Chat = services.GetRequiredService<ChatViewModel>();
        Sessions = services.GetRequiredService<SessionListViewModel>();
        CodeEditor = services.GetRequiredService<CodeEditorViewModel>();
        Diff = services.GetRequiredService<DiffViewModel>();
        TokenUsage = services.GetRequiredService<TokenUsageViewModel>();
        ProviderBrowser = services.GetRequiredService<ProviderBrowserViewModel>();
        Settings = services.GetRequiredService<SettingsViewModel>();
        CommandPalette = services.GetRequiredService<CommandPaletteViewModel>();

        // Subscribe to UiStore transitions on the UI thread. NOTE: _dispatcher.Bind(store)
        // is intentionally NOT called here — the composition root (AppHost.BuildAsync) binds
        // the dispatcher to the UiStore exactly once, idempotently. Calling Bind from each
        // ViewModel would create duplicate subscriptions (now prevented by the idempotent
        // Bind, but we still centralise the call for clarity).
        _onStoreChanged = (_, state) => OnStoreChanged(state);
        _dispatcher.OnUiThread += _onStoreChanged;

        // Toasts push on every error.
        _toasts.Show("Harbor ready — press Ctrl+P for the command palette.", ToastKind.Info);
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

    /// <summary>Provider browser view-model.</summary>
    public ProviderBrowserViewModel ProviderBrowser { get; }

    /// <summary>Settings view-model.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Command palette view-model.</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>Toast notifications visible right now.</summary>
    public ObservableCollection<ToastNotification> Toasts { get; } = new();

    [ObservableProperty]
    private string _activeView = "chat";

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isProviderBrowserOpen;

    [ObservableProperty]
    private bool _isDiffOpen;

    [ObservableProperty]
    private bool _isTokenUsageOpen;

    [ObservableProperty]
    private string _statusText = "idle";

    [ObservableProperty]
    private string _providerLabel = "ollama";

    [ObservableProperty]
    private string _modelLabel = "—";

    [ObservableProperty]
    private string _agentLabel = "code";

    [ObservableProperty]
    private long _tokensIn;

    [ObservableProperty]
    private long _tokensOut;

    [ObservableProperty]
    private decimal _costUsd;

    [ObservableProperty]
    private int _activeSessionCount = 1;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Status bar color key based on <see cref="StatusText"/>.</summary>
    public string StatusBrushKey => StatusText switch
    {
        "running" => "StatusRunningBrush",
        "compacting" => "StatusCompactBrush",
        "error" => "StatusErrorBrush",
        _ => "StatusIdleBrush"
    };

    private void OnStoreChanged(UiState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = state.Status;
            ProviderLabel = string.IsNullOrEmpty(state.Provider) ? "—" : state.Provider;
            ModelLabel = string.IsNullOrEmpty(state.Model) ? "—" : state.Model;
            AgentLabel = string.IsNullOrEmpty(state.AgentName) ? "—" : state.AgentName;
            TokensIn = state.Cost.TokensIn;
            TokensOut = state.Cost.TokensOut;
            CostUsd = state.Cost.CostUsd;
            IsRunning = state.IsAgentRunning;
            OnPropertyChanged(nameof(StatusBrushKey));

            // Track token-usage history for the chart.
            TokenUsage.RecordUsage(state);
        });
    }

    /// <summary>Toggle sidebar visibility (Ctrl+B).</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
    }

    /// <summary>Toggle theme (Ctrl+Shift+T).</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        _theme.Toggle();
    }

    /// <summary>Open command palette (Ctrl+P / Ctrl+Shift+P).</summary>
    [RelayCommand]
    private void OpenCommandPalette()
    {
        IsCommandPaletteOpen = true;
    }

    /// <summary>Open settings dialog.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
    }

    /// <summary>Open provider browser.</summary>
    [RelayCommand]
    private void OpenProviderBrowser()
    {
        IsProviderBrowserOpen = true;
    }

    /// <summary>Open diff view.</summary>
    [RelayCommand]
    private void OpenDiff()
    {
        IsDiffOpen = true;
    }

    /// <summary>Open token usage chart.</summary>
    [RelayCommand]
    private void OpenTokenUsage()
    {
        IsTokenUsageOpen = true;
    }

    /// <summary>Switch active main view to one of: chat, code, diff.</summary>
    /// <param name="view">View name.</param>
    [RelayCommand]
    private void SwitchView(string view)
    {
        ActiveView = view;
    }

    /// <summary>Push a toast to the visible toast collection.</summary>
    /// <param name="toast">Toast notification.</param>
    public void AddToast(ToastNotification toast)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Toasts.Add(toast);
            // Auto-dismiss after 4 seconds.
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() => Toasts.Remove(toast));
            }, TaskScheduler.Default);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.OnUiThread -= _onStoreChanged;
    }
}
