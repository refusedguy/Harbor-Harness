using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Seeds a session store with the messages a CLI suite needs.
/// </summary>
/// <remarks>
///     <para>
///         The session-fork and session-search suites both needed a store with a
///         titled session and a few messages in it, and both wrote the same
///         seeding loop.
///     </para>
/// </remarks>
internal static class SessionFixtures
{
    internal static async Task<Harbor.Abstractions.Models.Session> SeedAsync(
        MemorySessionStore store, string title, params AgentMessage[] messages)
    {
        var created = (await store.CreateAsync("/tmp", "code", "test", "test-model")).Value;
        if (!string.IsNullOrEmpty(title))
        {
            created = created with { Title = title };
            await store.UpdateAsync(created);
        }

        foreach (var message in messages)
        {
            await store.AppendMessageAsync(created.Id, message with { SessionId = created.Id });
        }

        return created;
    }
}
