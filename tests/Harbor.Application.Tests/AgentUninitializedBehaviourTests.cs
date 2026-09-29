// AgentUninitializedBehaviourTests.cs — the behavioural half of issue #559.
//
// The type change (`IAgent.State` is now `Maybe<AgentState>`) is proved by
// AgentStateContractRules in the architecture project, which only checks the
// SHAPE. This file pins the behaviour the shape is supposed to buy: an agent
// the host has not called `Initialize` on must be PREDICTABLE, not merely
// non-crashing.
//
// What "predictable" means here, and why each case earns its place:
//
//   * Observational reads answer with the truth, not with a fallback that hides
//     a mistake: an agent with no session has no run, so `IsRunning()` is false.
//   * Operations that cannot work without a session FAIL with a message that
//     names the fix, and they fail BEFORE any side effect — a pre-Initialize
//     prompt must not reach `ISessionStore.AppendMessageAsync` with a
//     fabricated session id.
//   * Operations that are meaningful while unbound (subscribe, steer, abort,
//     reset, dispose) keep working, because the constructor runs them.
//   * The event-bus fan-out resolves its "which session is this for" question
//     while unbound, which is the one path that used to read `State?.SessionId`
//     from a background continuation.
//
// The pre-#559 failure mode this replaces: `PromptPipeline.IsBusy` read
// `host.Agent.State.IsRunning` on every frame and the three contrib TEA bridges
// read `agent.State.Agent.*` inside their CONSTRUCTORS, where the caller cannot
// sequence around Initialize at all — so any host or test that built a bridge
// first took a NullReferenceException at construction time.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakeEventBus = Harbor.TestKit.FakeEventBus;
using FakeSessionStore = Harbor.Application.Tests.Fakes.FakeSessionStore;

namespace Harbor.Application.Tests;

/// <summary>
///     #559: the observable behaviour of a <see cref="DefaultAgent" /> that has not been
///     <see cref="DefaultAgent.Initialize" />d yet.
/// </summary>
public sealed class AgentUninitializedBehaviourTests
{
    private static Session NewSession() =>
        Session.Create("/tmp/harbor-agent-uninitialized-tests", "code", "test", "test-model");

    private static DefaultAgent NewAgent(Session session, out FakeSessionStore store)
    {
        store = new FakeSessionStore(session);
        return new DefaultAgent(store, new FakeAgentLoop(), new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
    }

    /// <summary>
    ///     Loop that parks inside the run until released. The block has to be in the LOOP, not
    ///     in the store: <c>DefaultAgent.PromptAsync</c> persists the user message BEFORE it
    ///     flips <c>IsRunning</c>, so a store-gated run is not yet "running" to a consumer and
    ///     would make the probe assertions below lie.
    /// </summary>
    private sealed class BlockingAgentLoop(TaskCompletionSource<Result> release) : IAgentLoop
    {
        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
            => release.Task;
    }

    /// <summary>
    ///     Bounded poll for a condition a background run establishes asynchronously — the same
    ///     idiom <c>CancelProtocolGenerationTests</c> uses, because a bare <c>Task.Yield()</c> is
    ///     not a synchronisation point.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        return condition();
    }

    // ── The contract itself ──────────────────────────────────────────────────

