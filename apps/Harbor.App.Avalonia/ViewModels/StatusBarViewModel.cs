using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Converters;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Status bar view-model — extracted from <see cref="MainViewModel" />
///     (Task R28: React-style component decomposition). Owns the
///     status text, provider/model/agent labels, token counts, cost,
///     message count, session count, and the "agent is running" flag.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists:</b> MainViewModel was 311 lines mixing
///         shell-level concerns (sidebar / palette / settings visibility)
///         with status-bar concerns (labels, tokens, cost). Splitting the
///         status bar out shrinks MainVM and lets the status bar be
///         tested / re-skinned independently — same pattern as a React
///         <c>&lt;StatusBar/&gt;</c> component receiving props from the
///         parent shell.
///     </para>
///     <para>
///         <b>Wiring:</b> MainViewModel subscribes to
///         <see cref="AvaloniaDispatcherAdapter.OnUiThread" /> and forwards
///         each <see cref="UiState" /> transition to
///         <see cref="ApplyState" />. The status bar view binds directly
///         to this VM's properties.
///     </para>
///     <para>
///         <b>Cost / token display strings:</b> the raw numeric
///         properties (<see cref="TokensIn" />, <see cref="TokensOut" />,
///         <see cref="CostUsd" />) remain bound for the chart; the
///         formatted strings (<see cref="TokensInText" />,
///         <see cref="TokensOutText" />, <see cref="CostText" />) are
///         derived via <see cref="StatusMappers" /> for the status bar
///         labels.
///     </para>
/// </remarks>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly AvaloniaDispatcherAdapter _dispatcher;
    private readonly ILogger<StatusBarViewModel> _logger;

    [ObservableProperty]
    private int _activeSessionCount = 1;

    [ObservableProperty]
    private string _agentLabel = "code";

    [ObservableProperty]
    private decimal _costUsd;

    [ObservableProperty]
    private bool _isRunning;

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

    public StatusBarViewModel(
        AvaloniaDispatcherAdapter dispatcher,
        ILogger<StatusBarViewModel> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Status bar color key based on <see cref="StatusText" />.</summary>
    public string StatusBrushKey => StatusMappers.StatusToBrushKey(StatusText);

    /// <summary>Compact display for input tokens ("1.2K" / "12K" / "1.4M").</summary>
    public string TokensInText => StatusMappers.TokensToCompact(TokensIn);

    /// <summary>Compact display for output tokens.</summary>
    public string TokensOutText => StatusMappers.TokensToCompact(TokensOut);

    /// <summary>USD cost as "$0.0123".</summary>
    public string CostText => StatusMappers.CostToUsd(CostUsd);

    /// <summary>
    ///     Apply a fresh <see cref="UiState" /> snapshot to the status bar.
    ///     Called by <see cref="MainViewModel" /> on every UiStore transition
    ///     (already on the UI thread).
    /// </summary>
    /// <param name="state">The fresh state.</param>
    /// <param name="sessionCount">Live session count (from the sidebar).</param>
    public void ApplyState(UiState state, int sessionCount)
    {
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

        // Trigger re-evaluation of computed properties.
        this.OnPropertyChanged(nameof(StatusBrushKey));
        this.OnPropertyChanged(nameof(TokensInText));
        this.OnPropertyChanged(nameof(TokensOutText));
        this.OnPropertyChanged(nameof(CostText));
    }
}
