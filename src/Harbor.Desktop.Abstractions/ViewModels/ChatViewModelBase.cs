using ChatLineViewModel = Harbor.Ui.Framework.ViewModels.ChatLineViewModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;
using ToolCallViewModel = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;

namespace Harbor.Desktop.Abstractions.ViewModels;

/// <summary>
///     Base for the streaming chat view-model shared by every desktop app.
///     Holds the observable chat-line collection and the streaming state;
///     platform VMs derive from this and add dispatcher wiring + the
///     platform-specific store/effect plumbing.
/// </summary>
/// <remarks>
///     <para>
///         The <see cref="RoleBrushKey" /> lookup is framework-agnostic and
///         lives here so all platforms use the same resource-key names. Each
///         platform's theme dictionary (e.g. Avalonia <c>Dark.axaml</c>) must
///         define those keys.
///     </para>
///     <para>
///         <see cref="ChatLineViewModel" /> is the canonical line record from
///         <c>Harbor.Ui.Framework.ViewModels</c> (re-exported via this
///         assembly's global usings) — do not redeclare it here.
///     </para>
/// </remarks>
public abstract partial class ChatViewModelBase : StoreSubscriberViewModel
{
    /// <summary>User input text bound to the chat input box.</summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>True while the agent loop is running (waiting for first token OR streaming).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    private bool _isAgentRunning;

    /// <summary>True while tokens are actively arriving.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    private bool _isStreaming;

    /// <summary>True while the agent is running but not yet streaming (thinking / tool-use).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    private bool _isThinking;

    /// <summary>
    ///     Human-readable status message shown while the agent is running
    ///     (e.g. <c>"Agent is running…"</c>). Cleared on completion.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True while older history is being loaded (pull-to-refresh).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullRefreshStatusText))]
    private bool _isLoadingHistory;

    /// <summary>Pull-to-refresh progress (0..1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullRefreshStatusText))]
    private double _pullProgress;

    /// <summary>Pull-to-refresh pixel offset.</summary>
    [ObservableProperty]
    private double _pullOffset;

    /// <summary>Zoom/content scale factor (Ctrl+=/Ctrl+-).</summary>
    [ObservableProperty]
    private double _contentScale = 1.0;

    /// <summary>True while there may be older history to load.</summary>
    [ObservableProperty]
    private bool _canLoadOlder = true;

    /// <summary>True while the pull indicator is visible.</summary>
    [ObservableProperty]
    private bool _showPullIndicator;

    /// <summary>Active streaming buffer (partial assistant text).</summary>
    [ObservableProperty]
    private string _streamingBuffer = string.Empty;

    /// <summary>Construct a <see cref="ChatViewModelBase" />.</summary>
    /// <param name="dispatcher">UI-thread marshaller / store binder.</param>
    /// <param name="logger">Logger.</param>
    protected ChatViewModelBase(IDispatcherAdapter dispatcher, ILogger logger)
        : base(dispatcher, logger)
    {
        Select(state => state.Chat.IsStreaming, v => IsStreaming = v);
        Select(state => state.Chat.IsAgentRunning, v => IsAgentRunning = v);
        Select(state => state.Chat.Active.TextBuffer ?? string.Empty, v => StreamingBuffer = v);
        Select(state => state.Ui.Input.Text, v => InputText = v);
        Select(state => state.Chat.IsAgentRunning && !state.Chat.IsStreaming, v => IsThinking = v);
        Select(state => state.Chat.IsAgentRunning
            ? (state.Chat.IsStreaming ? "Streaming response…" : "Agent is running…")
            : "Idle", v => StatusMessage = v);
    }

    /// <summary>Visible chat lines, projected for the view layer.</summary>
    public ObservableCollection<ChatLineViewModel> Lines { get; } = new();

    /// <summary>
    ///     Visible tool-call cards (one per tool invocation), projected from
    ///     <see cref="ChatDomainState.ToolCalls" /> by
    ///     <see cref="ToolCallProjection" /> (#680).
    /// </summary>
    /// <remarks>
    ///     This collection used to be rebuilt by parsing the rendered transcript
    ///     line <c>"→ edit {…}"</c> back into a call, with a hand-written glyph
    ///     table for ten builtin tools. Both are gone: the reducer publishes the
    ///     structure, the view-model reads it, and the glyph is the calling tool's
    ///     own.
    /// </remarks>
    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();

    /// <summary>
    ///     Ф-A1b (sprint 4.5): single chronological timeline mixing chat rows
    ///     and tool-call cards in true order — replaces two stacked
    ///     ItemsControls whose VirtualizingStackPanels never virtualized
    ///     (infinite-height StackPanel inside a ScrollViewer) and which broke
    ///     chronology by rendering every tool card above every message.
    ///     Items are ChatLineViewModel or ToolCallViewModel; XAML picks the
    ///     template by item type.
    /// </summary>
    public ObservableCollection<object> Timeline { get; } = new();

    /// <summary>Pull-to-refresh status text for the indicator label.</summary>
    public string PullRefreshStatusText => IsLoadingHistory
        ? "Loading older messages..."
        : PullProgress >= 0.5 ? "Release to refresh" : "Pull to load older messages";

    /// <summary>
    ///     Context-aware placeholder for the composer input. Changes based on
    ///     the current agent state so the user always knows what's happening.
    /// </summary>
    public string InputPlaceholder => GetPlaceholder();

    private string GetPlaceholder()
    {
        if (IsThinking)
        {
            return "Agent is thinking…";
        }
        if (IsStreaming)
        {
            return "Receiving response…";
        }
        if (IsAgentRunning)
        {
            return "Agent is running…";
        }
        return "Ask Harbor anything…";
    }

    /// <summary>Sync an <see cref="ObservableCollection{T}" /> from an <see cref="ImmutableArray{T}" />.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="target">Collection to update in-place.</param>
    /// <param name="source">Immutable source snapshot.</param>
    protected void SyncCollection<T>(ObservableCollection<T> target, ImmutableArray<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private void SyncLines(ImmutableArray<ChatLine> source)
    {
        Lines.Clear();
        foreach (var line in source)
            Lines.Add(new ChatLineViewModel(line.Role, line.Text));
    }

    private void SyncToolCalls(UiState state) => ToolCallProjection.Reconcile(state, ToolCalls);

    /// <summary>
    ///     Apply declared selectors against the new state snapshot and sync
    ///     the observable chat-line / tool-call collections.
    /// </summary>
    /// <param name="state">The new <see cref="UiState" /> snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
        SyncLines(state.Chat.Lines);
        SyncToolCalls(state);
    }

    /// <summary>Resource-key lookup for the role's accent color.</summary>
    /// <param name="role">Chat role.</param>
    /// <returns>A resource key like <c>"ChatUserBrush"</c> resolved by the platform theme.</returns>
    public static string RoleBrushKey(ChatRole role) => role switch
    {
        ChatRole.User => "ChatUserBrush",
        ChatRole.Assistant => "ChatAssistantBrush",
        ChatRole.Thinking => "ChatThinkingBrush",
        ChatRole.Tool => "ChatToolBrush",
        ChatRole.ToolResult => "ChatToolResultBrush",
        ChatRole.System => "ChatSystemBrush",
        ChatRole.Error => "ChatErrorBrush",
        _ => "ChatAssistantBrush"
    };
}
