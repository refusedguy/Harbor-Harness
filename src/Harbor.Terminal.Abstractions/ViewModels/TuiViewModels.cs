using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
namespace Harbor.Terminal.Abstractions.ViewModels;
/// <summary>
///     Status bar view model — shows model, agent, cost, tokens, status.
///     Uses CommunityToolkit.Mvvm source generators for INPC.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, ITuiViewModel
{

    [ObservableProperty]
    private string _agent = "code";

    /// <summary>
    ///     Current context size in tokens (last step's input/prompt token count).
    /// </summary>
    [ObservableProperty]
    private int _contextTokens;

    /// <summary>
    ///     Model context window (max input tokens). 0 when unknown.
    /// </summary>
    [ObservableProperty]
    private int _contextWindow;

    [ObservableProperty]
    private decimal _cost;
    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _status = "idle";

    [ObservableProperty]
    private int _tokensIn;

    [ObservableProperty]
    private int _tokensOut;

    /// <summary>
    ///     Context usage as a percentage of <see cref="_contextWindow" />. 0 when unknown.
    /// </summary>
    public int ContextPct => ContextWindow > 0 ? Math.Min(100, ContextTokens * 100 / ContextWindow) : 0;

    /// <summary>
    ///     Formatted status line for rendering. Cost is formatted with the
    ///     invariant culture: golden-frame tests pin exact strings, and a
    ///     locale decimal separator (ru-RU "$0,0000") must not leak into
    ///     renderer output.
    /// </summary>
    public string Formatted => ContextWindow > 0
        ? $"{Provider}/{Model} | agent: {Agent} | {CostText} | {TokensIn}↑ {TokensOut}↓ | ctx: {ContextTokens / 1000}k/{ContextPct}% | {Status}"
        : $"{Provider}/{Model} | agent: {Agent} | {CostText} | {TokensIn}↑ {TokensOut}↓ | {Status}";

    private string CostText => "$" + Cost.ToString("F4", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string Id => "status-bar";

    /// <inheritdoc />
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        switch (@event)
        {
            case AgentStartEvent ase:
                Status = "running";
                SetModel(ase.Model);
                break;
            case AgentEndEvent:
                Status = "idle";
                break;
            case MessageUpdateEvent mu when mu.LlmEvent is StepFinishEvent sf && sf.Usage is not null:
                TokensIn += sf.Usage.InputTokens;
                TokensOut += sf.Usage.OutputTokens;
                ContextTokens = sf.Usage.InputTokens;
                break;
            case AgentErrorEvent:
                Status = "error";
                break;
            case CompactionStartedEvent:
                Status = "compacting";
                break;
            case CompactionCompletedEvent:
                Status = "running";
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Set the active model so the view model can compute context usage.
    /// </summary>
    /// <param name="model">The resolved model info (may be null).</param>
    public void SetModel(ModelInfo? model) => ContextWindow = model?.ContextWindow ?? 0;

    /// <summary>
    ///     Reset all counters to zero. Bound to the <c>Reset</c> command.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        Cost = 0;
        TokensIn = 0;
        TokensOut = 0;
        ContextTokens = 0;
        Status = "idle";
    }
}

/// <summary>
///     Chat history view model — accumulates messages with streaming support.
/// </summary>
public sealed partial class ChatHistoryViewModel : ObservableObject, ITuiViewModel
{
    private readonly ObservableCollection<ChatEntry> _entries = new();

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    [property: BindsToView("chat-history")]
    private string _streamingText = string.Empty;

    [ObservableProperty]
    private string _thinkingText = string.Empty;

    /// <summary>
    ///     The accumulated chat entries (one per finalized message or tool result).
    /// </summary>
    public IReadOnlyList<ChatEntry> Entries => _entries;

    /// <inheritdoc />
    public string Id => "chat-history";

    /// <inheritdoc />
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        switch (@event)
        {
            case MessageStartEvent:
                IsStreaming = true;
                StreamingText = string.Empty;
                break;

            case MessageUpdateEvent mu:
                switch (mu.LlmEvent)
                {
                    case TextDeltaEvent td:
                        StreamingText += td.Delta;
                        break;
                    case ThinkingDeltaEvent thd:
                        IsThinking = true;
                        ThinkingText += thd.Delta;
                        break;
                    case ToolCallStartEvent tcs:
                        AddEntry(new ChatEntry("tool", $"→ {tcs.ToolName}", DateTimeOffset.UtcNow));
                        break;
                }
                break;

            case MessageEndEvent:
                if (!string.IsNullOrEmpty(StreamingText))
                    AddEntry(new ChatEntry("assistant", StreamingText, DateTimeOffset.UtcNow));
                IsStreaming = false;
                IsThinking = false;
                StreamingText = string.Empty;
                ThinkingText = string.Empty;
                break;

            case ToolExecutionEndEvent tee:
                string label = tee.IsError ? "✗" : "✓";
                string preview = tee.Result.Output.Length > 200 ? tee.Result.Output[..200] + "..." : tee.Result.Output;
                AddEntry(new ChatEntry("tool-result", $"{label} {preview}", DateTimeOffset.UtcNow));
                break;

            case AgentStartEvent ase:
                foreach (var m in ase.Messages)
                {
                    if (m is UserMessage u)
                        AddEntry(new ChatEntry("user", u.Content, u.CreatedAt));
                }
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Append a new chat entry. Called by event handlers when a message or tool result finalizes.
    /// </summary>
    /// <param name="entry">The entry to append.</param>
    public void AddEntry(ChatEntry entry) => _entries.Add(entry);

    /// <summary>
    ///     Clear all entries and reset streaming state.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        StreamingText = string.Empty;
        IsStreaming = false;
        ThinkingText = string.Empty;
        IsThinking = false;
    }

    /// <summary>
    ///     Clear all history. Bound to the <c>ClearHistory</c> command.
    /// </summary>
    [RelayCommand]
    private void ClearHistory() => Clear();
}

/// <summary>
///     Input editor view model — tracks user input.
/// </summary>
public sealed partial class InputViewModel : ObservableObject, ITuiViewModel
{

    [ObservableProperty]
    private int _cursorPosition;

    [ObservableProperty]
    private bool _isMultiline;

    [ObservableProperty]
    private string _placeholder = "Type your message...";
    [ObservableProperty]
    private string _text = string.Empty;

    /// <inheritdoc />
    public string Id => "input";

    /// <inheritdoc />
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     Submit the current text and reset the editor. Bound to the <c>Submit</c> command.
    /// </summary>
    [RelayCommand]
    private void Submit()
    {
        Text = string.Empty;
        CursorPosition = 0;
    }

    /// <summary>
    ///     Cancel the current edit (keeps the text for reference). Bound to the <c>Cancel</c> command.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        // Keep text for reference, just reset cursor
        CursorPosition = 0;
    }
}

