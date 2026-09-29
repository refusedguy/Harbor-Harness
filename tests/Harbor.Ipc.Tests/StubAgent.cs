using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Minimal IAgent stub for tests. Records the last Initialize call,
///     tracks IsRunning, and forwards Subscribe listeners. PromptAsync
///     returns success immediately without actually calling an LLM.
/// </summary>
internal sealed class StubAgent : IAgent
{
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = new();
    private readonly object _listenersLock = new();

    public CancellationToken AbortToken => _abortSource.Token;
    public void RequestAbort() => _abortSource.Cancel();
    private CancellationTokenSource _abortSource = new();
    // #559: Maybe, so the stub models "not initialized" the way the contract now
    // does instead of starting from a null the non-nullable annotation hid.
    public Maybe<AgentState> State => _state is { } bound ? Maybe.From(bound) : Maybe<AgentState>.None;

    private AgentState? _state;

    public string? LastPrompt { get; private set; }
    public string? LastSessionId { get; private set; }
    public string? LastAgentName { get; private set; }

    public Task<Result> PromptAsync(string text, CancellationToken ct = default)
    {
        LastPrompt = text;
        if (_state is not { } state)
            return Task.FromResult(Result.Failure("Agent not initialized."));

        _state = state with { IsRunning = true, StartedAt = DateTimeOffset.UtcNow };
        // Emit a minimal AgentStartEvent so event-subscription tests can observe it.
        // Use Task.Run + ContinueWith to await PublishAsync without making PromptAsync async.
        _ = PublishAsync(new AgentStartEvent(state.SessionId, Array.Empty<AgentMessage>(), null), ct)
            .AsTask();
        _state = state with { IsRunning = false, LastActivityAt = DateTimeOffset.UtcNow };
        return Task.FromResult(Result.Success());
    }

    public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default)
        => PromptAsync(message.Content, ct);

    public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     Test stub for <see cref="IAgentRunner.ResetAbortSource" />. Swaps in a
    ///     fresh <see cref="CancellationTokenSource" /> so post-abort prompts in
    ///     tests behave like the real <c>DefaultAgent</c>.
    /// </summary>
    public void ResetAbortSource()
    {
        if (!_abortSource.IsCancellationRequested) return;
        var old = _abortSource;
        _abortSource = new CancellationTokenSource();
        old.Dispose();
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_listenersLock) _listeners.Add(listener);
        return new Unsub(this, listener);
    }

    public void Initialize(Session session, AgentDefinition agent)
    {
        LastSessionId = session.Id;
        LastAgentName = agent.Name.Value;
        _state = AgentState.Idle(session.Id, agent);
    }

    public void Steer(AgentMessage message) { /* no-op */ }

    public void Dispose()
    {
        _abortSource.Dispose();
    }

    internal async ValueTask PublishAsync(AgentEvent evt, CancellationToken ct)
    {
        Func<AgentEvent, CancellationToken, ValueTask>[] snapshot;
        lock (_listenersLock)
        {
            snapshot = _listeners.ToArray();
        }
        foreach (var listener in snapshot)
        {
            await listener(evt, ct).ConfigureAwait(false);
        }
    }

    private sealed class Unsub : IDisposable
    {
        private readonly StubAgent _owner;
        private readonly Func<AgentEvent, CancellationToken, ValueTask> _listener;

        public Unsub(StubAgent owner, Func<AgentEvent, CancellationToken, ValueTask> listener)
        {
            _owner = owner;
            _listener = listener;
        }

        public void Dispose()
        {
            lock (_owner._listenersLock) _owner._listeners.Remove(_listener);
        }
    }
}
