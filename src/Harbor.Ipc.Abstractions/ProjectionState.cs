using System.Collections.Concurrent;
using Harbor.Abstractions.Events;
namespace Harbor.Ipc;
/// <summary>
///     The mutable per-host state that <see cref="AgentEventProjector" /> needs in
///     order to turn an <see cref="AgentEvent" /> into a wire-stable
///     <see cref="HarborEvent" />.
/// </summary>
/// <remarks>
///     <para>
///         One instance per streaming host — the in-process client and the IPC
///         broadcaster each own one. It carries only what the projection itself
///         owns: the session whose run is currently streaming, and the per-session
///         turn index. Lease-based ROUTING stays in the host and only READS this
///         state, so a projection never decides who receives an event.
///     </para>
///     <para>
///         <b>Thread safety:</b> <see cref="ActiveSessionId" /> is volatile and the
///         turn table is concurrent, so a host may project from more than one
///         thread. Turn state is SESSION-SCOPED on purpose: two parallel agent runs
///         each advance their own index, so a shared counter would leak run A's turn
///         into run B's emitted <c>TurnEnd</c>.
///     </para>
/// </remarks>
public sealed class ProjectionState
{
    private readonly ConcurrentDictionary<string, int> _turnsBySession = new();
    private volatile string? _activeSessionId;

    /// <summary>
    ///     Session of the run currently streaming; null before the first run.
    ///     Legacy emitters that carry no session id resolve against it.
    /// </summary>
    public string? ActiveSessionId => _activeSessionId;

    /// <summary>
    ///     Open a new run for <paramref name="sessionId" />: it becomes the active
    ///     session and its turn index restarts at zero.
    /// </summary>
    /// <param name="sessionId">The starting session.</param>
    public void BeginRun(string sessionId)
    {
        _activeSessionId = sessionId;
        _turnsBySession[sessionId] = 0;
    }

    /// <summary>
    ///     Record the 1-based turn index a <c>TurnStartEvent</c> carried — never
    ///     derived or overwritten by the host — filed under the event's own session,
    ///     falling back to the active run for legacy emitters with none.
    /// </summary>
    /// <param name="sessionId">Owning session, or null for a legacy emitter.</param>
    /// <param name="turnIndex">The index the event carried.</param>
    public void RecordTurn(string? sessionId, int turnIndex)
    {
        string? session = sessionId ?? _activeSessionId;
        if (session is not null)
        {
            _turnsBySession[session] = turnIndex;
        }
    }

    /// <summary>
    ///     Current turn of <paramref name="sessionId" />, or 0 when unknown. A null
    ///     id resolves against the active run, matching the single-run behavior
    ///     legacy emitters were written against.
    /// </summary>
    /// <param name="sessionId">Owning session, or null for a legacy emitter.</param>
    public int ResolveTurn(string? sessionId)
    {
        string? session = sessionId ?? _activeSessionId;
        return session is not null && _turnsBySession.TryGetValue(session, out int turn) ? turn : 0;
    }
}
