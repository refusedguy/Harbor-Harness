using CommunityToolkit.Mvvm.Input;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     serves as API for the app-shell view-model shared by every desktop app
///     (upon_layers_featureFlags): owns the status-bar projection from
///     <see cref="UiState" />, the overlay id-stack, sidebar/drawer flags, and
///     the relay commands every platform's shell exposes (Ctrl+P palette,
///     Ctrl+B sidebar, theme toggle, overlay pop). Platform VMs derive from
///     this, resolve the child VMs (Chat, Sessions, …) from DI, and forward
///     dispatcher ticks to <see cref="AdvanceDuration" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Dispatcher:</b> this base is dispatcher-agnostic. The platform
///         VM owns the UI-thread timer (Avalonia <c>DispatcherTimer</c>, WPF
///         <c>DispatcherTimer</c>, MAUI <c>IDispatcherTimer</c>) and calls
///         <see cref="AdvanceDuration" /> on each tick — no
///         <c>Avalonia.Threading</c> types leak into the abstractions layer.
///     </para>
///     <para>
///         <b>Overlay stack:</b> overlay ids ("palette", "settings", …) map to
///         boolean Is*Open flags via <see cref="OverlayIdToFlagProperty" /> —
///         data, not a string switch. The flag setter delegate is built once
///         per flag and cached in <see cref="OverlayFlagSetters" />; the
///         property lookup runs against the runtime (derived) type because the
///         source-generated flag properties live on the platform partial.
///     </para>
/// </remarks>
public abstract partial class MainViewModelBase : StoreSubscriberViewModel
{
    /// <summary>
    ///     Central registry mapping an overlay id (the same id pushed onto
    ///     <see cref="OverlayStack" />) to the name of the boolean property
    ///     that backs the overlay's <c>IsVisible</c> binding in the shell view.
    ///     Data, not tokens: <see cref="CloseOverlayCore" /> resolves the flag
    ///     through this table instead of a string switch.
    /// </summary>
    protected static readonly Dictionary<string, string> OverlayIdToFlagProperty = new()
    {
        ["palette"] = nameof(IsCommandPaletteOpen),
        ["settings"] = nameof(IsSettingsOpen),
        ["providerBrowser"] = nameof(IsProviderBrowserOpen),
        ["modelPicker"] = nameof(IsModelPickerOpen),
        ["diff"] = nameof(IsDiffOpen),
        ["tokenUsage"] = nameof(IsTokenUsageOpen),
        ["focusSession"] = nameof(IsFocusSessionOpen),
    };

    /// <summary>
    ///     Lazily-built setters keyed by flag property name. Reflection is
    ///     acceptable here: overlay close is user-input frequency (hot-key /
    ///     Escape), never a hot path — and the delegate is cached per flag.
    /// </summary>
    private static readonly Dictionary<string, Action<MainViewModelBase, bool>> OverlayFlagSetters = new();

    private readonly IThemeService _theme;

    private decimal _baseCost;
    private decimal _displayCost;
    private DateTime? _runningStartTime;

    [ObservableProperty]
    private int _activeSessionCount = 1;

    [ObservableProperty]
    private string _activeView = "chat";

    [ObservableProperty]
    private string _agentLabel = "code";

    [ObservableProperty]
    private decimal _costUsd;

    [ObservableProperty]
    private bool _hasOverlay;

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
    private bool _isRightDrawerOpen;

    [ObservableProperty]
    private bool _isRunning;

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

    /// <summary>Construct the shell view-model base.</summary>
    /// <param name="dispatcher">UI-thread marshaller / store binder.</param>
    /// <param name="theme">Theme service (toggle forwards here).</param>
    /// <param name="overlayStack">Overlay id-stack; a default in-process stack is used when null.</param>
    /// <param name="logger">Logger.</param>
    protected MainViewModelBase(
        IDispatcherAdapter dispatcher,
        IThemeService theme,
        IOverlayStack? overlayStack,
        ILogger logger)
        : base(dispatcher, logger)
    {
        _theme = theme;
        OverlayStack = overlayStack ?? new OverlayStackService();

        OverlayStack.Popped += id =>
        {
            if (id is not null)
            {
                CloseOverlayCore(id);
            }
        };
        OverlayStack.Changed += (_, _) => HasOverlay = OverlayStack.Current is not null;
        HasOverlay = OverlayStack.Current is not null;
    }

