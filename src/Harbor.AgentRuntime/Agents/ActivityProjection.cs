using System.Globalization;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Application.Agents;

/// <summary>
///     One line of an activity feed: a flat, UI-facing view of a single node of
///     an agent run's execution graph (issue #2673, item A9). Unlike the graph
///     in <see cref="ExecutionNodeProjection" />, an entry carries its owning
///     <see cref="RunId" /> directly so a panel can group, filter and render
///     lines without walking parent links.
/// </summary>
/// <param name="RunId">The run that owns this activity.</param>
/// <param name="Kind">What kind of work the activity stands for.</param>
/// <param name="NodeId">Opaque id of the graph node behind this activity.</param>
/// <param name="ParentId">Opaque id of the enclosing node, or null for a run.</param>
/// <param name="SessionId">Session that owns the run, when the event carried one.</param>
/// <param name="Label">Human-readable label (tool name, turn ordinal), when useful.</param>
/// <param name="StartedAt">Timestamp of the event that opened the node.</param>
/// <param name="EndedAt">Timestamp of the terminal event, or null while active.</param>
/// <param name="State">Current lifecycle state.</param>
public sealed record ActivityEntry(
    RunId RunId,
    ExecutionNodeKind Kind,
    ExecutionNodeId NodeId,
    ExecutionNodeId? ParentId,
    string SessionId,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    ExecutionNodeState State)
{
    /// <summary>Whether this line describes a tool invocation.</summary>
    public bool IsToolCall => Kind == ExecutionNodeKind.Tool;

    /// <summary>Elapsed time once the node has ended, or null while it is active.</summary>
    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;

    /// <summary>
    ///     Render the activity as a single stable log line: label, state and a
    ///     duration when the work has ended. Culture-invariant so the same
    ///     feed renders identically on every machine and in tests.
    /// </summary>
    /// <returns>The rendered line.</returns>
    public string Describe()
    {
        string label = string.IsNullOrWhiteSpace(Label) ? Kind.ToString() : Label!;
        string state = State switch
        {
            ExecutionNodeState.Active => "running",
            ExecutionNodeState.Completed => "done",
            ExecutionNodeState.Failed => "failed",
            ExecutionNodeState.Cancelled => "cancelled",
            _ => State.ToString().ToLowerInvariant(),
        };

        string line = $"{label} [{state}]";
        if (Duration is { } elapsed)
        {
            line += $" ({elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s)";
        }

        return line;
    }
}

/// <summary>
///     Projects the agent event stream into a flat activity feed grouped by
///     run (issue #2673, item A9). It composes <see cref="ExecutionNodeProjection" />
///     for identity and lifecycle, then flattens that graph into ordered,
///     renderable lines. It is debug/observability only: no TUI migration and
///     no change to the event union or runtime behavior.
/// </summary>
public sealed class ActivityProjection
{
    private readonly ExecutionNodeProjection _graph = new();
    private List<ActivityEntry> _entries = [];

    /// <summary>
    ///     Hide tool-call lines from the feed. The flag is applied when the
    ///     feed is read, so toggling it never loses the underlying entries.
    /// </summary>
    public bool HideToolCalls { get; set; }

    /// <summary>All current entries in chronological order, filtered by <see cref="HideToolCalls" />.</summary>
    public IReadOnlyList<ActivityEntry> Entries
    {
        get
        {
            var visible = new List<ActivityEntry>(_entries.Count);
            foreach (ActivityEntry entry in _entries)
            {
                if (!HideToolCalls || !entry.IsToolCall)
                    visible.Add(entry);
            }

            return visible.ToArray();
        }
    }

    /// <summary>The same entries grouped by their owning run, in first-seen run order.</summary>
    public IReadOnlyDictionary<RunId, IReadOnlyList<ActivityEntry>> EntriesByRun
    {
        get
        {
            var grouped = new Dictionary<RunId, List<ActivityEntry>>();
            foreach (ActivityEntry entry in Entries)
            {
                if (!grouped.TryGetValue(entry.RunId, out List<ActivityEntry>? lines))
                {
                    lines = [];
                    grouped[entry.RunId] = lines;
                }

                lines.Add(entry);
            }

            var snapshot = new Dictionary<RunId, IReadOnlyList<ActivityEntry>>(grouped.Count);
            foreach (KeyValuePair<RunId, List<ActivityEntry>> pair in grouped)
            {
                snapshot[pair.Key] = pair.Value.ToArray();
            }

            return snapshot;
        }
    }

    /// <summary>The currently open run id, or null between runs.</summary>
    public RunId? ActiveRunId => _graph.ActiveRunId;

    /// <summary>
    ///     One-line status text for the most recent run, derived from the
    ///     projected activity graph (#3383). The Run node is the stable line per
    ///     run: its <see cref="ActivityEntry.Describe"/> follows the run from
    ///     "running" to "done"/"failed" without per-tool id churn. Returns null
    ///     before the first <c>AgentStartEvent</c>, so a status bar can keep its
    ///     legacy text until there is activity to show.
    /// </summary>
    public string? DescribeStatus()
    {
        // _entries is already chronological; the last Run is the newest one.
        // Reading it directly avoids the snapshot allocation of Entries on the
        // render path.
        ActivityEntry? status = null;
        foreach (ActivityEntry entry in _entries)
        {
            if (entry.Kind == ExecutionNodeKind.Run)
                status = entry;
        }

        return status?.Describe();
    }

    /// <summary>
    ///     Apply one published event. The graph is advanced first, then the
    ///     flat feed is rebuilt from its run-scoped snapshot.
    /// </summary>
    /// <param name="evt">The event to fold in.</param>
    /// <exception cref="ArgumentNullException">The event is null.</exception>
    public void Apply(AgentEvent evt)
    {
        if (evt is null)
            throw new ArgumentNullException(nameof(evt));

        _graph.Apply(evt);
        Rebuild();
    }

    private void Rebuild()
    {
        var rebuilt = new List<ActivityEntry>();
        foreach (KeyValuePair<RunId, IReadOnlyList<ExecutionNode>> run in _graph.NodesByRun)
        {
            foreach (ExecutionNode node in run.Value)
            {
                rebuilt.Add(new ActivityEntry(
                    run.Key,
                    node.Kind,
                    node.Id,
                    node.ParentId,
                    node.SessionId,
                    node.Label,
                    node.StartedAt,
                    node.EndedAt,
                    node.State));
            }
        }

        rebuilt.Sort(static (a, b) => a.StartedAt.CompareTo(b.StartedAt));
        _entries = rebuilt;
    }
}
