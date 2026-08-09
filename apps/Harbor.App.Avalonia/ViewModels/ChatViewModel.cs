using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Sessions;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.Views.Components;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Streaming chat view-model. Subscribes to <see cref="UiStore" /> transitions on the
///     UI thread and projects them into an observable chat-line collection. User input is
///     forwarded through <see cref="TuiEffectHost" /> so the agent loop runs out-of-band.
/// </summary>
/// <remarks>
///     <para>
///         <b>Rendering pipeline:</b> the heavy lifting (append-only projection +
///         tool-call coalescing) lives in <see cref="DefaultUiProjector" />, while
///         the streaming-buffer state derivation lives in
///         <see cref="ChatStreamingPresenter" />. This view-model just wires the
///         UiStore subscription to those services and exposes observable properties
///         + commands for the view to bind against.
///     </para>
/// </remarks>
internal sealed partial class ChatViewModel : StoreSubscriberViewModel
{
    private readonly TuiEffectHost _effects;
    private readonly UiRenderEngine _renderEngine;
    private readonly ISessionManager? _sessionManager;
    private readonly ISessionStore _sessionStore;
    private readonly IToastService _toasts;
    private UiStore _store;

    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>
    ///     True while the agent loop is running (waiting for first token OR
    ///     streaming). Mirrors <see cref="UiState.IsAgentRunning" />. Bound to
    ///     the chat area's "Agent is running…" indicator + the Stop button's
    ///     IsVisible. Distinct from <see cref="IsStreaming" /> (only true while
    ///     tokens are actively arriving) and <see cref="IsThinking" /> (true
    ///     while running but not yet streaming).
    /// </summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isThinking;

    /// <summary>
    ///     Human-readable status message shown in the chat area while the
    ///     agent is running (e.g. <c>"Agent is running…"</c>). Cleared on
    ///     completion. Bound to the streaming-indicator border's TextBlock.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoadingHistory;

    [ObservableProperty]
    private double _pullProgress;

    [ObservableProperty]
    private double _pullOffset;

    [ObservableProperty]
    private double _contentScale = 1.0;

    [ObservableProperty]
    private bool _canLoadOlder = true;

    [ObservableProperty]
    private bool _showPullIndicator;

    public ObservableCollection<Suggestion> ContextualSuggestions { get; } = new();

