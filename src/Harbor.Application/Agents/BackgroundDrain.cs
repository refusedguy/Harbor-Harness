using Microsoft.Extensions.Logging;

namespace Harbor.Application.Agents;

/// <summary>
///     Background-task ping extracted from <see cref="AgentLoop" /> ([G4]):
///     drains finished detached runs for the session into a tool-result message.
///     No-op when unwired or nothing finished.
/// </summary>
/// <remarks>
///     Shares the loop's logger so log categories stay identical to pre-extraction.
/// </remarks>
internal sealed class BackgroundDrain(
    IBackgroundTaskRegistry? registry,
    ITokenTracker tokenTracker,
    ILogger logger)
{
    /// <summary>
    ///     Drain finished detached runs for this session into a tool-result message.
    /// </summary>
    internal async Task DrainAsync(ISessionContext session, CancellationToken ct)
    {
        if (registry is null)
        {
            return;
        }

        var done = registry.DrainCompleted(session.Session.Id);
        if (done.Count == 0)
        {
            return;
        }

        var entries = new List<ToolResultEntry>(done.Count);
        for (int i = 0; i < done.Count; i++)
        {
            var completion = done[i];
            // ROP boundary #101: single Match inspection — the payload and the
            // error flag come out of one pass instead of Match + IsFailure.
            var (output, isError) = completion.Result.Match(
                run => ($"[background sub-agent '{completion.AgentName}' finished — session {run.SessionId}, {run.NewMessages} message(s)]\n\n{run.FinalOutput}", false),
                err => ($"[background sub-agent '{completion.AgentName}' failed: {err}]", true));
            entries.Add(new ToolResultEntry(completion.Id, "task", output, isError));
            logger.LogInformation("Background task drained: id={Id} agent={Agent}", completion.Id, completion.AgentName);
        }

        var message = new ToolResultMessage(
            Guid.NewGuid().ToString("N"), session.Session.Id, DateTimeOffset.UtcNow, entries);
        await session.AppendMessageAsync(message, ct).ConfigureAwait(false);
        tokenTracker.RecordAppendedMessage(message);
    }
}
