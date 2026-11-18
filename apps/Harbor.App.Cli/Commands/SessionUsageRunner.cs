using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Core of <c>harbor sessions usage &lt;session-id&gt;</c>: a read-only,
///     per-session cost-and-usage report (issue #2684 S14). The numbers are
///     the ones <see cref="ISessionStore.GetStatsAsync" /> already derives
///     from the stored history; no second telemetry pipeline is introduced.
/// </summary>
/// <remarks>
///     <para>
///         Cost comes from <see cref="SessionMetadata.IsCostKnown" />. A store
///         that only keeps token counts (the JSONL store derives stats from
///         messages, which carry no prices) reports an unknown price rather
///         than a misleading <c>$0.0000</c> — the same rule the status bar
///         follows (#653). Formatting goes through <see cref="UsdCell.ToUsd" />,
///         the single home of the money cell's shape (#682).
///     </para>
/// </remarks>
public static class SessionUsageRunner
{
    /// <summary>
    ///     Prints the usage report for <paramref name="sessionId" /> to
    ///     <paramref name="output" />. Returns 0 on success, 2 for a missing
    ///     identifier and 1 when the store cannot be read.
    /// </summary>
    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        ISessionStore store,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await error.WriteLineAsync("Usage: harbor sessions usage <session-id>").ConfigureAwait(false);
            return 2;
        }

        var stats = await store.GetStatsAsync(sessionId).ConfigureAwait(false);
        if (stats.IsFailure)
        {
            await error.WriteLineAsync($"Cannot read usage for session {sessionId}: {stats.Error}").ConfigureAwait(false);
            return 1;
        }

        SessionMetadata usage = stats.Value;
        string cost = usage.IsCostKnown ? UsdCell.ToUsd(usage.Cost) : $"{UsdCell.Unpriced} (price unknown)";

        await output.WriteLineAsync($"Session {sessionId} — usage").ConfigureAwait(false);
        await output.WriteLineAsync($"  LLM calls:           {usage.MessageCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Input tokens:        {usage.TokensInput}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Output tokens:       {usage.TokensOutput}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Reasoning tokens:    {usage.TokensReasoning}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Cache read tokens:   {usage.TokensCacheRead}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Cache write tokens:  {usage.TokensCacheWrite}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Cost:                {cost}").ConfigureAwait(false);
        return 0;
    }
}
