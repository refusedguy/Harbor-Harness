using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
namespace Harbor.Terminal.Abstractions.ViewModels;
/// <summary>
///     Status bar view model — codex <c>/statusline</c> pattern: one collapsed
///     line of <c>model | ctx% | cost | queue</c> with a status tail.
///     Uses CommunityToolkit.Mvvm source generators for INPC.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, ITuiViewModel
{

    [ObservableProperty]
    private string _agent = "code";

    /// <summary>
    ///     Prompt-token count of the request the provider just accepted
    ///     (<c>StepFinishEvent.Usage.InputTokens</c>) — i.e. how much of the
    ///     context window the last request occupied. This is the source of the
    ///     ctx cell; the session's running token totals (<see cref="_tokensIn" />
    ///     / <see cref="_tokensOut" />) are spend, not occupancy, and are never
    ///     a window reading (#623).
    /// </summary>
    [ObservableProperty]
    private int _requestTokens;

    /// <summary>
    ///     Model context window (max input tokens). 0 when unknown.
    /// </summary>
    [ObservableProperty]
    private int _contextWindow;

    [ObservableProperty]
    private decimal _cost;

    /// <summary>
    ///     Whether <see cref="_cost" /> is a priced total (#653). False when the
    ///     core reported that the model publishes no price — then the cell reads
    ///     "—" instead of "$0.0000", which is a claim ("this was free") that is
    ///     true for Ollama and false for a paid provider whose catalogue entry
    ///     carries no rates. Defaults to true, so every path that predates the
    ///     flag renders exactly as it did.
    /// </summary>
    [ObservableProperty]
    private bool _isCostKnown = true;
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
    ///     Queued tool-call depth (codex <c>/statusline</c> queue segment):
    ///     incremented on <see cref="ToolExecutionStartEvent" />, decremented on
    ///     <see cref="ToolExecutionEndEvent" /> (floors at 0), cleared on
    ///     <see cref="AgentEndEvent" /> and <c>Reset</c>. Rendered as
    ///     <c>queue: N</c> only while non-zero so the idle line stays clean.
    /// </summary>
    [ObservableProperty]
    private int _queuedCount;

    private Pricing? _pricing;

    /// <summary>
    ///     Context-window occupancy as a percentage of <see cref="_contextWindow" />.
    ///     0 when the window is unknown or no request has been sent yet.
    ///     The numerator is the last request's prompt tokens
    ///     (<see cref="_requestTokens" />) — NOT the accumulated
    ///     <c>TokensIn + TokensOut</c> totals. Those are session-cumulative spend
    ///     (<see cref="SessionMetadata.AddUsage" /> accumulates across the whole
    ///     session), so reading them as occupancy made the bar grow without
    ///     bound and pin at 100% by turn 4 on a 128k window while the payload
    ///     never grew. #623; the CellForge ctx bar got the same fix in #630.
    /// </summary>
    public int ContextPct => ContextUsage.PercentUsed(RequestTokens, ContextWindow);

    /// <summary>
    ///     Codex-style collapsed status line: model, context-%, cost, queue.
    ///     Segment order is <c>provider/model | agent | ctx% | cost | tokens | queue? | status</c>:
    ///     context-% sits right after the model (codex <c>/statusline</c> order),
    ///     the queue segment appears only while tool calls are in flight.
    ///     Cost is formatted with the invariant culture: golden-frame tests pin
    ///     exact strings, and a locale decimal separator (ru-RU "$0,0000") must
    ///     not leak into renderer output. When the context window is unknown
    ///     and no tools are queued the shape is byte-identical to the legacy
    ///     line, so existing golden frames are unaffected.
    /// </summary>
    public string Formatted
    {
        get
        {
            string head = $"{Provider}/{Model} | agent: {Agent}";
            string ctx = ContextWindow > 0
                ? $" | ctx: {RequestTokens / 1000}k/{ContextPct}%"
                : string.Empty;
            string queue = QueuedCount > 0 ? $" | queue: {QueuedCount}" : string.Empty;
            return $"{head}{ctx} | {CostText} | {TokensIn}↑ {TokensOut}↓{queue} | {Status}";
        }
    }

    /// <summary>
    ///     The cost cell: the priced total, or "—" when the core could not price
    ///     this model (#653). Never a zero standing in for an unknown price.
    ///     <para>
    ///         #682: the shape comes from <c>UsdCell</c> in
    ///         Harbor.Abstractions.Contracts, not from a second <c>"$" + …"F4"</c>
    ///         written here. This assembly may not reference
    ///         Harbor.Ui.Framework.State (<c>TuiAbstractions_ReferencesOnlyAbstractions</c>),
    ///         so the shared shape had to live in the contracts layer — the same
    ///         reason <c>ContextUsage</c> is there.
    ///         <para>
    ///             The difference from <c>StatusBarText.CostCell</c> is
    ///             deliberate and is PLACEMENT, not shape: the projected bar hides
    ///             the money cell at zero (#457), this line cannot drop a slot
    ///             without shifting every segment after it. Same number, two
    ///             surfaces, one writer.
    ///         </para>
    ///     </para>
    /// </summary>
    private string CostText => IsCostKnown
        ? UsdCell.ToUsd(Cost)
        : UsdCell.Unpriced;

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
                QueuedCount = 0;
                break;
            case MessageUpdateEvent mu when mu.LlmEvent is StepFinishEvent sf && sf.Usage is not null:
                TokensIn += sf.Usage.InputTokens;
                TokensOut += sf.Usage.OutputTokens;
                // The one event that carries the size of the request just sent —
                // the ctx cell's occupancy reading (#623). SessionStatsEvent is
                // deliberately not a source: its totals are session-cumulative.
                RequestTokens = sf.Usage.InputTokens;
                if (_pricing is not null)
                    Cost += _pricing.CalculateCost(sf.Usage);
                break;
            case ToolExecutionStartEvent:
                QueuedCount++;
                break;
            case ToolExecutionEndEvent:
                if (QueuedCount > 0)
                    QueuedCount--;
                break;
            case SessionStatsEvent ss:
                // #653: the core owns the number — assign it, never re-derive it.
                // These totals are session-cumulative spend; they feed the
                // cost/token cells, not the ctx cell (#623).
                Cost = ss.Metadata.Cost;
                IsCostKnown = ss.Metadata.IsCostKnown;
                TokensIn = ss.Metadata.TokensInput;
                TokensOut = ss.Metadata.TokensOutput;
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
            // #773: the third arm of the lifecycle. This VM has no
            // MessageStartEvent arm, so unlike the store projection nothing
            // would have cleared a stale "compacting" — the cell would have
            // read "compacting" from the failure until the AgentEndEvent arm
            // above reset it to "idle". The run continues on truncated
            // history, so "running" is the truth, and the degradation itself
            // is narrated by the renderers that have a transcript.
            //
            // Not "error", for two reasons: the turn is not failing, and this
            // VM's AgentEndEvent arm resets to "idle" unconditionally — so an
            // "error" would not even survive to the end of the run, and would
            // disagree with the store projection, which deliberately preserves
            // it (#687).
            case CompactionFailedEvent:
                Status = "running";
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Set the active model so the view model can compute context usage and
    ///     accumulate step cost via the model's <see cref="Pricing" />.
    /// </summary>
    /// <param name="model">The resolved model info (may be null).</param>
    public void SetModel(ModelInfo? model)
    {
        ContextWindow = model?.ContextWindow ?? 0;
        _pricing = model?.Pricing;
    }

    /// <summary>
    ///     Reset all counters to zero. Bound to the <c>Reset</c> command.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        Cost = 0;
        TokensIn = 0;
        TokensOut = 0;
        RequestTokens = 0;
        QueuedCount = 0;
        Status = "idle";
    }
}

