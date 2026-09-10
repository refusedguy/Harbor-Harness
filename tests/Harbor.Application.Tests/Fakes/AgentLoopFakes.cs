using System.Text.Json;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;

namespace Harbor.Application.Tests.Fakes;

public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker
{
    public void RecordTurnUsage(Usage usage)
    {
    }

    public int Estimate(string text) => 0;

    public int EstimateMessage(AgentMessage message) => 0;

    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => shouldCompact;

    public TokenStats GetStats() => new(0, 0, null, null, null);
}

public sealed class FakeCompactionService : ICompactionService
{
    private readonly Result<CompactionResult> _outcome =
        Result.Failure<CompactionResult>("simulated compaction failure");

    public int Calls { get; private set; }

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_outcome);
    }
}

public sealed class TestSessionContext(Session session, IReadOnlyList<AgentMessage>? seedMessages = null) : ISessionContext
{
    private readonly List<AgentMessage> _messages = [.. seedMessages ?? []];

    public Session Session { get; } = session;

    public IReadOnlyList<AgentMessage> Messages => _messages;

    public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

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
