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

        var done = await registry.DrainCompletedAsync(session.Session.Id).ConfigureAwait(false);
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
            // #1760: a failure that reached the model carries a code, so the
            // model reads one and a policy can branch on it. The background
            // drain knows only that the sub-agent failed, so the generic code
            // is the honest label.
            entries.Add(new ToolResultEntry(completion.Id, "task", output, isError)
            {
                ErrorInfo = isError ? ToolErrorCodes.Create(ToolErrorCodes.Error) : null,
            });
            BackgroundDrainLog.BackgroundTaskDrained(logger, completion.Id, completion.AgentName);
        }

        var message = new ToolResultMessage(
            Guid.NewGuid().ToString("N"), session.Session.Id, DateTimeOffset.UtcNow, entries);
        await session.AppendMessageAsync(message, ct).ConfigureAwait(false);
        tokenTracker.RecordAppendedMessage(message);
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="BackgroundDrain" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class BackgroundDrainLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Background task drained: id={Id} agent={Agent}")]
    public static partial void BackgroundTaskDrained(ILogger logger, string id, string agent);

}
