// SessionStatsAggregator.cs — pure message-history → stats fold shared by the
// session stores.
//
// Linked source (no .csproj): compiled into each store assembly via
// <Compile Include="..\Harbor.Storage.Shared\*.cs" /> — the same mechanism as
// Harbor.Providers.Shared. No <ProjectReference> is added, so the
// Storage_ReferencesOnlyAbstractions architecture rule stays green.
//
// Moved verbatim out of <c>JsonlSessionStore.GetStatsAsync</c> (#184): the
// JSONL store derives stats from the message history on every call, so the
// fold is the single place where "derive" semantics live. The SQLite/Memory
// stores persist the metadata record instead (see ISessionStore remarks).

using System.Collections.Generic;
using Harbor.Abstractions.Models;

namespace Harbor.Storage.Shared;

/// <summary>
///     Derives <see cref="SessionMetadata" /> from a message list by summing
///     <see cref="Usage" /> over assistant messages. Token counters are exact
///     because every message carries its own usage; the cost is NOT derivable
///     here and says so (see <see cref="SessionMetadata.IsCostKnown" />).
/// </summary>
internal static class SessionStatsAggregator
{
    public static SessionMetadata Aggregate(IReadOnlyList<AgentMessage> messages)
    {
        // #653: this used to be a bare `0m` presented as a total cost, which
        // read as "the session was free". It is not a total and never was: a
        // message history records what was SPENT IN TOKENS, never the rates the
        // provider billed it at, so no fold over it can produce a price. The
        // honest output is a floor flagged unknown — the live core fold
        // (SessionMetadata.AddUsage, driven by the resolved ModelInfo.Pricing)
        // is what fills the real number in. Pricing here would also need the
        // store to resolve model rates, which puts a business rule in the
        // storage layer to compute a number the core already knows.
        decimal cost = 0m;
        int inputTokens = 0;
        int outputTokens = 0;
        int reasoningTokens = 0;
        int cacheRead = 0;
        int cacheWrite = 0;
        int count = 0;

        foreach (var msg in messages)
        {
            if (msg is AssistantMessage a)
            {
                inputTokens += a.Usage.InputTokens;
                outputTokens += a.Usage.OutputTokens;
                reasoningTokens += a.Usage.ReasoningTokens ?? 0;
                cacheRead += a.Usage.CacheReadTokens ?? 0;
                cacheWrite += a.Usage.CacheWriteTokens ?? 0;
                count++;
            }
        }

        return new SessionMetadata(
            cost,
            inputTokens,
            outputTokens,
            reasoningTokens,
            cacheRead,
            cacheWrite,
            count,
            null,
            IsCostKnown: false);
    }
}
