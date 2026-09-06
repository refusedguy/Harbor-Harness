using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using System.IO;

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

    public static string RenderText(LlmRequest request)
    {
        var sb = new System.Text.StringBuilder();
        foreach (LlmMessage message in request.Messages)
        {
            if (message is LlmUserMessage user)
            {
                foreach (LlmContentBlock block in user.Content)
                {
                    if (block is LlmTextBlock text)
                    {
                        sb.Append(text.Text);
                    }
                }
            }
            else if (message is LlmToolResultMessage toolResult)
            {
                sb.Append(toolResult.Output);
            }
        }
        return sb.ToString();
    }
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

    public static TestSessionContext Create(params AgentMessage[] seed) => new(
        Session.Create(Path.GetTempPath(), "code", "test", "test-model"),
        seed);

    public static TestSessionContext Create(string tempDir, params AgentMessage[] seed) => new(
        Session.Create(tempDir, "code", "test", "test-model"),
        seed);
}

/// <summary>Session context wrapper that captures every <see cref="ISessionContext.UpdateStatsAsync"/> call.</summary>
public sealed class CapturingStatsSession : ISessionContext
{
    private readonly List<Usage> _captured;
    private readonly TestSessionContext _inner;

    public CapturingStatsSession(Session session, IReadOnlyList<AgentMessage> messages, List<Usage> captured)
    {
        _captured = captured;
        _inner = new TestSessionContext(session, messages);
    }

    public Session Session => _inner.Session;
    public IReadOnlyList<AgentMessage> Messages => _inner.Messages;
    public System.Threading.Channels.Channel<AgentMessage> SteeringQueue => _inner.SteeringQueue;
    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => _inner.AppendMessageAsync(message, ct);
    public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default)
    {
        _captured.Add(usage);
        return Task.CompletedTask;
    }
}