    /// <summary>The overlay id-stack shared with the shell view.</summary>
    public IOverlayStack OverlayStack { get; }

    /// <summary>Rolling window of TokensIn+TokensOut samples (max 60) for the status-bar sparkline.</summary>
    public ObservableCollection<double> TokenHistory { get; } = new();

    /// <summary>Brush resource key for the current status (resolved by the platform theme).</summary>
    public string StatusBrushKey => StatusMappers.StatusToBrushKey(StatusText);

    /// <summary>Compact "1.2k" text for <see cref="TokensIn" />.</summary>
    public string TokensInText => StatusMappers.TokensToCompact(TokensIn);

    /// <summary>Compact "1.2k" text for <see cref="TokensOut" />.</summary>
    public string TokensOutText => StatusMappers.TokensToCompact(TokensOut);

    /// <summary>USD text for <see cref="CostUsd" />.</summary>
    public string CostText => StatusMappers.CostToUsd(CostUsd);

    /// <summary>"1h 2m" duration text while the agent is running; empty otherwise.</summary>
    public string RunningDurationText =>
        _runningStartTime is { } start ? FormatDuration(DateTime.UtcNow - start) : string.Empty;

    /// <summary>Smoothly interpolated cost text while running (ticks at ~0.0001$/s over the base cost).</summary>
    public string AnimatedCostText => StatusMappers.CostToUsd(_displayCost);

    /// <summary>True while the animated-cost label should be visible (agent running).</summary>
    public bool ShowAnimatedCost => _runningStartTime is not null;

    /// <summary>
    ///     Raised when the shell should refresh the running-duration /
    ///     animated-cost labels. The platform VM's UI-thread timer forwards
    ///     every tick here; subscribers re-read
    ///     <see cref="RunningDurationText" /> / <see cref="AnimatedCostText" />.
    /// </summary>
    public event Action? DurationTick;

    /// <summary>
    ///     Advance the running-duration / animated-cost projection by one tick.
    ///     Called by the platform VM's UI-thread timer. Returns false when the
    ///     agent stopped (the platform timer should stop itself).
    /// </summary>
    /// <returns>True while the agent is still running; false once stopped.</returns>
    public bool AdvanceDuration()
    {
        if (_runningStartTime is not { } start)
        {
            return false;
        }

        var elapsed = DateTime.UtcNow - start;
        _displayCost = _baseCost + (decimal)(elapsed.TotalSeconds * 0.0001);

        OnPropertyChanged(nameof(RunningDurationText));
        OnPropertyChanged(nameof(AnimatedCostText));
        DurationTick?.Invoke();
        return true;
    }

    /// <summary>
    ///     Project the shared <see cref="UiState" /> onto the status-bar /
    ///     shell-chrome properties. Called by the derived class's
    ///     <see cref="StoreSubscriberViewModel.OnStoreChanged" /> before it
    ///     updates its own platform-specific state (ShellStatus mirror,
    ///     TokenUsage recording, BaseCost refresh from running state, …).
    /// </summary>
    /// <param name="state">The newest UiState.</param>
    /// <param name="sessionCount">Number of live sessions (clamped to ≥1).</param>
    protected void ProjectShellState(UiState state, int sessionCount)
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
        ActiveSessionCount = Math.Max(1, sessionCount);
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
        {
            TokenHistory.RemoveAt(0);
        }

