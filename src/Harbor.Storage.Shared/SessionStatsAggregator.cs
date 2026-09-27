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
///     <see cref="Usage" /> over assistant messages. Total cost is always
///     zero (cost attribution lives elsewhere); non-assistant messages carry
///     no usage and contribute only ordering.
/// </summary>
internal static class SessionStatsAggregator
{
    public static SessionMetadata Aggregate(IReadOnlyList<AgentMessage> messages)
    {
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
            null);
    }
}
