using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Core of <c>harbor sessions retitle &lt;session-id&gt;</c> (issue #2684
///     S06): the command-line form of "regenerate this session's title". The
///     naming call lives in <see cref="SessionTitleRegenerator" />; this runner
///     only turns its result into output and an exit code.
/// </summary>
public static class SessionRetitleRunner
{
    /// <summary>
    ///     Regenerate and print the title of one session.
    /// </summary>
    /// <param name="output">Writer for the success line.</param>
    /// <param name="error">Writer for usage and failure lines.</param>
    /// <param name="store">Session store.</param>
    /// <param name="providers">Provider registry used by the naming request.</param>
    /// <param name="sessionId">Session to retitle.</param>
    /// <returns>0 on success, 2 for a missing id and 1 when regeneration fails.</returns>
    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        ISessionStore store,
        IProviderRegistry providers,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await error.WriteLineAsync("Usage: harbor sessions retitle <session-id>").ConfigureAwait(false);
            return 2;
        }

        var result = await SessionTitleRegenerator.RegenerateAsync(store, providers, sessionId).ConfigureAwait(false);
        if (result.IsFailure)
        {
            await error.WriteLineAsync($"Cannot regenerate title for session {sessionId}: {result.Error}")
                .ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"Session {sessionId} retitled → \"{result.Value}\"").ConfigureAwait(false);
        return 0;
    }
}
