using Harbor.Ui.Framework.State;

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
///     <para>
///         <b>Suggestions:</b> the contextual-suggestion record lives in the
///         platform layer today (it carries an <c>ICommand</c>); the
///         collection here is typed to it via a using-alias so the base
///         compiles against net8.0 + the Avalonia app.
///     </para>
/// </remarks>
public abstract partial class ChatViewModelBase : ViewModelBase
{
    /// <summary>User input text bound to the chat input box.</summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>True while the agent loop is running (waiting for first token OR streaming).</summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>True while tokens are actively arriving.</summary>
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>True while the agent is running but not yet streaming (thinking / tool-use).</summary>
    [ObservableProperty]
    private bool _isThinking;

    /// <summary>
    ///     Human-readable status message shown while the agent is running
    ///     (e.g. <c>"Agent is running…"</c>). Cleared on completion.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True while older history is being loaded (pull-to-refresh).</summary>
    [ObservableProperty]
    private bool _isLoadingHistory;

    /// <summary>Pull-to-refresh progress (0..1).</summary>
    [ObservableProperty]
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
    protected ChatViewModelBase(ILogger logger) : base(logger)
    {
    }

    /// <summary>Visible chat lines, projected for the view layer.</summary>
    public ObservableCollection<ChatLineViewModel> Lines { get; } = new();

    /// <summary>Contextual quick-action suggestions shown above the composer.</summary>
    public ObservableCollection<ChatSuggestion> ContextualSuggestions { get; } = new();

    /// <summary>
    ///     Visible tool-call cards (one per tool invocation). Updated
    ///     incrementally as <see cref="ChatRole.Tool" /> /
    ///     <see cref="ChatRole.ToolResult" /> lines arrive.
    /// </summary>
    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();

    /// <summary>Pull-to-refresh status text for the indicator label.</summary>
    public string PullRefreshStatusText => IsLoadingHistory
        ? "Loading older messages..."
        : PullProgress >= 0.5 ? "Release to refresh" : "Pull to load older messages";

    /// <summary>
    ///     Context-aware placeholder for the composer input. Changes based on
    ///     the current agent state so the user always knows what's happening.
    /// </summary>
    public string InputPlaceholder => GetPlaceholder();

    partial void OnIsThinkingChanged(bool value) => OnPropertyChanged(nameof(InputPlaceholder));
    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(InputPlaceholder));
    partial void OnIsAgentRunningChanged(bool value) => OnPropertyChanged(nameof(InputPlaceholder));

    partial void OnIsLoadingHistoryChanged(bool value) => OnPropertyChanged(nameof(PullRefreshStatusText));
    partial void OnPullProgressChanged(double value) => OnPropertyChanged(nameof(PullRefreshStatusText));

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

    /// <summary>
    ///     Reset the chat state — clears lines + tool-call cards + streaming
    ///     flags so the next render pass starts fresh. Called by the derived
    ///     Clear command and when switching sessions.
    /// </summary>
    protected void ResetChatState()
    {
        Lines.Clear();
        ToolCalls.Clear();
        IsStreaming = false;
        IsThinking = false;
        IsAgentRunning = false;
        StatusMessage = string.Empty;
        StreamingBuffer = string.Empty;
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

/// <summary>
///     One contextual quick-action suggestion shown above the composer.
///     UI-agnostic: carries label + prompt + the callback to run on activation
///     (no <c>ICommand</c> so it stays net8.0-compatible for WPF/MAUI).
/// </summary>
/// <param name="Label">Short button label (e.g. "⚡ Explain this codebase").</param>
/// <param name="Prompt">The prompt to submit when activated.</param>
/// <param name="Run">Callback invoked on activation.</param>
public sealed record ChatSuggestion(string Label, string Prompt, Action Run);
