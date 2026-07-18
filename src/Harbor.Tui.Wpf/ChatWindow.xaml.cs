using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Wpf;

/// <summary>
///     Code-behind for <see cref="ChatWindow" />. Wires the shared
///     <see cref="UiStore" /> + <see cref="TuiEffectHost" /> to the WPF controls
///     and forwards Enter-key submits to the effect runner.
/// </summary>
public partial class ChatWindow : Window
{
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly ObservableCollection<ChatLineViewModel> _lines;
    private readonly ILogger _logger;

    /// <summary>Construct the chat window.</summary>
    public ChatWindow(
        UiStore store,
        TuiEffectHost effects,
        ObservableCollection<ChatLineViewModel> lines,
        ILogger logger)
    {
        _store = store;
        _effects = effects;
        _lines = lines;
        _logger = logger;
        InitializeComponent();
        HistoryList.ItemsSource = _lines;
        SyncHeader(_store.State);
        InputBox.Focus();
    }

    /// <summary>Called by the renderer's <c>Dispatcher.BeginInvoke</c> on every store change.</summary>
    public void UpdateStreaming(UiState state)
    {
        SyncHeader(state);
        StatusLabel.Text = state.Status;
        CostLabel.Text = $"{state.Cost.TokensIn:N0} in / {state.Cost.TokensOut:N0} out / ${state.Cost.CostUsd:F4}";

        if (state.IsStreaming && !string.IsNullOrEmpty(state.Active.TextBuffer))
        {
            StreamingBar.Visibility = Visibility.Visible;
            StreamingLabel.Text = state.IsAgentRunning ? "● streaming" : "● thinking";
            StreamingBuffer.Text = state.Active.TextBuffer.Length > 200
                ? state.Active.TextBuffer[^200..]
                : state.Active.TextBuffer;
        }
        else
        {
            StreamingBar.Visibility = Visibility.Collapsed;
        }

        // Auto-scroll to bottom on new content.
        if (HistoryList.Items.Count > 0)
        {
            HistoryList.ScrollIntoView(HistoryList.Items[^1]);
        }
    }

    private void SyncHeader(UiState s)
    {
        AgentLabel.Text = string.IsNullOrEmpty(s.AgentName) ? "agent" : s.AgentName;
        ModelLabel.Text = string.IsNullOrEmpty(s.Model) ? "model" : s.Model;
        ProviderLabel.Text = string.IsNullOrEmpty(s.Provider) ? "provider" : s.Provider;
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Shift+Enter inserts a newline (TextBox default behavior).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        // Plain Enter or Ctrl+Enter submits.
        e.Handled = true;
        SubmitInput();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SubmitInput();

    private void SubmitInput()
    {
        var raw = InputBox.Text;
        var text = raw.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _logger.LogInformation("WPF submitting prompt, length={Length}", text.Length);
        InputBox.Clear();

        // Mirror the reducer's ClassifySubmit logic so exit words, slash
        // commands and regular prompts all behave like the terminal UI.
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
}
