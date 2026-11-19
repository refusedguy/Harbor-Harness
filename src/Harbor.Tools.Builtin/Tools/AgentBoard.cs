// AgentBoard.cs: #2680 F105. The typed, in-memory half of the shared agent
// board. It carries INFO/ASK/RESULT/HOLD/VETO messages between a main session
// and its subagents without touching a session store, an event bus or a TUI.
// Pure over its inputs: no clock, no I/O, no product wiring, so it stays
// AOT-safe and golden-neutral. A future persistence adapter can replay the
// messages this board hands back.
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The kinds a shared agent board carries (#2680 F105).
/// </summary>
public enum AgentBoardMessageKind
{
    /// <summary>Informational note; never blocks the recipient.</summary>
    Info,

    /// <summary>A question that expects an answer from the recipient.</summary>
    Ask,

    /// <summary>The answer to a previous <see cref="Ask" />.</summary>
    Result,

    /// <summary>A request to pause work until it is released.</summary>
    Hold,

    /// <summary>A rejection that permanently stops the addressed work.</summary>
    Veto,
}

/// <summary>
///     Lifecycle state of a board message. Only <see cref="Pending" /> messages
///     are handed to recipients; held, vetoed and delivered ones stay in history.
/// </summary>
public enum AgentBoardMessageState
{
    /// <summary>Waiting to be delivered.</summary>
    Pending,

    /// <summary>Temporarily withheld; can be released back to <see cref="Pending" />.</summary>
    Held,

    /// <summary>Rejected; terminal.</summary>
    Vetoed,

    /// <summary>Already consumed; terminal.</summary>
    Delivered,
}

/// <summary>
///     One immutable board message. The board assigns <see cref="Id" /> and
///     <see cref="State" />; callers never construct one directly.
/// </summary>
/// <param name="Id">Monotonic board-local id.</param>
/// <param name="Kind">Message kind.</param>
/// <param name="SourceSession">Session that posted the message.</param>
/// <param name="TargetSession">Addressed session, or null for a broadcast.</param>
/// <param name="Body">Message text, trimmed at post time.</param>
/// <param name="State">Current lifecycle state.</param>
public sealed record AgentBoardMessage(
    long Id,
    AgentBoardMessageKind Kind,
    string SourceSession,
    string? TargetSession,
    string Body,
    AgentBoardMessageState State)
{
    /// <summary>Whether the message is addressed to every session.</summary>
    public bool IsBroadcast => TargetSession is null;

    /// <summary>Whether the message is currently deliverable.</summary>
    public bool IsDeliverable => State is AgentBoardMessageState.Pending;
}

/// <summary>
///     Bounded, thread-safe shared board for a main session and its subagents
///     (#2680 F105). Messages are posted with a typed kind and move through a
///     small state machine: <see cref="AgentBoardMessageKind.Hold" /> pauses,
///     release resumes, <see cref="AgentBoardMessageKind.Veto" /> rejects and
///     delivery consumes. Held and vetoed messages are never returned as
///     pending, which is the hold/veto semantics the board exists to provide.
/// </summary>
/// <remarks>
///     <para>
///         This is a pure leaf: it keeps messages in memory, assigns ids from a
///         counter and enforces a hard capacity. It reads no clock, writes no
///         store and raises no events. Persistence on <c>ISessionStore</c> and
///         tool/event wiring are separate, later slices.
///     </para>
///     <para>
///         A broadcast has a null target and is pending for every session. A
///         targeted message is pending only for the session named at post time.
///     </para>
/// </remarks>
public sealed class AgentBoard : IAgentBoard
{
    /// <summary>Hard cap on retained messages; the oldest terminal entry is evicted first.</summary>
    public const int MaxMessages = 256;

    /// <summary>
    ///     #3384: the Domain-facing half of the board. The loop posts a kind as
    ///     text; this is the one place that maps it to the typed
    ///     <see cref="AgentBoardMessageKind" /> the board stores, so the
    ///     companions stay behind the board's own boundary.
    /// </summary>
    bool IAgentBoard.Post(string kind, string sourceSession, string? targetSession, string body)
    {
        AgentBoardMessageKind parsed = kind switch
        {
            "ask" => AgentBoardMessageKind.Ask,
            "result" => AgentBoardMessageKind.Result,
            "hold" => AgentBoardMessageKind.Hold,
            "veto" => AgentBoardMessageKind.Veto,
            _ => AgentBoardMessageKind.Info,
        };

        return Post(parsed, sourceSession, targetSession, body).IsSuccess;
    }

    private readonly object _gate = new();
    private readonly List<AgentBoardMessage> _messages = new();
    private long _nextId;

    /// <summary>Number of messages currently retained.</summary>
    public int Count
    {
        get { lock (_gate) return _messages.Count; }
    }

    /// <summary>
    ///     Posts a message. Blank source/body and unknown kinds are rejected;
    ///     a blank target becomes a broadcast. Text fields are trimmed.
    /// </summary>
    public Result<AgentBoardMessage> Post(
        AgentBoardMessageKind kind,
        string sourceSession,
        string? targetSession,
        string body)
    {
        if (!IsKnownKind(kind)) return Result.Failure<AgentBoardMessage>("Unknown board message kind.");
        if (string.IsNullOrWhiteSpace(sourceSession)) return Result.Failure<AgentBoardMessage>("Source session is required.");
        if (string.IsNullOrWhiteSpace(body)) return Result.Failure<AgentBoardMessage>("Message body is required.");

        string source = sourceSession.Trim();
        string? target = string.IsNullOrWhiteSpace(targetSession) ? null : targetSession.Trim();
        string text = body.Trim();

        lock (_gate)
        {
            var message = new AgentBoardMessage(
                ++_nextId,
                kind,
                source,
                target,
                text,
                AgentBoardMessageState.Pending);

            _messages.Add(message);
            EvictIfNeeded();
            return Result.Success(message);
        }
    }

    /// <summary>Moves a pending message to <see cref="AgentBoardMessageState.Held" />.</summary>
    public Result<AgentBoardMessage> Hold(long id) =>
        Transition(id, AgentBoardMessageState.Pending, AgentBoardMessageState.Held, "hold");

    /// <summary>Moves a held message back to <see cref="AgentBoardMessageState.Pending" />.</summary>
    public Result<AgentBoardMessage> Release(long id) =>
        Transition(id, AgentBoardMessageState.Held, AgentBoardMessageState.Pending, "release");

    /// <summary>Rejects a pending or held message; the veto is terminal.</summary>
    public Result<AgentBoardMessage> Veto(long id)
    {
        lock (_gate)
        {
            AgentBoardMessage? message = Find(id);
            if (message is null) return Result.Failure<AgentBoardMessage>("Unknown board message id.");
            if (message.State is not (AgentBoardMessageState.Pending or AgentBoardMessageState.Held))
                return Result.Failure<AgentBoardMessage>("Only a pending or held message can be vetoed.");

            var vetoed = message with { State = AgentBoardMessageState.Vetoed };
            Replace(message, vetoed);
            return Result.Success(vetoed);
        }
    }

    /// <summary>Marks a pending message as delivered; delivery is terminal.</summary>
    public Result<AgentBoardMessage> Deliver(long id) =>
        Transition(id, AgentBoardMessageState.Pending, AgentBoardMessageState.Delivered, "deliver");

    /// <summary>Looks up one retained message by id.</summary>
    public AgentBoardMessage? Get(long id)
    {
        lock (_gate) return Find(id);
    }

    /// <summary>
    ///     Returns messages pending for <paramref name="session" />: everything
    ///     broadcast plus messages addressed to that exact session, in post order.
    /// </summary>
    public IReadOnlyList<AgentBoardMessage> PendingFor(string session)
    {
        if (string.IsNullOrWhiteSpace(session)) return Array.Empty<AgentBoardMessage>();

        string target = session.Trim();
        lock (_gate)
        {
            var pending = new List<AgentBoardMessage>();
            foreach (AgentBoardMessage message in _messages)
            {
                if (message.State is not AgentBoardMessageState.Pending) continue;
                if (message.TargetSession is null || string.Equals(message.TargetSession, target, StringComparison.Ordinal))
                    pending.Add(message);
            }

            return pending.ToArray();
        }
    }

    /// <summary>
    ///     Returns up to <paramref name="limit" /> retained messages visible to
    ///     <paramref name="session" />, newest first. A limit outside [1,
    ///     <see cref="MaxMessages" />] is clamped.
    /// </summary>
    public IReadOnlyList<AgentBoardMessage> HistoryFor(string session, int limit)
    {
        if (string.IsNullOrWhiteSpace(session)) return Array.Empty<AgentBoardMessage>();

        string target = session.Trim();
        int take = Math.Clamp(limit, 1, MaxMessages);
        lock (_gate)
        {
            var history = new List<AgentBoardMessage>();
            for (int i = _messages.Count - 1; i >= 0 && history.Count < take; i--)
            {
                AgentBoardMessage message = _messages[i];
                if (message.TargetSession is null || string.Equals(message.TargetSession, target, StringComparison.Ordinal))
                    history.Add(message);
            }

            return history.ToArray();
        }
    }

    private Result<AgentBoardMessage> Transition(
        long id,
        AgentBoardMessageState from,
        AgentBoardMessageState to,
        string action)
    {
        lock (_gate)
        {
            AgentBoardMessage? message = Find(id);
            if (message is null) return Result.Failure<AgentBoardMessage>("Unknown board message id.");
            if (message.State != from)
                return Result.Failure<AgentBoardMessage>($"Cannot {action} a message in state {message.State}.");

            var moved = message with { State = to };
            Replace(message, moved);
            return Result.Success(moved);
        }
    }

    private AgentBoardMessage? Find(long id)
    {
        foreach (AgentBoardMessage message in _messages)
        {
            if (message.Id == id) return message;
        }

        return null;
    }

    private void Replace(AgentBoardMessage old, AgentBoardMessage updated)
    {
        int index = _messages.IndexOf(old);
        if (index >= 0) _messages[index] = updated;
    }

    private void EvictIfNeeded()
    {
        while (_messages.Count > MaxMessages)
        {
            int victim = _messages.FindIndex(m => m.State is AgentBoardMessageState.Delivered or AgentBoardMessageState.Vetoed);
            _messages.RemoveAt(victim >= 0 ? victim : 0);
        }
    }

    private static bool IsKnownKind(AgentBoardMessageKind kind) => kind switch
    {
        AgentBoardMessageKind.Info => true,
        AgentBoardMessageKind.Ask => true,
        AgentBoardMessageKind.Result => true,
        AgentBoardMessageKind.Hold => true,
        AgentBoardMessageKind.Veto => true,
        _ => false,
    };
}
