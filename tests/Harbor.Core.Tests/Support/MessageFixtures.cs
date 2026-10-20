using Harbor.Abstractions.Models;

namespace Harbor.Core.Tests;

/// <summary>
///     The compaction suites' message builders.
/// </summary>
/// <remarks>
///     <para>
///         Three files built the same assistant, user and summary messages. The
///         bodies were byte-identical, so the three copies were three places to
///         change when the message shape moved.
///     </para>
/// </remarks>
internal static class MessageFixtures
{
    internal static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    internal static UserMessage User(string content) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        content,
        "code",
        "test-model");

    internal static AssistantMessage Summary(string text, string? firstKeptId) => new(
        Guid.NewGuid().ToString("N"),
        "session-1",
        DateTimeOffset.UtcNow,
        new[] { new TextPart(text) },
        StopReason.Stop,
        new Usage(0, 0),
        "test-model",
        IsSummary: true,
        SummaryFirstKeptId: firstKeptId);
}
