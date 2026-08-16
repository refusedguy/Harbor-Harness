using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Streaming chat view model. Maintains the message list and the active
///     streaming buffer; user prompts are dispatched to the agent loop.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{

    /// <summary>Whether the user is dragging the splitter (suppresses auto-scroll).</summary>
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>User input text.</summary>
    [ObservableProperty] private string _inputText = string.Empty;

    /// <summary>Whether the agent is currently streaming.</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>Active streaming buffer — visible while the assistant is generating.</summary>
    [ObservableProperty] private string _streamingBuffer = string.Empty;
    /// <summary>Construct a <see cref="ChatViewModel" />.</summary>
    public ChatViewModel()
    {
        Messages = new ObservableCollection<ChatMessageViewModel>();
        StreamingBuffer = string.Empty;
        InputText = string.Empty;

        // Seed a welcome message so the panel is not visually empty on first launch.
        Messages.Add(new ChatMessageViewModel(
            "assistant",
            "Welcome to Harbor. Type a prompt and hit **Send** (or Ctrl+Enter).",
            DateTimeOffset.UtcNow));
    }

    /// <summary>Chat transcript, oldest first.</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; }

    /// <summary>Visibility for the streaming buffer border.</summary>
    public Visibility StreamingVisibility => IsStreaming ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    ///     Submit the current input as a user prompt. Clears the input box and
    ///     dispatches to the agent. Implementation hooks are intentionally
    ///     minimal here — the desktop shell can be wired to a real agent loop
    ///     via <c>IAgent.PromptAsync</c> when one is available.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string text = (InputText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return;

        Messages.Add(new ChatMessageViewModel("user", text, DateTimeOffset.UtcNow));
        InputText = string.Empty;
        IsStreaming = true;
        StreamingBuffer = string.Empty;

        try
        {
            // Simulated streaming — replace with IAgent.PromptAsync in a real wiring.
            await SimulateStreamAsync(text).ConfigureAwait(false);
        }
        finally
        {
            IsStreaming = false;
        }
    }

    /// <summary>Cancel the in-flight stream.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsStreaming = false;
        StreamingBuffer = string.Empty;
    }

    /// <summary>Clear the transcript.</summary>
    [RelayCommand]
    private void Clear() => Messages.Clear();

    private bool CanSend() => !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        this.OnPropertyChanged(nameof(StreamingVisibility));
    }

    private async Task SimulateStreamAsync(string prompt)
    {
        // In a real wiring, this method is replaced by an event-bus subscription
        // that feeds MessageUpdateEvent.StreamingDelta into StreamingBuffer.
        string[] words = ("Echo: " + prompt).Split(' ');
        foreach (string w in words)
        {
            StreamingBuffer += w + " ";
            await Task.Delay(60).ConfigureAwait(false);
        }
        Messages.Add(new ChatMessageViewModel("assistant", StreamingBuffer, DateTimeOffset.UtcNow));
        StreamingBuffer = string.Empty;
    }
}

/// <summary>
///     View model for a single chat message (user / assistant / tool). Platform
///     extension of the shared <see cref="Harbor.Desktop.Abstractions.ViewModels.ChatMessageViewModel" />
///     record — adds the WPF <see cref="Brush" /> for the role label (the
///     canonical color key lives on the shared record as
///     <c>RoleBrushKey</c>).
/// </summary>
public sealed partial class ChatMessageViewModel : Harbor.Desktop.Abstractions.ViewModels.ChatMessageViewModel
{
    /// <summary>Construct a <see cref="ChatMessageViewModel" />.</summary>
    public ChatMessageViewModel(string role, string content, DateTimeOffset timestamp)
        : base(role, content, timestamp) { }

    /// <summary>Foreground brush for the role label (Catppuccin Mocha).</summary>
    public Brush RoleForeground => Role switch
    {
        "user" => Brushes.SkyBlue,
        "assistant" => Brushes.Cornsilk,
        "tool" => Brushes.CornflowerBlue,
        "tool_result" => Brushes.MediumSeaGreen,
        "error" => Brushes.OrangeRed,
        _ => Brushes.LightGray
    };
}
