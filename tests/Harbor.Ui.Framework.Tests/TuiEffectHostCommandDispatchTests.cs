using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #1486: the effect runner is a Command registry, not a central switch.
///     A new effect is added by registering a handler. <c>Run</c> is never edited.
/// </summary>
public class TuiEffectHostCommandDispatchTests
{
    private sealed record ProbeEffect : TuiEffect;

    private sealed class ProbeHandler : ITuiEffectHandler<ProbeEffect>
    {
        private readonly TaskCompletionSource<bool> _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Called => _called.Task;

        public void Execute(ProbeEffect effect) => _called.TrySetResult(true);
    }

    private sealed class NoopRunner : IAgentRunner
    {
        public CancellationToken AbortToken => default;
        public void RequestAbort() { }

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ResetAbortSource() { }
    }

    [Test]
    public async Task RegisteredProbeEffect_DispatchesWithoutEditingRun()
    {
        var store = new UiStore();
        var host = new TuiEffectHost(new NoopRunner(), store);
        var handler = new ProbeHandler();

        host.RegisterEffectHandler(handler);
        host.Run(new ProbeEffect());

        await handler.Called.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(handler.Called.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task EffectWithoutHandler_IsNoOp()
    {
        var store = new UiStore();
        var host = new TuiEffectHost(new NoopRunner(), store);

        // None has no registered handler. The former switch's default was a no-op.
        host.Run(new TuiEffect.None());

        await Assert.That(store.State.Chat.Lines.Length).IsEqualTo(0);
    }
}
