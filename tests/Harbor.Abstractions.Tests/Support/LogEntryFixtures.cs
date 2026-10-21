using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.TestKit;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Builds the session-log entries the fold and guard suites read.
/// </summary>
/// <remarks>
///     <para>
///         The fold and guard suites both build a log of user messages and both
///         wrote the same entry factory.
///     </para>
/// </remarks>
internal static class LogEntryFixtures
{
    internal static SessionLogEntry Message(int position, string id, string content) => new()
    {
        Kind = SessionLogEntryKind.Message,
        Id = id,
        Position = position,
        Message = TestMessages.User(content, id: id, sessionId: "s1", createdAt: DateTimeOffset.UnixEpoch),
    };
}
