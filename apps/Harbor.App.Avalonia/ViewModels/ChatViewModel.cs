using Harbor.Abstractions.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Streaming chat view-model. Subscribes to <see cref="UiStore"/> transitions on the
///     UI thread and projects them into an observable chat-line collection. User input is
///     forwarded through <see cref="TuiEffectHost"/> so the agent loop runs out-of-band.
/// </summary>
/// <remarks>
///     <para>
///         <b>Task R1 — Tool-call cards:</b> alongside the existing
///         <see cref="Lines"/> transcript, this view-model now also
///         projects <see cref="ChatRole.Tool"/> /
///         <see cref="ChatRole.ToolResult"/> lines into
///         <see cref="ToolCalls"/> — an observable collection of
///         <see cref="ToolCallViewModel"/> cards. Each tool call is
///         coalesced: the start event creates a card in
///         <see cref="ToolCallStatus.Running"/> state; the matching
///         result event completes it (Success/Error + duration).
///     </para>
///     <para>
///         Coalescing is keyed on <c>ToolName + index</c> — the simplest
///         stable key that works without a real correlation id from
///         <c>AgentEvent</c>. A future refactor should thread a real
///         <c>ToolCallId</c> through <c>UiState</c>.
///     </para>
/// </remarks>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly SessionManager? _sessionManager;
    private readonly AvaloniaDispatcherAdapter _dispatcher;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly ToastService _toasts;
    private int _renderedLineCount;
    private readonly Dictionary<string, ToolCallViewModel> _toolCallById = new();
    private readonly Stopwatch _toolCallStopwatch = new();

    private readonly EventHandler<UiState> _onStoreChanged;

    /// <summary>Construct the chat view-model.</summary>
    public ChatViewModel(
        UiStore store,
        TuiEffectHost effects,
        SessionManager? sessionManager,
        AvaloniaDispatcherAdapter dispatcher,
        ILogger<ChatViewModel> logger,
        ToastService toasts)
    {
        _store = store;
        _effects = effects;
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _logger = logger;
        _toasts = toasts;

        _onStoreChanged = (_, state) => OnStoreChanged(state);
        // NOTE: _dispatcher.Bind(_store) is intentionally NOT called here — the
        // composition root (AppHost.BuildAsync) binds the dispatcher to the UiStore
        // exactly once, idempotently. Subscribing here would duplicate the binding
        // (now prevented by the idempotent Bind, but we still centralise the call).
        _dispatcher.OnUiThread += _onStoreChanged;
    }

    /// <summary>Visible chat lines.</summary>
    public ObservableCollection<ChatLineViewModel> Lines { get; } = new();

    /// <summary>
    ///     Visible tool-call cards (one per tool invocation). Updated
    ///     incrementally as <see cref="ChatRole.Tool"/> /
    ///     <see cref="ChatRole.ToolResult"/> lines arrive.
    /// </summary>
    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _streamingBuffer = string.Empty;

    [ObservableProperty]
    private bool _isThinking;

    /// <summary>
    ///     True while the agent loop is running (waiting for first token OR
    ///     streaming). Mirrors <see cref="UiState.IsAgentRunning"/>. Bound to
    ///     the chat area's "Agent is running…" indicator + the Stop button's
    ///     IsVisible. Distinct from <see cref="IsStreaming"/> (only true while
    ///     tokens are actively arriving) and <see cref="IsThinking"/> (true
    ///     while running but not yet streaming).
    /// </summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>
    ///     Human-readable status message shown in the chat area while the
    ///     agent is running (e.g. <c>"Agent is running…"</c>). Cleared on
    ///     completion. Bound to the streaming-indicator border's TextBlock.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>The role-color lookup for the view.</summary>
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

    private void OnStoreChanged(UiState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Debug: log state changes to diagnose missing messages
            _logger.LogDebug("OnStoreChanged: lines={Lines}, rendered={Rendered}, streaming={Streaming}, agentRunning={Running}, textBufLen={TextBufLen}",
                state.Lines.Length, _renderedLineCount, state.IsStreaming, state.IsAgentRunning, state.Active.TextBuffer?.Length ?? 0);
            
            // Append only new lines (idempotent under repeated notifications).
            while (_renderedLineCount < state.Lines.Length)
            {
                var line = state.Lines[_renderedLineCount];
                _logger.LogDebug("Adding line {Index}: role={Role}, textLen={TextLen}", _renderedLineCount, line.Role, line.Text?.Length ?? 0);
                // Tool / ToolResult lines are projected into ToolCalls
                // cards instead of (or in addition to) the transcript.
                if (line.Role is ChatRole.Tool or ChatRole.ToolResult)
                {
                    ProjectToolLine(line, _renderedLineCount);
                }
                else
                {
                    Lines.Add(new ChatLineViewModel(line.Role, line.Text ?? string.Empty));
                }
                _renderedLineCount++;
            }

            IsStreaming = state.IsStreaming;
            IsThinking = state.IsAgentRunning && !state.IsStreaming;
            IsAgentRunning = state.IsAgentRunning;

            // Update session status based on agent state (Task S2 / Problem 1).
            //   IsAgentRunning → Working (amber pulse)
            //   !IsAgentRunning && state.Status == "error" → Error (red)
            //   !IsAgentRunning && last line was assistant → Done (green)
            //   otherwise → Idle (grey)
            if (_sessionManager?.Active is { } activeSession)
            {
                SessionStatus newStatus;
                if (state.IsAgentRunning)
                {
                    newStatus = SessionStatus.Working;
                }
                else if (string.Equals(state.Status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    newStatus = SessionStatus.Error;
                }
                else if (state.Lines.Length > 0
                    && state.Lines[^1].Role == ChatRole.Assistant)
                {
                    newStatus = SessionStatus.Done;
                }
                else
                {
                    newStatus = SessionStatus.Idle;
                }
                _sessionManager.SetStatus(activeSession.Id, newStatus);

                // Push the live message count so the sidebar row's "N msgs"
                // label tracks new messages without a full RefreshAsync
                // round-trip (Task S2 / Problem 2).
                _sessionManager.NotifyMessageCount(activeSession.Id, state.Lines.Length);
            }
            StatusMessage = state.IsAgentRunning
                ? (state.IsStreaming ? "Streaming response…" : "Agent is running…")
                : string.Empty;
            StreamingBuffer = state.Active.TextBuffer ?? string.Empty;
        });
    }

    /// <summary>
    ///     Project a <see cref="ChatRole.Tool"/> (start) or
    ///     <see cref="ChatRole.ToolResult"/> (end) line into a
    ///     <see cref="ToolCallViewModel"/> card. Coalesces start/end
    ///     pairs by tool name + index.
    /// </summary>
    private void ProjectToolLine(ChatLine line, int lineIndex)
    {
        // Parse "tool_name: args" or "tool_name: result" — best-effort.
        // The agent loop formats these; we tolerate any shape.
        string text = line.Text ?? string.Empty;
        string toolName = text;
        string payload = string.Empty;
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            toolName = text[..colon].Trim();
            payload = text[(colon + 1)..].Trim();
        }

        // Coalescing key — tool name + line index parity. Two adjacent
        // Tool/ToolResult lines for the same tool name pair up.
        string id = $"{toolName}-{lineIndex / 2}";

        if (line.Role == ChatRole.Tool)
        {
            // Start event — create a running card.
            if (!_toolCallById.TryGetValue(id, out var card))
            {
                card = new ToolCallViewModel
                {
                    Id = id,
                    ToolName = toolName,
                    IconText = IconForTool(toolName),
                    Status = ToolCallStatus.Running,
                    ArgsPreview = TruncateForPreview(payload),
                    IsExpanded = false,
                };
                _toolCallById[id] = card;
                ToolCalls.Add(card);
                _toolCallStopwatch.Restart();
            }
        }
        else // ChatRole.ToolResult
        {
            // End event — complete the matching card. Fall back to the
            // most recent running card if the coalescing key missed.
            ToolCallViewModel? card = null;
            if (_toolCallById.TryGetValue(id, out card))
            {
                // Found by id — use it.
            }
            else
            {
                // Fall back: find the most-recently-added running card.
                card = ToolCalls.LastOrDefault(c => c.Status == ToolCallStatus.Running);
                if (card is not null)
                {
                    _toolCallById[id] = card;
                }
            }

            if (card is not null)
            {
                bool isError = payload.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                    || payload.StartsWith("fail", StringComparison.OrdinalIgnoreCase);
                card.Complete(
                    status: isError ? ToolCallStatus.Error : ToolCallStatus.Success,
                    resultPreview: TruncateForPreview(payload),
                    duration: _toolCallStopwatch.Elapsed);
            }
            else
            {
                // No matching start — create a completed card standalone.
                var standalone = new ToolCallViewModel
                {
                    Id = id,
                    ToolName = toolName,
                    IconText = IconForTool(toolName),
                    Status = ToolCallStatus.Success,
                    ResultPreview = TruncateForPreview(payload),
                };
                _toolCallById[id] = standalone;
                ToolCalls.Add(standalone);
            }
        }
    }

    private static string TruncateForPreview(string text, int max = 800)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string IconForTool(string toolName) => toolName.ToLowerInvariant() switch
    {
        "read" => "📖",
        "write" => "✍️",
        "edit" => "✏️",
        "patch" => "🩹",
        "bash" or "sh" => "🖥️",
        "grep" or "ripgrep" => "🔍",
        "glob" => "🌐",
        "ls" => "📁",
        "tree" => "🌲",
        "webfetch" or "fetch" => "🌐",
        "task" => "📋",
        "notebook" => "📓",
        "mcp" => "🔌",
        _ => "🔧",
    };

    /// <summary>Submit the current input text. Plain Enter (handled in view) triggers this.</summary>
    /// <remarks>
    ///     Task U4 — CanExecute wiring. <see cref="CanSend"/> gates the Send
    ///     button so it greys out when the input is empty. The
    ///     <see cref="OnInputTextChanged"/> partial (generated by
    ///     <c>[ObservableProperty]</c>) calls
    ///     <see cref="SendCommand.NotifyCanExecuteChanged"/> on every keystroke
    ///     so the button reflects the current InputText immediately — without
    ///     that, the button would stay disabled until focus moved off the box.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = (InputText ?? string.Empty).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _logger.LogInformation("Submitting prompt, length={Length}", text.Length);
        InputText = string.Empty;
        // Force CanExecute re-evaluation now that InputText has been cleared —
        // otherwise the Send button stays enabled until the next keystroke.
        SendCommand.NotifyCanExecuteChanged();

        string trimmed = text.Trim();
        TuiEffect effect;
        if (ChatCommands.ExitWords.Contains(trimmed))
        {
            effect = new TuiEffect.QuitApp();
        }
        else if (trimmed.StartsWith('/'))
        {
            effect = new TuiEffect.RunSlash(trimmed);
        }
        else
        {
            _store.Transition(s => s.AddLine(ChatRole.User, text));
            effect = new TuiEffect.PromptAgent(trimmed);
        }

        _effects.Run(effect);
    }

    /// <summary>
    ///     CanExecute for <see cref="SendCommand"/>. Returns true when there
    ///     is non-whitespace input. Bound to the Send button's IsEnabled.
    /// </summary>
    /// <returns>True if <see cref="InputText"/> has non-whitespace content.</returns>
    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText);

    /// <summary>
    ///     Source-generated partial invoked by <c>[ObservableProperty]</c>
    ///     whenever <see cref="InputText"/> changes. Forwards to
    ///     <see cref="SendCommand.NotifyCanExecuteChanged"/> so the Send
    ///     button's enabled state tracks the input live (every keystroke).
    /// </summary>
    /// <param name="value">The new InputText value.</param>
    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Abort the running agent (Ctrl+C in input or Stop button).</summary>
    [RelayCommand]
    private void Stop()
    {
        _effects.Run(new TuiEffect.AbortAgent());
        _toasts.Show("Abort requested — agent will stop at the next safe boundary.", ToastKind.Warning);
    }
    /// <summary>Clear the chat transcript (Ctrl+L).</summary>
    [RelayCommand]
    private void Clear()
    {
        _store.Reset();
        ResetRendering();
    }

    /// <summary>
    ///     Reset rendering state — called when switching sessions so the
    ///     previous session's chat lines + tool-call cards are evicted and
    ///     the new session's messages render fresh from the replayed
    ///     <see cref="UiStore"/> state.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without this call, <see cref="Lines"/> still holds the previous
    ///         session's chat rows after <see cref="SessionManager.OpenSessionAsync"/>
    ///         resets the UiStore. <see cref="_renderedLineCount"/> would also
    ///         stay at its old value, so <see cref="OnStoreChanged"/> would
    ///         skip the new session's replayed lines (thinking they were
    ///         already rendered).
    ///     </para>
    ///     <para>
    ///         Must run on the UI thread because <see cref="Lines"/> /
    ///         <see cref="ToolCalls"/> are <see cref="ObservableCollection{T}"/>
    ///         bound to the chat view. Callers in <see cref="SessionManager"/>
    ///         wrap the call in <see cref="Dispatcher.UIThread.Post"/>.
    ///     </para>
    /// </remarks>
    public void ResetRendering()
    {
        Lines.Clear();
        ToolCalls.Clear();
        _toolCallById.Clear();
        _renderedLineCount = 0;
        // Clear streaming/transient UI so a stale "Agent is running…" banner
        // from the previous session doesn't linger until the new session's
        // first agent event arrives.
        IsStreaming = false;
        IsThinking = false;
        IsAgentRunning = false;
        StatusMessage = string.Empty;
        StreamingBuffer = string.Empty;
    }
}

/// <summary>One chat line projected for the UI. Role + text + brush key for binding.</summary>
public sealed record ChatLineViewModel(ChatRole Role, string Text)
{
    /// <summary>The brush resource key (resolved by RoleBrushKey).</summary>
    public string BrushKey => ChatViewModel.RoleBrushKey(Role);

    /// <summary>The lowercase role label for the gutter.</summary>
    public string RoleLabel => Role.ToString().ToLowerInvariant();
}
