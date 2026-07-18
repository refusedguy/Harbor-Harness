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
        AvaloniaDispatcherAdapter dispatcher,
        ILogger<ChatViewModel> logger,
        ToastService toasts)
    {
        _store = store;
        _effects = effects;
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
            // Append only new lines (idempotent under repeated notifications).
            while (_renderedLineCount < state.Lines.Length)
            {
                var line = state.Lines[_renderedLineCount];
                // Tool / ToolResult lines are projected into ToolCalls
                // cards instead of (or in addition to) the transcript.
                if (line.Role is ChatRole.Tool or ChatRole.ToolResult)
                {
                    ProjectToolLine(line, _renderedLineCount);
                }
                else
                {
                    Lines.Add(new ChatLineViewModel(line.Role, line.Text));
                }
                _renderedLineCount++;
            }

            IsStreaming = state.IsStreaming;
            IsThinking = state.IsAgentRunning && !state.IsStreaming;
            StreamingBuffer = state.Active.TextBuffer;
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
    [RelayCommand]
    private void Send()
    {
        var text = (InputText ?? string.Empty).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _logger.LogInformation("Submitting prompt, length={Length}", text.Length);
        InputText = string.Empty;

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
        Lines.Clear();
        ToolCalls.Clear();
        _toolCallById.Clear();
        _renderedLineCount = 0;
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
