using ChatLineViewModel = Harbor.Ui.Framework.ViewModels.ChatLineViewModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;
using ToolCallStatus = Harbor.Ui.Framework.ViewModels.ToolCallStatus;
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
        Select(state => state.IsStreaming, v => IsStreaming = v);
        Select(state => state.IsAgentRunning, v => IsAgentRunning = v);
        Select(state => state.Active.TextBuffer ?? string.Empty, v => StreamingBuffer = v);
        Select(state => state.Input.Text, v => InputText = v);
        Select(state => state.IsAgentRunning && !state.IsStreaming, v => IsThinking = v);
        Select(state => state.IsAgentRunning
            ? (state.IsStreaming ? "Streaming response…" : "Agent is running…")
            : "Idle", v => StatusMessage = v);
    }

    /// <summary>Visible chat lines, projected for the view layer.</summary>
    public ObservableCollection<ChatLineViewModel> Lines { get; } = new();

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

    private void SyncToolCalls(UiState state)
    {
        var existingById = new Dictionary<string, ToolCallViewModel>(StringComparer.Ordinal);
        for (int i = 0; i < ToolCalls.Count; i++)
            existingById[ToolCalls[i].Id] = ToolCalls[i];

        var ordered = new List<ToolCallViewModel>(state.Lines.Length);

        for (int i = 0; i < state.Lines.Length; i++)
        {
            var line = state.Lines[i];
            if (line.ToolCallId is null) continue;

            if (line.Role == ChatRole.Tool)
            {
                var parsed = ParseToolLine(line.Text, line.ToolCallId);
                if (parsed is null) continue;

                if (existingById.TryGetValue(parsed.Id, out var existing))
                {
                    existing.ToolName = parsed.ToolName;
                    existing.ArgsPreview = parsed.ArgsPreview;
                    existing.IconText = parsed.IconText;
                    if (parsed.IsDiffTool)
                    {
                        existing.IsDiffTool = true;
                        existing.DiffFilePath = parsed.DiffFilePath;
                        existing.DiffPreview = parsed.DiffPreview;
                        existing.DiffFull = parsed.DiffFull;
                    }
                    ordered.Add(existing);
                }
                else
                {
                    ordered.Add(parsed);
                }
            }
            else if (line.Role == ChatRole.ToolResult && existingById.TryGetValue(line.ToolCallId!, out var entry))
            {
                entry.Complete(
                    line.Text.StartsWith("✗", StringComparison.Ordinal) ? ToolCallStatus.Error : ToolCallStatus.Success,
                    FormatResultPreview(line.Text),
                    TimeSpan.Zero);
                ordered.Add(entry);
            }
        }

        if (ToolCalls.Count != ordered.Count)
        {
            ToolCalls.Clear();
            foreach (var tc in ordered)
                ToolCalls.Add(tc);
            return;
        }

        for (int i = 0; i < ToolCalls.Count; i++)
        {
            if (ToolCalls[i] != ordered[i])
                ToolCalls[i] = ordered[i];
        }
    }

    private static ToolCallViewModel? ParseToolLine(string text, string toolCallId)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2 || text[0] != '→')
            return null;

        int spaceIdx = text.IndexOf(' ', 2);
        string toolName;
        string argsJson;
        if (spaceIdx < 0)
        {
            toolName = text[2..];
            argsJson = "{}";
        }
        else
        {
            toolName = text[2..spaceIdx];
            argsJson = text[(spaceIdx + 1)..].TrimStart();
        }

        var vm = new ToolCallViewModel
        {
            Id = toolCallId,
            ToolName = toolName,
            ArgsPreview = argsJson == "{}" ? string.Empty : argsJson,
            IconText = toolName switch
            {
                "edit" => "✎",
                "write" => "✚",
                "read" => "▸",
                "patch" => "⌥",
                "bash" => "$",
                "grep" => "🔍",
                "glob" => "🌐",
                "ls" => "📁",
                "task" => "☐",
                "web_fetch" => "🌍",
                _ => "?"
            }
        };

        return vm;
    }

    private static string FormatResultPreview(string resultText)
    {
        if (string.IsNullOrEmpty(resultText))
            return string.Empty;
        if (resultText.Length >= 2 && (resultText[0] == '✓' || resultText[0] == '✗') && resultText[1] == ' ')
            return resultText[2..];
        return resultText;
    }

    /// <summary>
    ///     Apply declared selectors against the new state snapshot and sync
    ///     the observable chat-line / tool-call collections.
    /// </summary>
    /// <param name="state">The new <see cref="UiState" /> snapshot.</param>
    protected override void OnStoreChanged(UiState state)
    {
        ApplySelectors(state);
        SyncLines(state.Lines);
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
