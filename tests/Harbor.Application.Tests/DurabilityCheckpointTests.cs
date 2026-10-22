using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1765: the durability checkpoint, fail-closed. A tool body may change
///     the user's repository, and the record of the intent has to survive a
///     crash BEFORE the body runs. Otherwise the log says one thing and the
///     disk says another, and nobody can find out what changed.
/// </summary>
/// <remarks>
///     <para>
///         The defect this pins closed: the assistant message carrying a
///         <c>tool_call</c> is appended to the session log, and the append's
///         <see cref="Result" /> was LOGGED AND DROPPED. A store that failed
///         the write (disk full, read-only file system, ENOSPC) left the run
///         running: the tool executed, its effect landed, and the log had no
///         trace of the call that caused it.
///     </para>
///     <para>
///         The rule now: the checkpoint answers "does the session's state
///         survive a crash?", and a NO means the dependent work does not
///         start. Both directions are pinned below: the broken checkpoint
///         refuses the work and says why, and the intact one lets it run.
///     </para>
/// </remarks>
public class DurabilityCheckpointTests
{
    /// <summary>The reason a broken checkpoint reports, in the words the refusal carries.</summary>
    private const string BrokenCheckpoint = "disk full (simulated)";

    /// <summary>
    ///     The answer a context gives when its state is not durable: the
    ///     boundary the checkpoint was protecting, and the cause.
    /// </summary>
    /// <param name="reason">The boundary the checkpoint protected.</param>
    /// <returns>The failure the caller refuses the work on.</returns>
    private static Result NotDurable(string reason) =>
        Result.Failure(
            $"Session log durability checkpoint failed ({reason}): {BrokenCheckpoint}. "
            + "The session log does not survive a crash, so work that depends on it did not start.");

    private static Session NewSession() =>
        Session.Create("/tmp/harbor-1765-durability", "code", "test", "test-model");

    /// <summary>
    ///     The scripted provider: one turn that asks for a tool, one that
    ///     answers.
    /// </summary>
    private static ScriptedLlmClient ScriptedClient() => new(
    [
        new LlmEvent[]
        {
            TestEvents.ToolCallStart(),
            TestEvents.ToolCallDelta(argsDelta: """{"n":1}"""),
            TestEvents.StepFinish(finishReason: "tool_use", usage: new Usage(4, 2))
        },
        new LlmEvent[]
        {
            TestEvents.TextDelta(delta: "after tool"),
            TestEvents.StepFinish(1, usage: new Usage(1, 1))
        }
    ]);

