using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

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

    public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     #1765: the durability checkpoint. This double keeps every record in
    ///     memory and loses none of them, so its state survives as far as the
    ///     double is concerned. That is the answer a test double owes the loop.
    /// </summary>
    public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default)
        => Task.FromResult(Result.Success());

    public void EnqueueSteering(params AgentMessage[] messages)
    {
        foreach (AgentMessage message in messages)
        {
            SteeringQueue.Writer.TryWrite(message);
        }
    }

    public static TestSessionContext Create(params AgentMessage[] seed) => new(
        Session.Create(TestTempDirs.NewDirectory("harbor-test-session"), "code", "test", "test-model"),
        seed);

    public static TestSessionContext Create(string tempDir, params AgentMessage[] seed) => new(
        Session.Create(tempDir, "code", "test", "test-model"),
        seed);
}

/// <summary>Session context wrapper that captures every <see cref="ISessionContext.UpdateStatsAsync"/> call.</summary>
public sealed class CapturingStatsSession : ISessionContext
{
    private readonly List<Usage> _captured;
    private readonly List<Pricing> _pricing;
    private readonly TestSessionContext _inner;

    public CapturingStatsSession(Session session, IReadOnlyList<AgentMessage> messages, List<Usage> captured)
        : this(session, messages, captured, [])
    {
    }

    /// <summary>
    ///     Also captures the rate table each call was priced with, so a test can
    ///     assert the core passed the RESOLVED model's prices (#653) instead of
    ///     any other model's.
    /// </summary>
    public CapturingStatsSession(
        Session session,
        IReadOnlyList<AgentMessage> messages,
        List<Usage> captured,
        List<Pricing> pricing)
    {
        _captured = captured;
        _pricing = pricing;
        _inner = new TestSessionContext(session, messages);
    }

    /// <summary>Rate tables seen by <see cref="UpdateStatsAsync" />, in call order.</summary>
    public IReadOnlyList<Pricing> Pricings => _pricing;

    public Session Session => _inner.Session;
    public IReadOnlyList<AgentMessage> Messages => _inner.Messages;
    public System.Threading.Channels.Channel<AgentMessage> SteeringQueue => _inner.SteeringQueue;
    public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) => _inner.AppendMessageAsync(message, ct);
    public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default)
    {
        _captured.Add(usage);
        _pricing.Add(pricing);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     #1765: the durability checkpoint, forwarded to the inner double.
    ///     this wrapper adds bookkeeping, not a store.
    /// </summary>
    public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default)
        => _inner.CheckpointAsync(reason, ct);
}
