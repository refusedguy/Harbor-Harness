using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
namespace Harbor.Application.Agents.Pipeline;
/// <summary>
///     Mid-run steering injection (audit v2 §3.5 concern #4), extracted verbatim
///     from <c>AgentLoop</c>: drains the session's steering channel into the
///     history. Called INSIDE the turn (right after tool results are persisted —
///     never between assistant tool_calls and their results, providers require
///     that adjacency) and at the turn boundary (Ф2/B2).
/// </summary>
public sealed class SteeringDrainBehavior(
    ITokenTracker tokenTracker,
    ILogger logger)
{
    /// <summary>Drain the whole steering queue into the session history.</summary>
    public async Task DrainAsync(ISessionContext session, CancellationToken ct)
    {
        while (session.SteeringQueue.Reader.TryRead(out AgentMessage? steerMsg))
        {
            // G2: the steering channel is one-per-agent and outlives session
            // rebinds. A message authored against another session must never
            // enter this history — least of all be persisted under this
            // session's id.
            if (!string.Equals(steerMsg.SessionId, session.Session.Id, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Dropped steering message {MessageId} authored for session {MessageSession} while agent is bound to {Session}",
                    steerMsg.Id, steerMsg.SessionId, session.Session.Id);
                continue;
            }

            await session.AppendMessageAsync(steerMsg, ct).ConfigureAwait(false);
            tokenTracker.RecordAppendedMessage(steerMsg);
        }
    }
}
