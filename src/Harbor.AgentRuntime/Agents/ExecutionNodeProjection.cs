using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Application.Agents;

/// <summary>
///     Strong identifier for one node of an agent run's execution graph
///     (issue #2673, item A3). Ids are opaque and minted locally by
///     <see cref="ExecutionNodeProjection" />; they are not session ids and
///     never cross the wire as provider handles.
/// </summary>
public sealed record ExecutionNodeId
{
    private ExecutionNodeId(string value) => Value = value;

    /// <summary>The underlying opaque value.</summary>
    public string Value { get; }

    /// <summary>Mint a fresh, process-unique node id.</summary>
    /// <returns>A new identifier.</returns>
    public static ExecutionNodeId New() => new(Guid.NewGuid().ToString("N"));

    /// <summary>Wrap a non-blank value as a node id.</summary>
    /// <param name="value">The candidate value.</param>
    /// <returns>The wrapped identifier.</returns>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public static ExecutionNodeId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Execution node ID cannot be empty.", nameof(value));

        return new(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
///     The kind of work an <see cref="ExecutionNode" /> stands for. The
///     projection currently emits Run, Root, Turn and Tool nodes; Approval and
///     Subagent are part of the vocabulary so a future emitter does not have
///     to change the shape of the graph.
/// </summary>
public enum ExecutionNodeKind
{
    /// <summary>The run container: one AgentStartEvent through its terminal event.</summary>
    Run,

    /// <summary>The root agent node inside a run.</summary>
    Root,

    /// <summary>One provider turn inside the root node.</summary>
    Turn,

    /// <summary>One tool invocation inside a turn.</summary>
    Tool,

    /// <summary>Reserved for an approval request node.</summary>
    Approval,

    /// <summary>Reserved for a delegated subagent node.</summary>
    Subagent,
}

/// <summary>Lifecycle state of an <see cref="ExecutionNode" />.</summary>
public enum ExecutionNodeState
{
    /// <summary>The work is still in flight.</summary>
    Active,

    /// <summary>The work finished successfully.</summary>
    Completed,

    /// <summary>The work finished with an error.</summary>
    Failed,

    /// <summary>The work was cancelled by its caller.</summary>
    Cancelled,
}

/// <summary>
///     One node of the execution graph. Nodes are immutable facts; the
///     projection replaces a node with a terminal copy when its matching end
///     event arrives, so a reader never sees a half-mutated graph.
/// </summary>
/// <param name="Id">Opaque id of this node.</param>
/// <param name="Kind">What kind of work the node represents.</param>
/// <param name="ParentId">Opaque id of the enclosing node, or null for a run.</param>
/// <param name="SessionId">Session that owns the run, when the event carried one.</param>
/// <param name="Label">Human-readable label (tool name, turn ordinal), when useful.</param>
/// <param name="StartedAt">Timestamp of the event that opened the node.</param>
/// <param name="EndedAt">Timestamp of the terminal event, or null while active.</param>
/// <param name="State">Current lifecycle state.</param>
public sealed record ExecutionNode(
    ExecutionNodeId Id,
    ExecutionNodeKind Kind,
    ExecutionNodeId? ParentId,
    string SessionId,
    string? Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    ExecutionNodeState State);

/// <summary>
///     Projects the existing agent event stream into a run-scoped execution
///     graph (issue #2673, item A3). Debug/observability only: no UI migration,
///     no event shape change, no subscription to the real event bus. A caller
///     feeds events in publish order with <see cref="Apply" /> and reads the
///     result from <see cref="NodesByRun" />.
/// </summary>
/// <remarks>
///     Runs are minted here because the emitted <see cref="AgentEvent" />
///     records carry a session id but no run id yet. One AgentStartEvent opens
///     a run; its matching AgentEndEvent or AgentErrorEvent closes it. A
///     second AgentStartEvent closes any still-open run as cancelled first,
///     so overlapping runs cannot leak active nodes into each other.
/// </remarks>
public sealed class ExecutionNodeProjection
{
    private readonly Dictionary<RunId, List<ExecutionNode>> _nodesByRun = [];
    private readonly Dictionary<int, ExecutionNodeId> _turnNodes = [];
    private readonly Dictionary<string, ExecutionNodeId> _toolNodes = new(StringComparer.Ordinal);

    private RunId? _activeRunId;
    private ExecutionNodeId? _runNodeId;
    private ExecutionNodeId? _rootNodeId;
    private ExecutionNodeId? _currentTurnId;
    private string _sessionId = string.Empty;

    /// <summary>
    ///     All runs seen so far, each with an immutable snapshot of its nodes
    ///     in open order. A run stays in the map after it terminates so its
    ///     graph remains available for audit.
    /// </summary>
    public IReadOnlyDictionary<RunId, IReadOnlyList<ExecutionNode>> NodesByRun
    {
        get
        {
            var snapshot = new Dictionary<RunId, IReadOnlyList<ExecutionNode>>(_nodesByRun.Count);
            foreach (KeyValuePair<RunId, List<ExecutionNode>> pair in _nodesByRun)
            {
                snapshot[pair.Key] = pair.Value.ToArray();
            }

            return snapshot;
        }
    }

    /// <summary>The currently open run id, or null between runs.</summary>
    public RunId? ActiveRunId => _activeRunId;

    /// <summary>
    ///     Apply one published event. Unknown event types are ignored so the
    ///     projection can be placed in front of a growing event union without
    ///     having to change callers.
    /// </summary>
    /// <param name="evt">The event to fold in.</param>
    public void Apply(AgentEvent evt)
    {
        if (evt is null)
            throw new ArgumentNullException(nameof(evt));

        switch (evt)
        {
            case AgentStartEvent start:
                StartRun(start);
                break;
            case TurnStartEvent turnStart:
                StartTurn(turnStart);
                break;
            case ToolExecutionStartEvent toolStart:
                StartTool(toolStart);
                break;
            case ToolExecutionEndEvent toolEnd:
                EndTool(toolEnd);
                break;
            case TurnEndEvent turnEnd:
                EndTurn(turnEnd);
                break;
            case AgentEndEvent agentEnd:
                EndRun(agentEnd.Cancelled ? ExecutionNodeState.Cancelled : ExecutionNodeState.Completed, agentEnd.Timestamp);
                break;
            case AgentErrorEvent error:
                EndRun(ExecutionNodeState.Failed, error.Timestamp);
                break;
        }
    }

    private void StartRun(AgentStartEvent evt)
    {
        CloseCurrentRun(ExecutionNodeState.Cancelled, evt.Timestamp);

        RunId runId = RunId.New();
        _activeRunId = runId;
        _sessionId = evt.SessionId ?? string.Empty;

        ExecutionNode run = Add(runId, ExecutionNodeKind.Run, null, _sessionId, null, evt.Timestamp);
        _runNodeId = run.Id;
        _rootNodeId = Add(runId, ExecutionNodeKind.Root, run.Id, _sessionId, "agent", evt.Timestamp).Id;
    }

    private void StartTurn(TurnStartEvent evt)
    {
        if (_activeRunId is null || _rootNodeId is null)
            return;

        if (_turnNodes.TryGetValue(evt.TurnIndex, out ExecutionNodeId previous))
        {
            Finish(previous, evt.Timestamp, ExecutionNodeState.Completed);
        }

        ExecutionNode node = Add(
            _activeRunId,
            ExecutionNodeKind.Turn,
            _rootNodeId,
            evt.SessionId ?? _sessionId,
            $"turn {evt.TurnIndex}",
            evt.Timestamp);
        _turnNodes[evt.TurnIndex] = node.Id;
        _currentTurnId = node.Id;
    }

    private void StartTool(ToolExecutionStartEvent evt)
    {
        if (_activeRunId is null)
            return;

        ExecutionNode node = Add(
            _activeRunId,
            ExecutionNodeKind.Tool,
            _currentTurnId ?? _rootNodeId,
            _sessionId,
            evt.ToolName,
            evt.Timestamp);
        _toolNodes[evt.ToolCallId] = node.Id;
    }

    private void EndTool(ToolExecutionEndEvent evt)
    {
        if (!_toolNodes.TryGetValue(evt.ToolCallId, out ExecutionNodeId id))
            return;

        Finish(id, evt.Timestamp, evt.IsError ? ExecutionNodeState.Failed : ExecutionNodeState.Completed);
        _toolNodes.Remove(evt.ToolCallId);
    }

    private void EndTurn(TurnEndEvent evt)
    {
        if (_currentTurnId is not null)
        {
            Finish(_currentTurnId, evt.Timestamp, ExecutionNodeState.Completed);
        }

        _currentTurnId = null;
        _turnNodes.Clear();
    }

    private void EndRun(ExecutionNodeState state, DateTimeOffset at)
    {
        CloseCurrentRun(state, at);
    }

    private void CloseCurrentRun(ExecutionNodeState state, DateTimeOffset at)
    {
        if (_activeRunId is null)
            return;

        foreach (ExecutionNodeId id in _toolNodes.Values)
        {
            Finish(id, at, state);
        }

        foreach (ExecutionNodeId id in _turnNodes.Values)
        {
            Finish(id, at, state);
        }

        Finish(_rootNodeId, at, state);
        Finish(_runNodeId, at, state);

        _activeRunId = null;
        _runNodeId = null;
        _rootNodeId = null;
        _currentTurnId = null;
        _turnNodes.Clear();
        _toolNodes.Clear();
    }

    private ExecutionNode Add(
        RunId runId,
        ExecutionNodeKind kind,
        ExecutionNodeId? parentId,
        string sessionId,
        string? label,
        DateTimeOffset startedAt)
    {
        if (!_nodesByRun.TryGetValue(runId, out List<ExecutionNode>? nodes))
        {
            nodes = [];
            _nodesByRun[runId] = nodes;
        }

        var node = new ExecutionNode(
            ExecutionNodeId.New(),
            kind,
            parentId,
            sessionId,
            label,
            startedAt,
            EndedAt: null,
            State: ExecutionNodeState.Active);
        nodes.Add(node);
        return node;
    }

    private void Finish(ExecutionNodeId? id, DateTimeOffset at, ExecutionNodeState state)
    {
        if (id is null || _activeRunId is null || !_nodesByRun.TryGetValue(_activeRunId, out List<ExecutionNode>? nodes))
            return;

        for (int i = 0; i < nodes.Count; i++)
        {
            ExecutionNode node = nodes[i];
            if (node.Id != id || node.State != ExecutionNodeState.Active)
                continue;

            nodes[i] = node with { EndedAt = at, State = state };
            return;
        }
    }
}
