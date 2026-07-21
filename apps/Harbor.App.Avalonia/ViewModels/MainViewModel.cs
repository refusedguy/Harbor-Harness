using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Shell view-model. Holds the active view tab, sidebar visibility, status bar,
///     and the central <see cref="UiStore" /> subscription. Top-level keyboard shortcuts
///     (Ctrl+P, Ctrl+Shift+P, Ctrl+B, Ctrl+Shift+T) are wired in MainWindow.axaml.cs
///     and dispatch to commands here.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AvaloniaDispatcherAdapter _dispatcher;
    private readonly TuiEffectHost _effects;
    private readonly ILogger<MainViewModel> _logger;

    private readonly EventHandler<UiState> _onStoreChanged;
    private readonly IServiceProvider _services;
    private readonly UiStore _store;
    private readonly ThemeService _theme;
    private readonly ToastService _toasts;

    [ObservableProperty]
    private int _activeSessionCount = 1;

    [ObservableProperty]
    private string _activeView = "chat";

    [ObservableProperty]
    private string _agentLabel = "code";

    [ObservableProperty]
    private decimal _costUsd;
    private bool _disposed;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private bool _isDiffOpen;

    /// <summary>
    ///     True when the provider/model picker flyout is open. Toggled by
    ///     clicking the status-bar model label (wired in MainWindow.axaml) and
    ///     auto-reset to false after a model is selected.
    /// </summary>
    [ObservableProperty]
    private bool _isModelPickerOpen;

    [ObservableProperty]
    private bool _isProviderBrowserOpen;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private bool _isTokenUsageOpen;

    /// <summary>
    ///     Live message count for the active chat (number of chat lines in
    ///     the current <see cref="UiStore" /> state). Updated on every
    ///     <see cref="OnStoreChanged" /> transition so the status bar's
    ///     "N msgs" label tracks new messages immediately after the user
    ///     sends a prompt (Task D2 / Problem 2: status bar message count
    ///     was stale — only refreshed on full RefreshAsync cycles).
    /// </summary>
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
        ProviderModelPicker = services.GetRequiredService<ProviderModelPickerViewModel>();
        Settings = services.GetRequiredService<SettingsViewModel>();
        CommandPalette = services.GetRequiredService<CommandPaletteViewModel>();

        // Wire the picker into Settings so the embedded "Browse Models"
        // section uses the same VM as the status-bar flyout.
        Settings.Picker = ProviderModelPicker;

        // Close the picker flyout as soon as the user selects a model. The
        // picker VM raises ModelSelected after persisting the choice and
        // rebinding the active session — at that point the flyout has served
        // its purpose and lingering would only confuse the user.
        ProviderModelPicker.ModelSelected += () =>
        {
            _dispatcher.Post(() => IsModelPickerOpen = false);
        };

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

    /// <summary>Toast notifications visible right now.</summary>
    public ObservableCollection<ToastNotification> Toasts { get; } = new();

    /// <summary>Status bar color key based on <see cref="StatusText" />.</summary>
    public string StatusBrushKey => StatusMappers.StatusToBrushKey(StatusText);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dispatcher.OnUiThread -= _onStoreChanged;
    }

    private void OnStoreChanged(UiState state)
    {
        _dispatcher.Post(() =>
        {
            StatusText = state.Status;
            ProviderLabel = string.IsNullOrEmpty(state.Provider) ? "—" : state.Provider;
            ModelLabel = string.IsNullOrEmpty(state.Model) ? "—" : state.Model;
            AgentLabel = string.IsNullOrEmpty(state.AgentName) ? "—" : state.AgentName;
            TokensIn = state.Cost.TokensIn;
            TokensOut = state.Cost.TokensOut;
            CostUsd = state.Cost.CostUsd;
            IsRunning = state.IsAgentRunning;
            // Refresh the status bar's session-count group from the live
            // sidebar collection so it tracks New / Open / Delete without
            // waiting for a RefreshAsync round-trip (Task S2 / Problem 2 —
            // the count was frozen at the initial value of 1 forever).
            ActiveSessionCount = Math.Max(1, Sessions.Sessions.Count);
            // Push the live message count so the status bar's "N msgs"
            // label updates immediately after the user sends a prompt
            // (Task D2 / Problem 2: status bar message count was stale).
            MessageCount = state.Lines.Length;
            this.OnPropertyChanged(nameof(StatusBrushKey));

            // Track token-usage history for the chart.
            TokenUsage.RecordUsage(state);
        });
    }

    /// <summary>Toggle sidebar visibility (Ctrl+B).</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    /// <summary>Toggle theme (Ctrl+Shift+T).</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

    /// <summary>Open command palette (Ctrl+P / Ctrl+Shift+P).</summary>
    [RelayCommand]
    private void OpenCommandPalette() => IsCommandPaletteOpen = true;

    /// <summary>Open settings dialog.</summary>
    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    /// <summary>Open provider browser.</summary>
    [RelayCommand]
    private void OpenProviderBrowser() => IsProviderBrowserOpen = true;

    /// <summary>
    ///     Open the provider/model picker flyout. Wired to a click handler on
    ///     the status-bar's <c>ModelLabel</c> TextBlock so the user can swap
    ///     models without leaving the chat view.
    /// </summary>
    [RelayCommand]
    private void OpenModelPicker() => IsModelPickerOpen = true;

    /// <summary>Open diff view.</summary>
    [RelayCommand]
    private void OpenDiff() => IsDiffOpen = true;

    /// <summary>Open token usage chart.</summary>
    [RelayCommand]
    private void OpenTokenUsage() => IsTokenUsageOpen = true;

    /// <summary>Switch active main view to one of: chat, code, diff.</summary>
    /// <param name="view">View name.</param>
    [RelayCommand]
    private void SwitchView(string view) => ActiveView = view;

    /// <summary>Push a toast to the visible toast collection.</summary>
    /// <param name="toast">Toast notification.</param>
    public void AddToast(ToastNotification toast)
    {
        _dispatcher.Post(() =>
        {
            Toasts.Add(toast);
            // Auto-dismiss after 4 seconds.
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
            {
                _dispatcher.Post(() => Toasts.Remove(toast));
            }, TaskScheduler.Default);
        });
    }
}
