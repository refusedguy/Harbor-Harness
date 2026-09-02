using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>Message builders with sensible defaults (P.6: −15 fixture copies).</summary>
public static class TestMessages
{
    public static UserMessage User(string content, string sessionId = "session-1") => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        content,
        "user",
        "test-model");

    public static AssistantMessage Assistant(string text, string sessionId = "session-1") => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        [new TextPart(text)],
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    public static ToolResultMessage ToolResult(string toolName, string output, string callId = "call-1", string sessionId = "session-1") => new(
        Guid.NewGuid().ToString("N"),
        sessionId,
        DateTimeOffset.UtcNow,
        [new ToolResultEntry(callId, toolName, output, false)]);
}

/// <summary>In-memory <see cref="ISessionContext" /> with steering helpers.</summary>
public sealed class TestSessionContext(Session session, IReadOnlyList<AgentMessage>? seedMessages = null) : ISessionContext
{
    private readonly List<AgentMessage> _messages = [.. seedMessages ?? []];

    public Session Session { get; } = session;

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public System.Threading.Channels.Channel<AgentMessage> SteeringQueue { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<AgentMessage>();

    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) => Task.CompletedTask;

    public void EnqueueSteering(params AgentMessage[] messages)
    {
        foreach (AgentMessage message in messages)
        {
            SteeringQueue.Writer.TryWrite(message);
        }
    }
}
