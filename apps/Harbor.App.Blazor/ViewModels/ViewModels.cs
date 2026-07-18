using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Blazor.Services;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Blazor.ViewModels;

/// <summary>
///     View-model for the chat page. Owns the local demo transcript and
///     bridges to the singleton <see cref="UiStore"/> (TEA pattern) so
///     the StatusBar + TopBar reflect chat activity.
/// </summary>
/// <remarks>
///     <para>
///         <b>Architecture:</b> the VM is a thin projector. It does not own
///         the canonical chat state — that lives in <see cref="UiStore"/>
///         (driven by <see cref="AgentEvent"/> values from the real agent).
///         For the demo shell without a wired-up agent, the VM keeps a local
///         transcript so the chat UI is usable end-to-end. When the
///         <c>HostBuilder</c> from <c>Harbor.Cli</c> injects an
///         <c>IAgentRunner</c> + <c>TuiEffectHost</c>, the VM switches to
///         routing through <c>PromptAsync</c> and the local transcript is
///         no longer touched.
///     </para>
/// </remarks>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly UiStore _store;
    private readonly BlazorDispatcherAdapter _dispatcher;
    private readonly ILogger<ChatViewModel> _logger;
    private string _input = string.Empty;

    /// <summary>Construct the VM.</summary>
    public ChatViewModel(UiStore store, BlazorDispatcherAdapter dispatcher, ILogger<ChatViewModel> logger)
    {
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Current draft text in the input box.</summary>
    public string Input
    {
        get => _input;
        set => SetProperty(ref _input, value);
    }

    /// <summary>Immutable snapshot of the current UI state (for the status / top bars).</summary>
    public UiState State => _store.State;

    /// <summary>Local demo transcript. Empty when a real agent is wired up.</summary>
    public ObservableCollection<ChatLine> LocalLines { get; } = new();

    /// <summary>Send the current input as a user prompt. Clears the input box.</summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        string text = _input.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _input = string.Empty;
        OnPropertyChanged(nameof(Input));

        // Add to local demo transcript.
        await _dispatcher.InvokeAsync(() =>
        {
            LocalLines.Add(new ChatLine(ChatRole.User, text));
        }).ConfigureAwait(false);

        try
        {
            // Without a wired-up agent, echo an "unconfigured" assistant line
            // after a brief delay so the user sees the loop end-to-end.
            await Task.Delay(150).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                LocalLines.Add(new ChatLine(ChatRole.Assistant,
                    "No agent is wired up in this Blazor shell. Open Settings → Providers to configure an LLM provider and bind an agent."));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat send failed");
        }
    }

    /// <summary>Cancel the current agent run (stub — wires through to TuiEffectHost when agent is configured).</summary>
    [RelayCommand]
    public Task CancelAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>Clear the local transcript (does not delete the session on disk).</summary>
    [RelayCommand]
    public void Clear()
    {
        LocalLines.Clear();
    }

    private async void OnStoreChanged(object? _, UiStateChangedEventArgs __)
    {
        // Re-publish State so bound controls refresh on the render thread.
        await _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(State))).ConfigureAwait(false);
    }
}

/// <summary>
///     View-model for the Sessions page. Loads the list of saved sessions
///     from <see cref="SessionBrowserService"/> and exposes them for binding.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    private readonly SessionBrowserService _browser;
    private readonly BlazorDispatcherAdapter _dispatcher;

    /// <summary>Construct the VM.</summary>
    public SessionListViewModel(SessionBrowserService browser, BlazorDispatcherAdapter dispatcher)
    {
        _browser = browser;
        _dispatcher = dispatcher;
    }

    /// <summary>Currently loaded sessions.</summary>
    [ObservableProperty]
    private ObservableCollection<SessionSummary> _sessions = new();

    /// <summary>Whether the list is currently loading.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Reload the list from the configured directory.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _browser.ListAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                Sessions = new ObservableCollection<SessionSummary>(list);
            }).ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>
///     View-model for the Providers page. Loads the list of configured LLM
///     providers from <see cref="ProviderBrowserService"/>.
/// </summary>
public sealed partial class ProviderBrowserViewModel : ObservableObject
{
    private readonly ProviderBrowserService _browser;
    private readonly BlazorDispatcherAdapter _dispatcher;

