using Harbor.Abstractions.Agents;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

public sealed class FakeAgent : IAgent
{
    public AgentState State { get; }
    public CancellationTokenSource AbortSource { get; } = new();
    public FakeAgent(AgentState state)
    {
        State = state;
    }
    public void Dispose() => AbortSource.Dispose();
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NoopDisposable();
    public Task<Result> PromptAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());
    public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) => Task.FromResult(Result.Success());
    public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void ResetAbortSource() { }
    public void Initialize(Session session, AgentDefinition agent) { }
    public void Steer(AgentMessage message) { }
    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