/// <summary>
///     Chat history view model — accumulates messages with streaming support.
/// </summary>
public sealed partial class ChatHistoryViewModel : ObservableObject, ITuiViewModel
{
    private readonly ObservableCollection<ChatEntry> _entries = new();

    /// <summary>
    /// Guards <see cref="_entries"/>: event-thread appends vs draw-thread
    /// enumeration (ENG12 #284, TGui snapshot pattern — draw paths iterate
    /// a copy taken under this lock, never the live collection).
    /// </summary>
    private readonly object _entriesLock = new();

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
    ///     In-flight tool calls by id, so a <c>tool-result</c> entry can name
    ///     its tool (transcript grouping, #1171). Bounded: entries leave on
    ///     <see cref="ToolExecutionEndEvent"/>.
    /// </summary>
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

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
                        _toolNames[tcs.Id] = tcs.ToolName;
                        AddEntry(new ChatEntry("tool", $"→ {tcs.ToolName}", DateTimeOffset.UtcNow, tcs.Id, tcs.ToolName));
                        break;
                }
                break;

            case MessageEndEvent:
                if (!string.IsNullOrEmpty(ThinkingText))
                    AddEntry(new ChatEntry("thinking", ThinkingText, DateTimeOffset.UtcNow));
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
                _toolNames.TryGetValue(tee.ToolCallId, out string? resultTool);
                _toolNames.Remove(tee.ToolCallId);
                AddEntry(new ChatEntry("tool-result", $"{label} {preview}", DateTimeOffset.UtcNow, tee.ToolCallId, resultTool));
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
    ///     Snapshot of the accumulated chat entries for draw paths (ENG12 #284).
    ///     Copies under <see cref="_entriesLock"/> so a mid-draw event-thread
    ///     append cannot invalidate the draw iteration.
    /// </summary>
    public ChatEntry[] SnapshotEntries()
    {
        lock (_entriesLock)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>
    ///     Append a new chat entry. Called by event handlers when a message or tool result finalizes.
    /// </summary>
    /// <param name="entry">The entry to append.</param>
    public void AddEntry(ChatEntry entry)
    {
        lock (_entriesLock)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>
    ///     Clear all entries and reset streaming state.
    /// </summary>
    public void Clear()
    {
        lock (_entriesLock)
        {
            _entries.Clear();
        }
        _toolNames.Clear();
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
/// <remarks>
///     <para>
///         <b>vm-dedup canon (audit 27-G):</b> TUI canon diff-list VM (<c>ITuiViewModel</c>,
///         event-driven, Next/Previous navigation), bound by <c>DiffPreviewView</c> and
///         <c>BaseTuiRenderer</c>. Distinct from the Framework side-by-side compute VM,
///         the Desktop unified-diff VM and the WPF hunk VM — do not merge.
///     </para>
/// </remarks>
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

    public void AddDiff(string diffContent)
    {
        _diffs.Add(new DiffEntry("diff", diffContent, DateTimeOffset.UtcNow));
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
/// <param name="Role">The role string (<c>user</c>, <c>assistant</c>, <c>tool</c>, <c>tool-result</c>, <c>thinking</c>, <c>system</c>).</param>
/// <param name="Content">The entry's text content.</param>
/// <param name="Timestamp">When the entry was created.</param>
/// <param name="ToolCallId">Stable tool-call identity for <c>tool</c> / <c>tool-result</c> entries (transcript grouping, #1171); null otherwise.</param>
/// <param name="ToolName">Tool name for <c>tool</c> / <c>tool-result</c> entries (transcript grouping, #1171); null otherwise.</param>
public sealed record ChatEntry(
    string Role,
    string Content,
    DateTimeOffset Timestamp,
    string? ToolCallId = null,
    string? ToolName = null);

/// <summary>
///     A single diff entry.
/// </summary>
/// <param name="ToolName">The tool that produced the change (e.g. <c>write</c>, <c>edit</c>).</param>
/// <param name="Output">The tool's output (typically includes the file path and a diff summary).</param>
/// <param name="Timestamp">When the change was recorded.</param>
public sealed record DiffEntry(string ToolName, string Output, DateTimeOffset Timestamp);