    partial void OnIsThinkingChanged(bool value) => OnPropertyChanged(nameof(InputPlaceholder));
    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(InputPlaceholder));
    partial void OnIsAgentRunningChanged(bool value) => OnPropertyChanged(nameof(InputPlaceholder));

    partial void OnIsLoadingHistoryChanged(bool value) => OnPropertyChanged(nameof(PullRefreshStatusText));
    partial void OnPullProgressChanged(double value) => OnPropertyChanged(nameof(PullRefreshStatusText));

    public string PullRefreshStatusText => IsLoadingHistory ? "Loading older messages..."
        : PullProgress >= 0.5 ? "Release to refresh" : "Pull to load older messages";

    /// <summary>
    ///     Context-aware placeholder for the composer input. Changes based on
    ///     the current agent state so the user always knows what's happening.
    /// </summary>
    public string InputPlaceholder => GetPlaceholder();

    private string GetPlaceholder()
    {
        if (IsThinking)
            return "Agent is thinking…";
        if (IsStreaming)
            return "Receiving response…";
        if (IsAgentRunning)
            return "Agent is running…";
        return "Ask Harbor anything…";
    }

    [ObservableProperty]
    private string _streamingBuffer = string.Empty;

    /// <summary>Construct the chat view-model.</summary>
    public ChatViewModel(
        UiStore store,
        TuiEffectHost effects,
        ISessionManager? sessionManager,
        ISessionStore sessionStore,
        IDispatcherAdapter dispatcher,
        ILogger<ChatViewModel> logger,
        IToastService toasts,
        UiRenderEngine renderEngine)
        : base(dispatcher, logger)
    {
        _store = store;
        _effects = effects;
        _sessionManager = sessionManager;
        _sessionStore = sessionStore;
        _toasts = toasts;
        _renderEngine = renderEngine;
    }

    /// <summary>Visible chat lines.</summary>
    public ObservableCollection<ChatLineViewModel> Lines { get; } = new();

    /// <summary>
    ///     Visible tool-call cards (one per tool invocation). Updated
    ///     incrementally as <see cref="ChatRole.Tool" /> /
    ///     <see cref="ChatRole.ToolResult" /> lines arrive.
    /// </summary>
    public ObservableCollection<ToolCallViewModel> ToolCalls { get; } = new();

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

    protected override void OnStoreChanged(UiState state)
    {
        Logger.LogDebug("OnStoreChanged: lines={Lines}, streaming={Streaming}, agentRunning={Running}, textBufLen={TextBufLen}",
            state.Lines.Length, state.IsStreaming, state.IsAgentRunning, state.Active.TextBuffer?.Length ?? 0);

        _renderEngine.Render(state, this);

        if (_sessionManager?.Active is { } activeSession)
        {
            _sessionManager.SetStatus(activeSession.Id, _renderEngine.DeriveStatus(state));
            _sessionManager.NotifyMessageCount(activeSession.Id, state.Lines.Length);
        }
    }

    /// <summary>Submit the current input text. Plain Enter (handled in view) triggers this.</summary>
    /// <remarks>
    ///     Task U4 — CanExecute wiring. <see cref="CanSend" /> gates the Send
    ///     button so it greys out when the input is empty. The
    ///     <see cref="OnInputTextChanged" /> partial (generated by
    ///     <c>[ObservableProperty]</c>) calls
    ///     <see cref="SendCommand.NotifyCanExecuteChanged" /> on every keystroke
    ///     so the button reflects the current InputText immediately.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        string text = (InputText ?? string.Empty).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        Logger.LogInformation("Submitting prompt, length={Length}", text.Length);
        InputText = string.Empty;
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
    ///     CanExecute for <see cref="SendCommand" />. Returns true when there
    ///     is non-whitespace input. Bound to the Send button's IsEnabled.
    /// </summary>
    /// <returns>True if <see cref="InputText" /> has non-whitespace content.</returns>
    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText);

    /// <summary>
    ///     Source-generated partial invoked by <c>[ObservableProperty]</c>
    ///     whenever <see cref="InputText" /> changes. Forwards to
    ///     <see cref="SendCommand.NotifyCanExecuteChanged" /> so the Send
    ///     button's enabled state tracks the input live (every keystroke).
    /// </summary>
    /// <param name="value">The new InputText value.</param>
    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

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

    [RelayCommand]
    private void SendSuggestion(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _store.Transition(s => s.AddLine(ChatRole.User, prompt));
        _effects.Run(new TuiEffect.PromptAgent(prompt));
    }

    /// <summary>
    ///     Simulate loading older messages from the session store. Runs on
    ///     release after the user pulls past the threshold at the top of the
    ///     chat scroll viewer.
    /// </summary>
    [RelayCommand]
    private async Task LoadOlderMessagesAsync()
    {
        if (IsLoadingHistory || !CanLoadOlder) return;

        IsLoadingHistory = true;
        ShowPullIndicator = true;
        PullProgress = 1.0;

        try
        {
            await Task.Delay(1500).ConfigureAwait(false);

            var session = _sessionManager?.Active;
            if (session is null) return;

            var messagesResult = await _sessionStore.GetMessagesAsync(session.Id).ConfigureAwait(false);
            if (messagesResult.IsFailure) return;

            var messages = messagesResult.Value;
            var olderMessages = new List<ChatLineViewModel>();

            for (int i = 0; i < Math.Min(5, messages.Count); i++)
            {
                var msg = messages[i];
                var role = msg.Role switch
                {
                    "user" => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    "tool_result" => ChatRole.ToolResult,
                    _ => ChatRole.System
                };
                var prefix = msg.Role switch
                {
                    "user" => "[User]",
                    "assistant" => "[Assistant]",
                    "tool_result" => "[Tool]",
                    _ => "[System]"
                };
                olderMessages.Add(new ChatLineViewModel(role, $"{prefix} Older message #{i + 1} from history"));
            }

            for (int i = olderMessages.Count - 1; i >= 0; i--)
            {
                Lines.Insert(0, olderMessages[i]);
            }

            _toasts.Show($"Loaded {olderMessages.Count} older messages", ToastKind.Info);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load older messages");
            _toasts.Show("Failed to load older messages", ToastKind.Error);
        }
        finally
        {
            IsLoadingHistory = false;
            ShowPullIndicator = false;
            CanLoadOlder = false;
        }
    }

    public void UpdateContextualSuggestions()
    {
        ContextualSuggestions.Clear();
        ContextualSuggestions.Add(Suggestion.Create("⚡ Explain this codebase", "Explain this codebase structure and purpose", new RelayCommand(() => ExecuteSuggestion("Explain this codebase structure and purpose"))));
        ContextualSuggestions.Add(Suggestion.Create("🔍 Review recent changes", "Review recent changes and suggest improvements", new RelayCommand(() => ExecuteSuggestion("Review recent changes and suggest improvements"))));
        ContextualSuggestions.Add(Suggestion.Create("📋 Find TODOs", "Find all TODO comments in the codebase", new RelayCommand(() => ExecuteSuggestion("Find all TODO comments in the codebase"))));
    }

    private void ExecuteSuggestion(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _store.Transition(s => s.AddLine(ChatRole.User, prompt));
        _effects.Run(new TuiEffect.PromptAgent(prompt));
    }

    /// <summary>
    ///     Reset rendering state — called when switching sessions so the
    ///     previous session's chat lines + tool-call cards are evicted and
    ///     the new session's messages render fresh from the replayed
    ///     <see cref="UiStore" /> state.
    /// </summary>
    /// <remarks>
    ///     Must run on the UI thread because <see cref="Lines" /> /
    ///     <see cref="ToolCalls" /> are <see cref="ObservableCollection{T}" />
    ///     bound to the chat view. Callers in <see cref="SessionManager" />
    ///     wrap the call in <see cref="IDispatcherAdapter.Post" />.
    /// </remarks>
    public void ResetRendering()
    {
        Lines.Clear();
        ToolCalls.Clear();
        IsStreaming = false;
        IsThinking = false;
        IsAgentRunning = false;
        StatusMessage = string.Empty;
        StreamingBuffer = string.Empty;
        UpdateContextualSuggestions();
    }

    /// <summary>
    ///     Rebind this view-model to a different session's
    ///     <see cref="UiStore" />. Used when the user switches sessions:
    ///     the old store keeps accumulating events for the (still-running)
    ///     background agent, but the UI now renders the new session's store.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Sequence:</b>
    ///         <list type="number">
    ///             <item>Unbind the dispatcher from the old store.</item>
    ///             <item>
    ///                 Clear local rendering state (Lines, ToolCalls,
    ///                 streaming flags).
    ///             </item>
    ///             <item>Swap <c>_store</c> to <paramref name="newStore" />.</item>
    ///             <item>Bind the dispatcher to <paramref name="newStore" />.</item>
    ///             <item>
    ///                 Replay the new store's current state through
    ///                 <see cref="OnStoreChanged" /> so the chat rows render
    ///                 immediately (rather than waiting for the next agent event).
    ///             </item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Must be called on the UI thread — it mutates
    ///         <see cref="Lines" /> / <see cref="ToolCalls" /> (ObservableCollection
    ///         bound to the view) and the <paramref name="newStore" /> can be
    ///         touched concurrently by the background agent's reducer.
    ///     </para>
    /// </remarks>
    /// <param name="newStore">The UiStore of the session being switched to.</param>
    public void RebindToStore(UiStore newStore)
    {
        ArgumentNullException.ThrowIfNull(newStore);
        if (ReferenceEquals(_store, newStore))
        {
            OnStoreChanged(newStore.State);
            return;
        }

        void RebindCore()
        {
            Dispatcher.Unbind(_store);

            Lines.Clear();
            ToolCalls.Clear();
            IsStreaming = false;
            IsThinking = false;
            IsAgentRunning = false;
            StatusMessage = string.Empty;
            StreamingBuffer = string.Empty;

            _store = newStore;
            _effects.RebindStore(newStore);
            Dispatcher.Bind(newStore);

            OnStoreChanged(newStore.State);
        }

        if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RebindCore();
        }
        else
        {
            _ = Dispatcher.Invoke<object>(() =>
            {
                RebindCore();
                return null!;
            });
        }
    }
}