        if (IsRunning && !wasRunning)
        {
            _runningStartTime = DateTime.UtcNow;
            _baseCost = state.Cost.CostUsd;
            _displayCost = state.Cost.CostUsd;
        }
        else if (!IsRunning && wasRunning)
        {
            _runningStartTime = null;
            _displayCost = state.Cost.CostUsd;
        }
        else if (IsRunning)
        {
            _baseCost = state.Cost.CostUsd;
        }
    }

    /// <summary>Toggle sidebar visibility (Ctrl+B).</summary>
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    /// <summary>Toggle theme (Ctrl+Shift+T).</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

    /// <summary>Open the command palette (Ctrl+P / Ctrl+Shift+P).</summary>
    [RelayCommand]
    private void OpenCommandPalette() => OpenOverlay("palette");

    /// <summary>Open the settings dialog.</summary>
    [RelayCommand]
    private void OpenSettings() => OpenOverlay("settings");

    /// <summary>Open the provider browser.</summary>
    [RelayCommand]
    private void OpenProviderBrowser() => OpenOverlay("providerBrowser");

    /// <summary>
    ///     Open the provider/model picker flyout. Wired to a click handler on
    ///     the status-bar's model label so the user can swap models without
    ///     leaving the chat view.
    /// </summary>
    [RelayCommand]
    private void OpenModelPicker() => OpenOverlay("modelPicker");

    /// <summary>Open the diff view.</summary>
    [RelayCommand]
    private void OpenDiff() => OpenOverlay("diff");

    /// <summary>Open the token-usage chart.</summary>
    [RelayCommand]
    private void OpenTokenUsage() => OpenOverlay("tokenUsage");

    /// <summary>Pop the topmost overlay (Back-button in the chrome).</summary>
    [RelayCommand(CanExecute = nameof(CanPopOverlay))]
    private void OverlayPop() => OverlayStack.PopTop();

    private bool CanPopOverlay() => OverlayStack.Current is not null;

    /// <summary>Switch the active main view (e.g. "chat", "code", "diff").</summary>
    /// <param name="view">View name.</param>
    [RelayCommand]
    private void SwitchView(string view) => ActiveView = view;

    /// <summary>Push an overlay: set its Is*Open flag and record the id on the stack.</summary>
    /// <param name="overlayId">One of the ids in <see cref="OverlayIdToFlagProperty" />.</param>
    protected void OpenOverlay(string overlayId)
    {
        CloseOverlayViaFlag(overlayId, true);
        OverlayStack.Push(overlayId);
    }

    /// <summary>Set an overlay's Is*Open flag directly (e.g. FocusSession toggle in the derived VM).</summary>
    /// <param name="overlayId">Overlay id from <see cref="OverlayIdToFlagProperty" />.</param>
    /// <param name="isOpen">New flag value.</param>
    protected void SetOverlayFlag(string overlayId, bool isOpen) => CloseOverlayViaFlag(overlayId, isOpen);

    private void CloseOverlayViaFlag(string overlayId, bool value)
    {
        if (!OverlayIdToFlagProperty.TryGetValue(overlayId, out var propertyName))
        {
            return;
        }

        if (!OverlayFlagSetters.TryGetValue(propertyName, out var setter))
        {
            // The flag properties are source-generated on the derived platform
            // partial, so the lookup MUST run against the runtime type, not
            // typeof(MainViewModelBase).
            var property = GetType().GetProperty(propertyName);
            if (property?.SetMethod is null)
            {
                return;
            }
            setter = (vm, v) => property.SetValue(vm, v);
            OverlayFlagSetters[propertyName] = setter;
        }

        setter(this, value);
    }

    /// <summary>
    ///     Close the overlay identified by <paramref name="id" /> by clearing
    ///     its backing boolean flag. The id → property mapping lives in
    ///     <see cref="OverlayIdToFlagProperty" />; the setter delegate is
    ///     built once per flag and cached in <see cref="OverlayFlagSetters" />.
    /// </summary>
    protected void CloseOverlayCore(string id) => CloseOverlayViaFlag(id, false);

    /// <summary>
    ///     Close the topmost overlay, if any. Single mechanism used by
    ///     Escape handling, backdrop clicks, and shell close buttons:
    ///     peek the top id, clear its flag, then pop the stack.
    /// </summary>
    /// <returns>True if an overlay was closed; false when the stack is empty.</returns>
    public bool CloseTopOverlay()
    {
        var top = OverlayStack.Current;
        if (top is null)
        {
            return false;
        }

        CloseOverlayCore(top);
        OverlayStack.PopTop();
        return true;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }
        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds}s";
        }
        return $"{duration.Seconds}s";
    }
}