    /// <summary>Construct the VM.</summary>
    public ProviderBrowserViewModel(ProviderBrowserService browser, BlazorDispatcherAdapter dispatcher)
    {
        _browser = browser;
        _dispatcher = dispatcher;
    }

    /// <summary>Currently loaded providers.</summary>
    [ObservableProperty]
    private ObservableCollection<ProviderSummary> _providers = new();

    /// <summary>Currently selected provider JSON, displayed in the editor.</summary>
    [ObservableProperty]
    private string? _selectedJson;

    /// <summary>Reload the list of providers.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        var list = await _browser.ListAsync().ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            Providers = new ObservableCollection<ProviderSummary>(list);
        }).ConfigureAwait(false);
    }

    /// <summary>Select a provider to view its raw JSON.</summary>
    [RelayCommand]
    public void Select(ProviderSummary summary)
    {
        SelectedJson = summary.RawJson;
    }
}

/// <summary>
///     View-model for the Settings page. Holds the app-wide theme, the
///     sessions directory, and basic agent defaults.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly SessionBrowserService _sessions;
    private readonly ToastService _toasts;

    /// <summary>Construct the VM.</summary>
    public SettingsViewModel(ThemeService theme, SessionBrowserService sessions, ToastService toasts)
    {
        _theme = theme;
        _sessions = sessions;
        _toasts = toasts;
        _theme.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object? _, ThemeChangedEventArgs __)
    {
        OnPropertyChanged(nameof(SelectedTheme));
    }

    /// <summary>The currently selected Catppuccin theme.</summary>
    public HarborTheme SelectedTheme
    {
        get => _theme.Current;
        set
        {
            _ = _theme.SetThemeAsync(value);
            _ = _toasts.InfoAsync($"Theme set to {value}");
        }
    }

    /// <summary>Directory to scan for saved JSONL sessions.</summary>
    [ObservableProperty]
    private string _sessionsDirectory = string.Empty;

    /// <summary>Apply the sessions directory to the browser service.</summary>
    [RelayCommand]
    public async Task ApplyDirectoryAsync()
    {
        _sessions.SetDirectory(string.IsNullOrWhiteSpace(SessionsDirectory) ? null : SessionsDirectory);
        await _toasts.SuccessAsync("Sessions directory updated").ConfigureAwait(false);
    }
}

/// <summary>
///     View-model for the Token Usage page. Builds a small in-memory series
///     of token counts (input vs output) for the chart.
/// </summary>
public sealed partial class TokenUsageViewModel : ObservableObject
{
    /// <summary>Construct the VM with sample data.</summary>
    public TokenUsageViewModel()
    {
        Series = new ObservableCollection<TokenPoint>(Seed());
    }

    /// <summary>Series of token samples.</summary>
    public ObservableCollection<TokenPoint> Series { get; }

    /// <summary>Total input tokens across the series.</summary>
    public long TotalIn
    {
        get
        {
            long sum = 0;
            foreach (var p in Series) sum += p.InputTokens;
            return sum;
        }
    }

    /// <summary>Total output tokens across the series.</summary>
    public long TotalOut
    {
        get
        {
            long sum = 0;
            foreach (var p in Series) sum += p.OutputTokens;
            return sum;
        }
    }

    /// <summary>Estimated cost in USD (sample pricing — replace with real usage from sessions).</summary>
    public decimal EstimatedCostUsd => (TotalIn * 0.000001m) + (TotalOut * 0.000002m);

    private static IEnumerable<TokenPoint> Seed()
    {
        var rng = new Random(42);
        for (int i = 0; i < 12; i++)
        {
            yield return new TokenPoint(
                Label: $"T{i + 1}",
                InputTokens: rng.Next(120, 1800),
                OutputTokens: rng.Next(40, 900));
        }
    }

    /// <summary>Append a new sample point (used by the demo).</summary>
    [RelayCommand]
    public void AddSample()
    {
        var rng = new Random();
        Series.Add(new TokenPoint($"T{Series.Count + 1}", rng.Next(120, 1800), rng.Next(40, 900)));
        OnPropertyChanged(nameof(TotalIn));
        OnPropertyChanged(nameof(TotalOut));
        OnPropertyChanged(nameof(EstimatedCostUsd));
    }
}

/// <summary>One token sample point on the chart.</summary>
public sealed record TokenPoint(string Label, int InputTokens, int OutputTokens);
