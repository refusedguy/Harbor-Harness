using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;

namespace Harbor.Tui.Avalonia;

/// <summary>
///     Code-behind for <see cref="MainWindow" />. Mirrors the WPF
///     <c>ChatWindow.xaml.cs</c> with Avalonia's input/event APIs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly ObservableCollection<ChatLineViewModel> _lines;
    private readonly ILogger _logger;

    /// <summary>Construct the main window.</summary>
    public MainWindow(
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
        InputBox.AttachedToVisualTree += (_, _) => InputBox.Focus();
    }

    /// <summary>Called by the renderer via <c>Dispatcher.UIThread.Post</c> on store changes.</summary>
    public void UpdateStreaming(UiState state)
    {
        SyncHeader(state);
        StatusLabel.Text = state.Status;
        CostLabel.Text = $"{state.Cost.TokensIn:N0} in / {state.Cost.TokensOut:N0} out / ${state.Cost.CostUsd:F4}";

        if (state.IsStreaming && !string.IsNullOrEmpty(state.Active.TextBuffer))
        {
            StreamingBar.IsVisible = true;
            StreamingLabel.Text = state.IsAgentRunning ? "● streaming" : "● thinking";
            var buf = state.Active.TextBuffer;
            StreamingBuffer.Text = buf.Length > 200 ? buf[^200..] : buf;
        }
        else
        {
            StreamingBar.IsVisible = false;
        }

        if (_lines.Count > 0)
        {
            HistoryList.ScrollIntoView(_lines[^1]);
        }
    }

    private void SyncHeader(UiState s)
    {
        AgentLabel.Text = string.IsNullOrEmpty(s.AgentName) ? "agent" : s.AgentName;
        ModelLabel.Text = string.IsNullOrEmpty(s.Model) ? "model" : s.Model;
        ProviderLabel.Text = string.IsNullOrEmpty(s.Provider) ? "provider" : s.Provider;
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Shift+Enter inserts a newline (TextBox default behavior).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        // Plain Enter or Ctrl+Enter submits.
        e.Handled = true;
        SubmitInput();
    }

    private void SendButton_Click(object? sender, RoutedEventArgs e) => SubmitInput();

    private void SubmitInput()
    {
        var text = InputBox.Text?.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _logger.LogInformation("Avalonia submitting prompt, length={Length}", text.Length);
        InputBox.Text = string.Empty;

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
