using System.Collections.ObjectModel;
using ChatLineViewModel = Harbor.Ui.Framework.ViewModels.ChatLineViewModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Sessions;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.Views.Components;
using Harbor.Desktop.Abstractions.ViewModels;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

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
public sealed partial class ChatViewModel : ChatViewModelBase
{
    private readonly TuiEffectHost _effects;
    private readonly UiRenderEngine _renderEngine;
    private readonly ISessionManager? _sessionManager;
    private readonly ISessionStore _sessionStore;
    private readonly IToastService _toasts;
    private UiStore _store;

    public ObservableCollection<Suggestion> ContextualSuggestions { get; } = new();

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

    /// <summary>
    ///     Called after all selectors have been applied and INPC notifications raised.
    ///     Performs Avalonia-specific UI updates (rendering, session status) on the
    ///     UI thread after the state projection is complete.
    /// </summary>
    /// <remarks>
    ///     Ф-A1 (sprint 4.5): renders are FRAME-COALESCED. During streaming the
    ///     store emits one transition per TextDelta — dozens per second — and
    ///     each full reconcile + INPC storm forces a layout pass, which made
    ///     the whole window jitter. Instead of rendering every transition, we
    ///     remember only the LATEST snapshot and flush at most once per
    ///     16 ms frame via a DispatcherTimer; intermediate snapshots collapse.
    /// </remarks>
    /// <param name="state">The new <see cref="UiState" /> snapshot.</param>
    protected override void OnAfterSelectorsApplied(UiState state)
    {
        Dispatcher.Post(() =>
        {
            Logger.LogDebug("OnAfterSelectorsApplied: lines={Lines}, streaming={Streaming}, agentRunning={Running}, textBufLen={TextBufLen}",
                state.Lines.Length, state.IsStreaming, state.IsAgentRunning, state.Active.TextBuffer?.Length ?? 0);

            _pendingRenderState = state;
            StartRenderTimer();
        });
    }

    /// <summary>Latest snapshot awaiting its frame slot (UI thread only).</summary>
    private UiState? _pendingRenderState;

    private DispatcherTimer? _renderTimer;

    /// <summary>
    ///     Arm the 16 ms frame timer if it is not running yet. Created lazily
    ///     on the UI thread — the ViewModel itself is constructed by DI off-thread.
    /// </summary>
    private void StartRenderTimer()
    {
        if (_renderTimer is null)
        {
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _renderTimer.Tick += RenderFrameTick;
        }

        if (!_renderTimer.IsEnabled)
        {
            _renderTimer.Start();
        }
    }

    private void RenderFrameTick(object? sender, EventArgs e)
    {
        // One tick = one frame = one render with the freshest snapshot.
        _renderTimer!.Stop();
        var state = _pendingRenderState;
        _pendingRenderState = null;
        if (state is null)
        {
            return;
        }

        // A frame-coalesced render must not clobber NEWER local state: while
        // this snapshot sat queued, the user may have typed into the input
        // (local VM state, not part of UiState). Capture it and restore.
        string inputBefore = InputText;
        _renderEngine.Render(state, this);
        if (inputBefore.Length > 0 && !string.Equals(InputText, inputBefore, StringComparison.Ordinal))
        {
            InputText = inputBefore;
        }

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
    ///     <see cref="OnPropertyChanged" /> override calls
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

    protected override void OnPropertyChanged(PropertyChangedEventArgs? e)
    {
        base.OnPropertyChanged(e!);
        if (e?.PropertyName == nameof(InputText))
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
