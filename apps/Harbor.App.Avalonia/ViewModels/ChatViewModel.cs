using System.Collections.ObjectModel;
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
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly AvaloniaDispatcherAdapter _dispatcher;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly ToastService _toasts;
    private int _renderedLineCount;

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
                Lines.Add(new ChatLineViewModel(line.Role, line.Text));
                _renderedLineCount++;
            }

            IsStreaming = state.IsStreaming;
            IsThinking = state.IsAgentRunning && !state.IsStreaming;
            StreamingBuffer = state.Active.TextBuffer;
        });
    }

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
