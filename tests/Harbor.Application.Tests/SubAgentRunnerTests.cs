using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Tests;

/// <summary>
///     Tests for <see cref="SubAgentRunner" /> — isolated-session spawn, final-output
///     extraction, failure surfacing, nesting guard, and the deferred forwarder.
/// </summary>
public class SubAgentRunnerTests
{
    private static Session NewSession() => Session.Create("/tmp/harbor-subtest", "code", "test", "test-model");

    private static AgentDefinition SubAgent(string name = "explore") => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);

    private static AgentDefinition MainAgent() => new(
        AgentName.Create("code"),
        "code",
        "code",
        "test-model",
        "test",
        PermissionRuleset.Empty);

    private static AssistantMessage Assistant(string text) => new(
        Guid.NewGuid().ToString("N"),
        "session-1", // store ignores ids for its single list; shape only
        DateTimeOffset.UtcNow,
        [new TextPart(text)],
        StopReason.Stop,
        new Usage(0, 0),
        "test-model");

    /// <summary>Loop fake that appends scripted replies into the context and captures calls.</summary>
    private sealed class ScriptedLoop : IAgentLoop
    {
        private readonly AgentMessage[] _replies;
        private readonly Result? _outcome;

        public ScriptedLoop(Result? outcome = null, params AgentMessage[] replies)
        {
            _outcome = outcome;
            _replies = replies;
        }

        public ISessionContext? LastContext { get; private set; }
        public AgentDefinition? LastAgent { get; private set; }
        public Func<Task>? MidRun { get; set; }

        public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            LastContext = session;
            LastAgent = agent;
            foreach (AgentMessage message in _replies)
                await session.AppendMessageAsync(message, ct).ConfigureAwait(false);

            if (MidRun is not null)
                await MidRun().ConfigureAwait(false);
            return _outcome ?? Result.Success();
        }
    }

    [Test]
    public async Task RunAsync_HappyPath_ReturnsFinalAssistantOutput()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new ScriptedLoop(replies: Assistant("found 3 TODOs in src/"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("find the todos", ParentSessionId: "parent-1"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.FinalOutput).IsEqualTo("found 3 TODOs in src/");
        await Assert.That(result.Value.AgentName).IsEqualTo("explore");
        await Assert.That(result.Value.NewMessages).IsEqualTo(2); // user prompt + assistant answer
        await Assert.That(store.Appends).IsEqualTo(2);
    }

    [Test]
    public async Task RunAsync_Isolation_SessionHasParentLinkageAndTitle()
    {
        var session = NewSession();
        var store = new FakeSessionStore(session);
        var loop = new ScriptedLoop(replies: Assistant("done"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("first line\nsecond line", "parent-9"));

        await Assert.That(result.IsSuccess).IsTrue();
        // The context handed to the loop carries the parent linkage + generated title.
        await Assert.That(loop.LastContext!.Session.ParentSessionId).IsEqualTo("parent-9");
        await Assert.That(loop.LastContext.Session.Title).StartsWith("task(explore): first line");
        await Assert.That(loop.LastContext!.Session.Title).DoesNotContain("\n");
    }

    [Test]
    public async Task RunAsync_PassesSubAgentDefinitionToLoop()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new ScriptedLoop(replies: Assistant("ok"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var definition = SubAgent();
        await runner.RunAsync(definition, new SubAgentRunRequest("go"));

        await Assert.That(loop.LastAgent).IsEqualTo(definition);
    }

    [Test]
    public async Task RunAsync_NonSubAgentDefinition_FailsWithoutTouchingStore()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new ScriptedLoop(replies: Assistant("never"));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(MainAgent(), new SubAgentRunRequest("nope"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not a sub-agent");
        await Assert.That(loop.LastContext).IsNull();
        await Assert.That(store.Appends).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_LoopFailure_SurfacesErrorWithSessionId()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new ScriptedLoop(outcome: Result.Failure("model exploded"), replies: []);
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("failed: model exploded");
        await Assert.That(result.Error).Contains(loop.LastContext!.Session.Id);
    }

    [Test]
    public async Task RunAsync_NoFinalText_FailsExplicitly()
    {
        var store = new FakeSessionStore(NewSession());
        var loop = new ScriptedLoop(); // success but no messages appended beyond the prompt
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("without producing a final assistant message");
    }

    [Test]
    public async Task RunAsync_LongOutput_TruncatedWithMarker()
    {
        // SubAgentRunner.MaxOutputChars is internal (not visible to this project);
        // the cap constant itself is asserted indirectly by the lengths below.
        const int maxChars = 32_000;
        var longText = new string('x', maxChars + 500);
        var store = new FakeSessionStore(NewSession());
        var loop = new ScriptedLoop(replies: Assistant(longText));
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.FinalOutput.Length).IsLessThan(longText.Length);
        await Assert.That(result.Value.FinalOutput).Contains("[truncated 500 chars]");
    }

    [Test]
    public async Task NestingGuard_InnerTaskCallRefused_DepthRestoredAfterRun()
    {
        var store = new FakeSessionStore(NewSession());

        // The outer sub-run's loop attempts a nested 'task' delegation mid-run —
        // exactly what an LLM-driven recursive chain would look like.
        SubAgentRunner? runner = null;
        bool? canSpawnInside = null;
        Result<SubAgentRunResult>? nestedOutcome = null;

        var outerLoop = new ScriptedLoop(replies: Assistant("outer"))
        {
            MidRun = async () =>
            {
                canSpawnInside = runner!.CanSpawn;
                nestedOutcome = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("nested"));
            }
        };
        runner = new SubAgentRunner(store, outerLoop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("outer task"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(canSpawnInside).IsFalse();
        await Assert.That(nestedOutcome!.Value.IsFailure).IsTrue();
        await Assert.That(nestedOutcome.Value.Error).Contains("Nesting limit reached");
        // Depth must be restored once the outer run exits its guard scope.
        await Assert.That(runner.CanSpawn).IsTrue();
    }

    [Test]
    public async Task CanSpawn_TrueAtTopLevel()
    {
        var store = new FakeSessionStore(NewSession());
        var runner = new SubAgentRunner(store, new ScriptedLoop(), NullLogger<SubAgentRunner>.Instance);

        await Assert.That(runner.CanSpawn).IsTrue();
    }

    [Test]
    public async Task DeferredRunner_Detached_CanSpawnFalseAndFailsHonestly()
    {
        var deferred = new DeferredSubAgentRunner();

        await Assert.That(deferred.CanSpawn).IsFalse();

        var result = await deferred.RunAsync(SubAgent(), new SubAgentRunRequest("go"));
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not initialized yet");
    }

    [Test]
    public async Task DeferredRunner_Attached_DelegatesToRealRunner()
    {
        var store = new FakeSessionStore(NewSession());
        var real = new SubAgentRunner(store, new ScriptedLoop(replies: Assistant("hi from real")), NullLogger<SubAgentRunner>.Instance);
        var deferred = new DeferredSubAgentRunner();

        deferred.Attach(real);

        await Assert.That(deferred.CanSpawn).IsTrue();
        var result = await deferred.RunAsync(SubAgent(), new SubAgentRunRequest("go"));
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.FinalOutput).IsEqualTo("hi from real");
    }
}
