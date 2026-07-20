using Harbor.Abstractions.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToolCallViewModel = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Streaming chat view-model. Subscribes to <see cref="UiStore"/> transitions on the
///     UI thread and projects them into an observable chat-line collection. User input is
///     forwarded through <see cref="TuiEffectHost"/> so the agent loop runs out-of-band.
/// </summary>
/// <remarks>
///     <para>
///         <b>Rendering pipeline:</b> the heavy lifting (append-only projection +
///         tool-call coalescing) lives in <see cref="ChatMessageRenderer"/>, while
///         the streaming-buffer state derivation lives in
///         <see cref="ChatStreamingPresenter"/>. This view-model just wires the
///         UiStore subscription to those services and exposes observable properties
///         + commands for the view to bind against.
///     </para>
/// </remarks>
public sealed partial class ChatViewModel : ObservableObject
{
    private UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly SessionManager? _sessionManager;
    private readonly AvaloniaDispatcherAdapter _dispatcher;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly ToastService _toasts;
    private readonly ChatMessageRenderer _renderer;
    private readonly ChatStreamingPresenter _presenter;
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
        ToastService toasts,
        ChatMessageRenderer renderer,
        ChatStreamingPresenter presenter)
    {
        _store = store;
        _effects = effects;
        _sessionManager = sessionManager;
        _dispatcher = dispatcher;
        _logger = logger;
        _toasts = toasts;
        _renderer = renderer;
        _presenter = presenter;

        _onStoreChanged = (_, state) => OnStoreChanged(state);
        // NOTE: _dispatcher.Bind(_store) is intentionally NOT called here — the
        // composition root (AppHost.BuildAsync) binds the dispatcher to the UiStore
        // exactly once, idempotently. Subscribing here would duplicate the binding.
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
        _dispatcher.Post(() =>
        {
            _logger.LogDebug("OnStoreChanged: lines={Lines}, rendered={Rendered}, streaming={Streaming}, agentRunning={Running}, textBufLen={TextBufLen}",
                state.Lines.Length, _renderedLineCount, state.IsStreaming, state.IsAgentRunning, state.Active.TextBuffer?.Length ?? 0);

            // Delegate append-only line + tool-call card projection to ChatMessageRenderer.
            _renderedLineCount = _renderer.Render(
                state, Lines, ToolCalls, _toolCallById, _toolCallStopwatch, _renderedLineCount);

            // Delegate streaming-buffer state derivation to ChatStreamingPresenter.
            _presenter.Apply(
                state,
                setStreaming: v => IsStreaming = v,
                setThinking: v => IsThinking = v,
                setAgentRunning: v => IsAgentRunning = v,
                setStatusMessage: v => StatusMessage = v,
                setStreamingBuffer: v => StreamingBuffer = v);

            // Push session status + message count to the sidebar via SessionManager.
            if (_sessionManager?.Active is { } activeSession)
            {
                _sessionManager.SetStatus(activeSession.Id, _presenter.DeriveStatus(state));
                _sessionManager.NotifyMessageCount(activeSession.Id, state.Lines.Length);
            }
        });
    }

    /// <summary>Submit the current input text. Plain Enter (handled in view) triggers this.</summary>
    /// <remarks>
    ///     Task U4 — CanExecute wiring. <see cref="CanSend"/> gates the Send
    ///     button so it greys out when the input is empty. The
    ///     <see cref="OnInputTextChanged"/> partial (generated by
    ///     <c>[ObservableProperty]</c>) calls
    ///     <see cref="SendCommand.NotifyCanExecuteChanged"/> on every keystroke
    ///     so the button reflects the current InputText immediately.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = (InputText ?? string.Empty).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _logger.LogInformation("Submitting prompt, length={Length}", text.Length);
        InputText = string.Empty;
        // Force CanExecute re-evaluation now that InputText has been cleared.
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
    ///     Must run on the UI thread because <see cref="Lines"/> /
    ///     <see cref="ToolCalls"/> are <see cref="ObservableCollection{T}"/>
    ///     bound to the chat view. Callers in <see cref="SessionManager"/>
    ///     wrap the call in <see cref="IDispatcherAdapter.Post"/>.
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

    /// <summary>
    ///     Current <c>_renderedLineCount</c> snapshot. Exposed so
    ///     <see cref="SessionManager"/> can persist it into the
    ///     <see cref="SessionContext.RenderedLineCount"/> field when
    ///     switching AWAY from a session, so that switching back resumes
    ///     rendering at the right offset (no duplicate lines appended).
    /// </summary>
    public int RenderedLineCount => _renderedLineCount;

    /// <summary>
    ///     Rebind this view-model to a different session's
    ///     <see cref="UiStore"/>. Used when the user switches sessions:
    ///     the old store keeps accumulating events for the (still-running)
    ///     background agent, but the UI now renders the new session's store.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Sequence:</b>
    ///         <list type="number">
    ///             <item>Unbind the dispatcher from the old store.</item>
    ///             <item>Clear local rendering state (Lines, ToolCalls,
    ///             _renderedLineCount, streaming flags).</item>
    ///             <item>Swap <c>_store</c> to <paramref name="newStore"/>.</item>
    ///             <item>Bind the dispatcher to <paramref name="newStore"/>.</item>
    ///             <item>Replay the new store's current state through
    ///             <see cref="OnStoreChanged"/> so the chat rows render
    ///             immediately (rather than waiting for the next event).</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Must be called on the UI thread — it mutates
    ///         <see cref="Lines"/> / <see cref="ToolCalls"/> (ObservableCollection
    ///         bound to the view) and the <paramref name="newStore"/> can be
    ///         touched concurrently by the background agent's reducer.
    ///     </para>
    /// </remarks>
    /// <param name="newStore">The UiStore of the session being switched to.</param>
    /// <param name="savedRenderedLineCount">
    ///     The <see cref="RenderedLineCount"/> snapshot saved when the
    ///     session was previously switched away from (0 if first visit).
    /// </param>
    public void RebindToStore(UiStore newStore, int savedRenderedLineCount)
    {
        ArgumentNullException.ThrowIfNull(newStore);
        if (ReferenceEquals(_store, newStore))
        {
            // Already bound to this store — just replay.
            OnStoreChanged(newStore.State);
            return;
        }

        // 1. Unsubscribe the dispatcher from the OLD store. Without this,
        //    events from the old session's still-running agent would still
        //    flow into OnStoreChanged and clobber the new session's chat
        //    transcript — the exact user-visible bug this rebind fixes.
        _dispatcher.Unbind(_store);

        // 2. Clear local rendering state so old chat rows + tool-call cards
        //    don't leak into the new session's view.
        Lines.Clear();
        ToolCalls.Clear();
        _toolCallById.Clear();
        _renderedLineCount = savedRenderedLineCount;
        IsStreaming = false;
        IsThinking = false;
        IsAgentRunning = false;
        StatusMessage = string.Empty;
        StreamingBuffer = string.Empty;

        // 3. Swap to the new store + bind the dispatcher.
        _store = newStore;
        _dispatcher.Bind(newStore);

        // 4. Replay the new store's current state so the chat transcript
        //    renders immediately rather than waiting for the next agent event.
        OnStoreChanged(newStore.State);
    }
}