    private static AgentLoop NewLoop(ScriptedLlmClient client, ITool tool, AgentDefinition agent) =>
        new(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(tool),
            new FakeAgentRegistry(agent),
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(new FakeAgentRegistry(agent), NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

    /// <summary>
    ///     A session context whose checkpoint answers what the test says it
    ///     answers, and remembers which boundaries it was asked about.
    /// </summary>
    private sealed class CheckpointSessionContext(
        Session session,
        Func<string, CancellationToken, Task<Result>>? checkpoint = null) : ISessionContext
    {
        private readonly List<AgentMessage> _messages = [];

        public Session Session { get; } = session;

        public IReadOnlyList<AgentMessage> Messages => _messages;

        public Channel<AgentMessage> SteeringQueue { get; } = Channel.CreateUnbounded<AgentMessage>();

        /// <summary>The boundaries the checkpoint was asked about, in order.</summary>
        public List<string> Reasons { get; } = [];

        public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task UpdateStatsAsync(Usage usage, Pricing pricing, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Result> CheckpointAsync(string reason, CancellationToken ct = default)
        {
            Reasons.Add(reason);
            return checkpoint is null
                ? Task.FromResult(Result.Success())
                : checkpoint(reason, ct);
        }
    }

    /// <summary>
    ///     A store whose Nth append fails, the shape of a disk that filled up
    ///     between the prompt and the tool call.
    /// </summary>
    private sealed class FailNthAppendStore(Session session, int failAtAppend) : ISessionStore
    {
        private readonly object _lock = new();
        private readonly List<AgentMessage> _messages = [];
        private int _appends;

        /// <summary>The records the store actually holds.</summary>
        public IReadOnlyList<AgentMessage> Messages
        {
            get
            {
                lock (_lock)
                {
                    return [.. _messages];
                }
            }
        }

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(session));

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(session));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
            => Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            int n = Interlocked.Increment(ref _appends);
            if (n == failAtAppend)
            {
                return Task.FromResult(Result.Failure(BrokenCheckpoint));
            }

            lock (_lock)
            {
                _messages.Add(message);
            }

            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([.. _messages]));
            }
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<int>("DeleteMessagesAfter is not supported by this test fake."));

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(SessionMetadata.Empty));

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    [Test]
    public async Task ToolBody_BrokenCheckpoint_ToolNeverRuns_AndTheRefusalSaysWhy()
    {
        AgentDefinition agent = TestAgents.AllowAll();
        var tool = new CountingTool();
        var client = ScriptedClient();

        // The checkpoint fails at the tool-body boundary. From then on it
        // fails everywhere, so the NEXT turn's checkpoint ends the run
        // instead of looping on the scripted provider.
        bool broken = false;
        var session = new CheckpointSessionContext(NewSession(), (reason, _) =>
        {
            if (string.Equals(reason, ToolDispatcher.ToolBodyCheckpointReason, StringComparison.Ordinal))
            {
                broken = true;
                return Task.FromResult(NotDurable(reason));
            }

            return Task.FromResult(broken ? NotDurable(reason) : Result.Success());
        });

        var loop = NewLoop(client, tool, agent);
        Result result = await loop.RunAsync(session, agent);

        // The body did not run.
        await Assert.That(tool.Executions).IsEqualTo(0);

        // The refusal says the tool did NOT run, and why.
        ToolResultMessage results = session.Messages.OfType<ToolResultMessage>().Single();
        ToolResultEntry entry = results.Results.Single();
        await Assert.That(entry).HasFailed();
        await Assert.That(entry.Output).Contains("was NOT executed");
        await Assert.That(entry.Output).Contains(BrokenCheckpoint);

        // The boundary that was protected is named in the refusal.
        await Assert.That(entry.Output).Contains(ToolDispatcher.ToolBodyCheckpointReason);

        // The run ends on the next checkpoint instead of asking the model again.
        await Assert.That(result).IsFailed();
        await Assert.That(result.Error).Contains("durability checkpoint");
    }

    [Test]
    public async Task ToolBody_IntactCheckpoint_TheToolRuns()
    {
        // The control: a checkpoint that passes changes nothing about the run.
        AgentDefinition agent = TestAgents.AllowAll();
        var tool = new CountingTool();
        var client = ScriptedClient();
        var session = new CheckpointSessionContext(NewSession());

        var loop = NewLoop(client, tool, agent);
        Result result = await loop.RunAsync(session, agent);

        await Assert.That(result).IsSuccessful();
        await Assert.That(tool.Executions).IsEqualTo(1);

        // Both boundaries were protected: the model request and the tool body.
        await Assert.That(session.Reasons).Contains(ToolDispatcher.ToolBodyCheckpointReason);
        await Assert.That(session.Reasons.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ModelRequest_BrokenCheckpoint_TheModelIsNeverCalled()
    {
        // The checkpoint BEFORE the model request fails: the adapter is never
        // dispatched, so no tokens are spent on an answer nobody can read back.
        AgentDefinition agent = TestAgents.AllowAll();
        var tool = new CountingTool();
        var client = ScriptedClient();
        var session = new CheckpointSessionContext(
            NewSession(),
            (reason, _) => Task.FromResult(NotDurable(reason)));

        var loop = NewLoop(client, tool, agent);
        Result result = await loop.RunAsync(session, agent);

        await Assert.That(client.Requests.Count).IsEqualTo(0);
        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(result).IsFailed();
        await Assert.That(result.Error).Contains("durability checkpoint");
        // The boundary the failed checkpoint protected is named in the reason
        // the user reads: the run stopped before the model request.
        await Assert.That(result.Error).Contains("before the model request");
        await Assert.That(result.Error).Contains(BrokenCheckpoint);
    }

    [Test]
    public async Task CancelledAfterTheCheckpoint_TheToolNeverRuns_AndTheResultSaysSo()
    {
        // A stop that lands BETWEEN the checkpoint and the dispatch. The call
        // is durably recorded, the body never ran, and the result says which
        // of the two happened, under a code of its own.
        AgentDefinition agent = TestAgents.AllowAll();
        var tool = new CountingTool();
        var client = ScriptedClient();
        using var cts = new CancellationTokenSource();

        var session = new CheckpointSessionContext(NewSession(), (reason, _) =>
        {
            if (string.Equals(reason, ToolDispatcher.ToolBodyCheckpointReason, StringComparison.Ordinal))
            {
                cts.Cancel();
            }

            return Task.FromResult(Result.Success());
        });

        var loop = NewLoop(client, tool, agent);
        Result result = await loop.RunAsync(session, agent, cts.Token);

        await Assert.That(tool.Executions).IsEqualTo(0);

        ToolResultMessage results = session.Messages.OfType<ToolResultMessage>().Single();
        ToolResultEntry entry = results.Results.Single();
        await Assert.That(entry).HasFailed();
        await Assert.That(entry.Output).Contains(ToolDispatcher.AbortedBeforeDispatchCode);

        // A cancelled run is not a successful run.
        await Assert.That(result).IsFailed();
    }

    [Test]
    public async Task FailedAppend_BrokenCheckpoint_TheToolNeverRuns()
    {
        // The end-to-end shape: the assistant message carrying the tool_call
        // is appended to the log, the append FAILS, and the tool body must
        // not run anyway. Before #1765 the append's Result was logged and
        // dropped, and the tool executed with no trace in the log.
        AgentDefinition agent = TestAgents.AllowAll();
        var tool = new CountingTool();
        var client = ScriptedClient();
        var session = NewSession();

        // Append #1 is the user's prompt (the agent writes it), #2 is the
        // assistant message that carries the tool call, the one that fails.
        var store = new FailNthAppendStore(session, failAtAppend: 2);
        var loop = NewLoop(client, tool, agent);
        var bus = new FakeEventBus();
        var runner = new DefaultAgent(store, loop, bus, NullLogger<DefaultAgent>.Instance);

        try
        {
            runner.Initialize(session, agent);
            Result result = await runner.PromptAsync("do the thing");

            // The body did not run, even though the model asked for it.
            await Assert.That(tool.Executions).IsEqualTo(0);

            // The refusal is on the record, and it names the cause.
            ToolResultMessage results = store.Messages.OfType<ToolResultMessage>().Single();
            ToolResultEntry entry = results.Results.Single();
            await Assert.That(entry).HasFailed();
            await Assert.That(entry.Output).Contains("was NOT executed");
            await Assert.That(entry.Output).Contains(BrokenCheckpoint);

            // The run ends with a reason the user can read.
            await Assert.That(result).IsFailed();
            await Assert.That(result.Error).Contains("durability checkpoint");
        }
        finally
        {
            runner.Dispose();
        }
    }

    [Test]
    public async Task HealthyStore_TheToolRuns_AndTheRunFinishes()
    {
        // The control for the end-to-end shape: the same run against a store
        // that keeps its promises does its work.
        AgentDefinition agent = TestAgents.AllowAll();
        var tool = new CountingTool();
        var client = ScriptedClient();
        var session = NewSession();

        var store = new FailNthAppendStore(session, failAtAppend: 0);
        var loop = NewLoop(client, tool, agent);
        var bus = new FakeEventBus();
        var runner = new DefaultAgent(store, loop, bus, NullLogger<DefaultAgent>.Instance);

        try
        {
            runner.Initialize(session, agent);
            Result result = await runner.PromptAsync("do the thing");

            await Assert.That(result).IsSuccessful();
            await Assert.That(tool.Executions).IsEqualTo(1);
        }
        finally
        {
            runner.Dispose();
        }
    }
}
