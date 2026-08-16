using System.Collections.Immutable;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Reducers;

/// <summary>
///     Pure reducer: <c>(AgentEvent, SessionsViewState) → SessionsViewState</c>.
/// </summary>
/// <remarks>
///     <para>
///         Maps agent activity into session list state. Every interactive renderer
///         funnels its events through this — there is no per-renderer
///         <c>switch (AgentEvent)</c> anywhere.
///     </para>
///     <para>
///         Must never call into <c>IAgent</c> or perform I/O — side-effects are the
///         responsibility of the host effect runner.
///     </para>
/// </remarks>
public static partial class SessionsReducer
{
    /// <summary>
    ///     Apply an agent event to the sessions view state, returning the next
    ///     immutable snapshot.
    /// </summary>
    public static SessionsViewState Reduce(AgentEvent @event, SessionsViewState state) => @event switch
    {
        AgentStartEvent ase => OnAgentStart(state, ase),
        SessionChangedEvent sce => state with { ActiveSessionId = SessionId.Create(sce.SessionId) },
        _ => state
    };

    private static SessionsViewState OnAgentStart(SessionsViewState state, AgentStartEvent ase)
    {
        var sessionId = SessionId.Create(ase.SessionId);
        var title = ExtractTitle(ase.Messages) ?? $"Session {ase.Timestamp:yyyy-MM-dd HH:mm}";
        var info = new SessionInfo(
            sessionId,
            title,
            ase.Timestamp,
            ase.Timestamp,
            "active");

        var builder = state.Sessions.ToBuilder();
        int existing = -1;
        for (int i = 0; i < builder.Count; i++)
        {
            if (builder[i].SessionId.Equals(sessionId))
            {
                existing = i;
                break;
            }
        }

        if (existing >= 0)
            builder[existing] = info;
        else
            builder.Add(info);

        return state with { Sessions = builder.ToImmutable() };
    }

    private static string? ExtractTitle(IReadOnlyList<AgentMessage> messages)
    {
        foreach (var m in messages)
        {
            if (m is UserMessage { Content: { Length: > 0 } content })
            {
                string trimmed = content.Trim();
                if (trimmed.Length > 48)
                    trimmed = trimmed[..48] + "…";
                return trimmed;
            }
        }

        return null;
    }
}