/// <summary>
///     Diff preview view model — shows file diffs from edit/write tools.
/// </summary>
public sealed partial class DiffPreviewViewModel : ObservableObject, ITuiViewModel
{
    private readonly ObservableCollection<DiffEntry> _diffs = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextDiffCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousDiffCommand))]
    private int _currentIndex = -1;

    /// <summary>
    ///     All recorded diffs.
    /// </summary>
    public IReadOnlyList<DiffEntry> Diffs => _diffs;

    /// <summary>
    ///     The currently selected diff, or <see langword="null" /> if none.
    /// </summary>
    public DiffEntry? Current => CurrentIndex >= 0 && CurrentIndex < _diffs.Count ? _diffs[CurrentIndex] : null;

    /// <inheritdoc />
    public string Id => "diff-preview";

    /// <inheritdoc />
    public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default)
    {
        if (@event is ToolExecutionEndEvent tee && !tee.IsError)
        {
            // Heuristic: detect file paths from output
            if (tee.Result.Output.Contains("Wrote ") || tee.Result.Output.Contains("Edited "))
            {
                AddDiff(new DiffEntry("file-change", tee.Result.Output, DateTimeOffset.UtcNow));
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Append a new diff. Auto-selects it if nothing is selected.
    /// </summary>
    /// <param name="entry">The diff entry to append.</param>
    public void AddDiff(DiffEntry entry)
    {
        _diffs.Add(entry);
        if (CurrentIndex < 0) CurrentIndex = 0;
    }

    /// <summary>
    ///     Move to the next diff. Bound to the <c>NextDiff</c> command.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextDiff()
    {
        if (CurrentIndex < _diffs.Count - 1) CurrentIndex++;
    }

    /// <summary>
    ///     Move to the previous diff. Bound to the <c>PreviousDiff</c> command.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousDiff()
    {
        if (CurrentIndex > 0) CurrentIndex--;
    }

    private bool CanNext() => CurrentIndex >= 0 && CurrentIndex < _diffs.Count - 1;
    private bool CanPrevious() => CurrentIndex > 0;
}

/// <summary>
///     A single chat history entry.
/// </summary>
/// <param name="Role">The role string (<c>user</c>, <c>assistant</c>, <c>tool</c>, <c>tool-result</c>).</param>
/// <param name="Content">The entry's text content.</param>
/// <param name="Timestamp">When the entry was created.</param>
public sealed record ChatEntry(string Role, string Content, DateTimeOffset Timestamp);

/// <summary>
///     A single diff entry.
/// </summary>
/// <param name="ToolName">The tool that produced the change (e.g. <c>write</c>, <c>edit</c>).</param>
/// <param name="Output">The tool's output (typically includes the file path and a diff summary).</param>
/// <param name="Timestamp">When the change was recorded.</param>
public sealed record DiffEntry(string ToolName, string Output, DateTimeOffset Timestamp);
