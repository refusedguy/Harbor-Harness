using System.Collections.ObjectModel;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace Harbor.Tui.Maui;

/// <summary>
///     Code-behind for <see cref="MainPage" />. Mirrors the WPF/Avalonia
///     chat windows with MAUI's input/event APIs. Single-column layout works
///     on phones (no sidebar) and desktop (wide window).
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly UiStore _store;
    private readonly TuiEffectHost _effects;
    private readonly ObservableCollection<ChatLineViewModel> _lines;
    private readonly ILogger _logger;

    /// <summary>Construct the main page.</summary>
    public MainPage(
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
        _store.Changed += OnStoreChanged;
    }

    private void OnStoreChanged(object? sender, UiStateChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var state = e.State;
            while (_lines.Count < state.Lines.Length)
            {
                var line = state.Lines[_lines.Count];
                _lines.Add(new ChatLineViewModel(line.Role, line.Text));
            }
            UpdateStreaming(state);
        });
    }

    /// <summary>Project the latest state into the streaming bar + status bar.</summary>
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
            HistoryList.ScrollTo(_lines[^1], position: ScrollToPosition.End, animate: false);
        }
    }

    private void SyncHeader(UiState s)
    {
        AgentLabel.Text = string.IsNullOrEmpty(s.AgentName) ? "agent" : s.AgentName;
        ModelLabel.Text = string.IsNullOrEmpty(s.Model) ? "model" : s.Model;
        ProviderLabel.Text = string.IsNullOrEmpty(s.Provider) ? "provider" : s.Provider;
    }

    private void InputBox_Completed(object? sender, EventArgs e) => SubmitInput();

    private void SendButton_Click(object? sender, EventArgs e) => SubmitInput();

    private void SubmitInput()
    {
        var text = InputBox.Text?.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text)) return;

        _logger.LogInformation("MAUI submitting prompt, length={Length}", text.Length);
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