    [Test]
    public async Task State_BeforeInitialize_HasNoValue()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out _);

        await Assert.That(subject.State.HasNoValue).IsTrue()
            .Because(
                "#559: the property used to be declared non-nullable and assigned `null!`, which asked "
                + "the compiler to accept a value nobody had produced. Absence has to be visible in the type.");
        await Assert.That(subject.State.HasValue).IsFalse()
            .Because("HasValue and HasNoValue must agree; a Maybe that answers both is not a Maybe.");
    }

    [Test]
    public async Task State_AfterInitialize_CarriesTheBoundSessionAndDefinition()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out _);
        AgentDefinition definition = TestAgents.AllowAll();

        subject.Initialize(session, definition);

        await Assert.That(subject.State.HasValue).IsTrue()
            .Because("Initialize is the only transition from None to Just; after it the snapshot is real.");
        await Assert.That(subject.State.Value.SessionId).IsEqualTo(session.Id)
            .Because("The session id is the one the caller bound, not an invented placeholder.");
        await Assert.That(subject.State.Value.Agent).IsEqualTo(definition)
            .Because("The definition is the one the caller bound — the whole reason #559 rejects a fabricated Uninitialized sentinel.");
        await Assert.That(subject.State.Value.IsRunning).IsFalse()
            .Because("A freshly bound agent has no run in flight.");
    }

    // ── The probe every consumer now shares ─────────────────────────────────

    [Test]
    public async Task IsRunning_BeforeInitialize_IsFalse()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out _);

        await Assert.That(subject.IsRunning()).IsFalse()
            .Because(
                "An agent with no bound session has no run, so false is the TRUTH here, not a fallback that "
                + "hides a mistake. Ten call sites would otherwise each have re-derived this answer "
                + "(frame loop, abort gesture, four slash commands) and the derivations would have drifted.");
    }

    [Test]
    public async Task IsRunning_AfterInitialize_TracksTheRun()
    {
        Session session = NewSession();
        var store = new FakeSessionStore(session);
        var release = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subject = new DefaultAgent(store, new BlockingAgentLoop(release), new FakeEventBus(), NullLogger<DefaultAgent>.Instance);
        subject.Initialize(session, TestAgents.AllowAll());

        Task<Result> run = subject.PromptAsync("hello");

        await Assert.That(await WaitUntilAsync(() => subject.IsRunning())).IsTrue()
            .Because("A run parked in the agent loop must be visible to every consumer that asks.");

        release.TrySetResult(Result.Success());
        Result outcome = await run;
        await Assert.That(outcome.IsSuccess).IsTrue();
        await Assert.That(subject.IsRunning()).IsFalse()
            .Because("The probe must fall back to false again once the run settles.");
    }

    // ── Operations that cannot work unbound fail, and fail early ────────────

    [Test]
    public async Task PromptAsync_Text_BeforeInitialize_FailsAndNamesTheFix()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out FakeSessionStore store);

        Result result = await subject.PromptAsync("hello");

        await Assert.That(result.IsFailure).IsTrue()
            .Because("An unbound agent has no session to append to and no definition to drive, so the call cannot succeed.");
        await Assert.That(result.Error).Contains("not initialized")
            .Because("The failure text must name the condition, so the operator knows what to do instead of guessing.");
        await Assert.That(store.Appends).IsEqualTo(0)
            .Because(
                "The guard has to fire BEFORE persistence. This is the concrete argument against the "
                + "AgentState.Uninitialized sentinel the issue proposed: a fabricated SessionId would have "
                + "reached ISessionStore.AppendMessageAsync right here.");
    }

    [Test]
    public async Task PromptAsync_PreBuiltMessage_BeforeInitialize_FailsWithoutPersisting()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out FakeSessionStore store);

        Result result = await subject.PromptAsync(new UserMessage(
            Guid.NewGuid().ToString("N"), session.Id, DateTimeOffset.UtcNow, "hi", "user", "test-model"));

        await Assert.That(result.IsFailure).IsTrue()
            .Because("The UserMessage overload must refuse too — it is the overload the image path and the IPC path use.");
        await Assert.That(result.Error).Contains("not initialized");
        await Assert.That(store.Appends).IsEqualTo(0)
            .Because("No fabricated session id may reach the store.");
    }

    // ── Operations that are meaningful while unbound keep working ───────────

    [Test]
    public async Task WaitForIdleAsync_BeforeInitialize_CompletesInsteadOfHanging()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out _);

        Task idle = subject.WaitForIdleAsync();

        await Assert.That(idle.IsCompleted).IsTrue()
            .Because(
                "An unbound agent is already idle by definition. The old `State?.IsRunning != true` guard "
                + "covered this; the Maybe rewrite has to cover it too or WaitForIdleAsync hangs the caller "
                + "forever — the failure mode this guard exists to prevent.");
    }

    [Test]
    public async Task Subscribe_ThenPublish_BeforeInitialize_DeliversWithoutThrowing()
    {
        Session session = NewSession();
        FakeSessionStore store = new(session);
        var bus = new FakeEventBus();
        using var subject = new DefaultAgent(store, new FakeAgentLoop(), bus, NullLogger<DefaultAgent>.Instance);

        var received = new List<AgentEvent>();
        using IDisposable subscription = subject.Subscribe((evt, _) =>
        {
            received.Add(evt);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new AgentStartEvent(session.Id, []));

        await Assert.That(received.Count).IsEqualTo(1)
            .Because(
                "The bus subscription is installed in the CONSTRUCTOR, so this callback runs before any "
                + "Initialize. It used to read `State?.SessionId`; the Maybe rewrite must resolve the "
                + "unbound session id to Maybe.None rather than dereference a state that does not exist.");
    }

    [Test]
    public async Task Steer_BeforeInitialize_IsAcceptedAndSurvivesTheBind()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out _);

        // The steering queue is created once per agent lifetime and outlives the
        // bind, so a message handed over before Initialize is not lost.
        subject.Steer(new AssistantMessage(
            Guid.NewGuid().ToString("N"), session.Id, DateTimeOffset.UtcNow, [], StopReason.Stop, new Usage(0, 0), "test-model"));
        subject.Initialize(session, TestAgents.AllowAll());

        await Assert.That(subject.State.HasValue).IsTrue()
            .Because("Steering must not have wedged the agent in a half-initialized state.");
    }

    [Test]
    public async Task AbortSurface_BeforeInitialize_IsSafe()
    {
        Session session = NewSession();
        using DefaultAgent subject = NewAgent(session, out _);

        subject.RequestAbort();
        await subject.WaitForIdleAsync();

        // Reset refuses while the token is uncancelled; after an abort it must swap.
        subject.ResetAbortSource();

        await Assert.That(subject.AbortToken.IsCancellationRequested).IsFalse()
            .Because(
                "Abort + reset is reachable before Initialize (the REPL's Ctrl+C gesture is wired at "
                + "construction). It must round-trip cleanly rather than throw on the absent state.");
    }

    [Test]
    public async Task Dispose_BeforeInitialize_IsSafe()
    {
        Session session = NewSession();
        DefaultAgent subject = NewAgent(session, out _);

        subject.Dispose();

        await Assert.That(subject.State.HasNoValue).IsTrue()
            .Because("Dispose must not fabricate a state on its way out, and reading State afterwards must still answer.");
    }

    // ── Rebinding ───────────────────────────────────────────────────────────

    [Test]
    public async Task Initialize_Twice_ReplacesTheSnapshotRatherThanAccumulating()
    {
        Session first = NewSession();
        Session second = Session.Create("/tmp/harbor-agent-uninitialized-tests", "code", "test", "other-model");
        using DefaultAgent subject = NewAgent(first, out _);

        subject.Initialize(first, TestAgents.AllowAll());
        subject.Initialize(second, TestAgents.AllowAll(model: "other-model"));

        await Assert.That(subject.State.Value.SessionId).IsEqualTo(second.Id)
            .Because("Initialize is a rebind, not an accumulate — the Maybe projection must follow the latest bind.");
        await Assert.That(subject.State.Value.IsRunning).IsFalse()
            .Because("A rebind resets the run flag; AgentState.Idle is what Initialize publishes.");
    }
}
