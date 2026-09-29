using Harbor.Abstractions.Agents;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

public sealed class FakeAgent : IAgent
{
    /// <summary>
    ///     #559: absence is <c>Maybe.None</c> until a state is supplied, so a
    ///     fixture can model an agent the host has not initialized yet instead of
    ///     relying on a <c>null!</c> the signature used to promise was a value.
    /// </summary>
    public Maybe<AgentState> State => _state is { } bound ? Maybe.From(bound) : Maybe<AgentState>.None;

    private readonly CancellationTokenSource _abortSource = new();
    private AgentState? _state;

    public CancellationToken AbortToken => _abortSource.Token;

    public void RequestAbort() => _abortSource.Cancel();

    public FakeAgent(AgentState state)
    {
        _state = state;
    }

    /// <summary>An agent with no bound session — <see cref="State" /> is <c>Maybe.None</c>.</summary>
    public FakeAgent()
    {
    }

    public void Dispose() => _abortSource.Dispose();
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NoopDisposable();
    public Task<Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());
    public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) => Task.FromResult(Result.Success());
    public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void ResetAbortSource() { }
    public void Initialize(Session session, AgentDefinition agent) => _state = AgentState.Idle(session.Id, agent);
    public void Steer(AgentMessage message) { }
    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
