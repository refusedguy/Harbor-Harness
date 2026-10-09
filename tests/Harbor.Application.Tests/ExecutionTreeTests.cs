using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #407 (#41 S7/B5.1), narrow slice: children of a run must not outlive it.
///     <para>
///         (a) Shared cancellation — cancelling a parent run reaches detached
///         children (background tasks) through a registry-owned CTS, not only
///         through the launch token they captured. The launch token can be
///         orphaned (a per-call timeout CTS is disposed when the launching tool
///         call returns; a finished run's CTS is disposed by the host), and an
///         orphaned child is unstoppable today: <see cref="IBackgroundTaskRegistry" />
///         has no cancel seam at all.
///     </para>
///     <para>
///         (b) Attribution under cancel — a child's token spend still debits the
///         parent stats/message when the cancel lands mid-run. Post-run
///         bookkeeping in <see cref="SubAgentRunner" /> rides the run token, so
///         a fired token throws out of the debit path and the spend is lost.
///     </para>
///     <para>
///         RED status: <see cref="ParentCancel_StopsBackgroundChildren" /> calls
///         <c>CancelSession</c>, which does not exist yet (build break, the
///         #1033-sanctioned red shape); <see cref="AgentLoopCancel_CancelsBackgroundChildren" />
///         compiles but the loop never cancels the registry, so the spy stays
///         empty; the two <see cref="SubAgentRunner" /> tests throw
///         <see cref="OperationCanceledException" /> out of the debit path
///         instead of landing the debit.
///     </para>
/// </summary>
public class ExecutionTreeTests
{
    private static AgentDefinition SubAgent(string name = "explore") => new(
        AgentName.Create(name),
        name,
        name,
        "test-model",
        "test",
        PermissionRuleset.Empty,
        20,
        IsSubAgent: true);

    private static AgentDefinition LoopAgent() => new(
        AgentName.Create("code"),
        "Code",
        "execution-tree harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: 20);

    private static AssistantMessage AssistantWithUsage(string text, Usage usage) => new(
        Guid.NewGuid().ToString("N"),
        "child",
        DateTimeOffset.UtcNow,
        [new TextPart(text)],
        StopReason.Stop,
        usage,
        "test-model");

    private static TestSessionContext NewLoopSession() =>
        new(Session.Create(Harbor.TestKit.TestTempDirs.NewDirectory("harbor-407-loop"), "code", "test", "test-model"));

    private static Session NewParent() =>
        Session.Create(Harbor.TestKit.TestTempDirs.NewDirectory("harbor-407-parent"), "code", "test", "test-model");

    /// <summary>Loop fake with a mid-run hook, so the test can fire the cancel exactly mid-flight.</summary>
    private sealed class CancellingLoop(Result? outcome, params AgentMessage[] replies) : IAgentLoop
    {
        public Func<Task>? MidRun { get; set; }

        public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default)
        {
            foreach (AgentMessage message in replies)
                await session.AppendMessageAsync(message, ct).ConfigureAwait(false);
            if (MidRun is not null)
                await MidRun().ConfigureAwait(false);
            return outcome ?? Result.Success();
        }
    }

    /// <summary>
    ///     Multi-session store fake that observes the token on every call, like a
    ///     real store: once the run token fires, any further gated write throws
    ///     instead of landing. Post-run bookkeeping must therefore NOT ride it.
    /// </summary>
    private sealed class CancelAwareCostStore : ISessionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<AgentMessage>> _messages = new();
        private readonly Dictionary<string, Session> _sessions = new();
        private readonly Dictionary<string, SessionMetadata> _stats = new();

        public List<Session> CreatedSessions { get; } = [];
        public List<Session> UpdatedSessions { get; } = [];
        public List<(string SessionId, SessionMetadata Metadata)> UpdatedStats { get; } = [];

        public void Seed(Session session, params AgentMessage[] messages)
        {
            lock (_lock)
            {
                _sessions[session.Id] = session;
                _messages[session.Id] = [.. messages];
                _stats[session.Id] = SessionMetadata.Empty;
            }
        }

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var child = Session.Create(directory, agentName, providerId, modelId);
            lock (_lock)
            {
                _sessions[child.Id] = child;
                _messages[child.Id] = [];
                _stats[child.Id] = SessionMetadata.Empty;
                CreatedSessions.Add(child);
            }

            return Task.FromResult(Result.Success(child));
        }

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                return Task.FromResult(_sessions.TryGetValue(sessionId, out var session)
                    ? Result.Success(session)
                    : Result.Failure<Session>($"Session '{sessionId}' was not found."));
            }
        }

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                return Task.FromResult(Result.Success<IReadOnlyList<Session>>([.. _sessions.Values]));
            }
        }

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_messages.TryGetValue(sessionId, out var list))
                    return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
                list.Add(message);
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_messages.TryGetValue(sessionId, out var list))
                    return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Id == message.Id)
                    {
                        list[i] = message;
                        break;
                    }
                }

                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                return Task.FromResult(_messages.TryGetValue(sessionId, out var list)
                    ? Result.Success<IReadOnlyList<AgentMessage>>([.. list])
                    : Result.Failure<IReadOnlyList<AgentMessage>>($"Session '{sessionId}' was not found."));
            }
        }

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _sessions[session.Id] = session;
                UpdatedSessions.Add(session);
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                return Task.FromResult(_stats.TryGetValue(sessionId, out var metadata)
                    ? Result.Success(metadata)
                    : Result.Failure<SessionMetadata>($"Session '{sessionId}' was not found."));
            }
        }

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_stats.ContainsKey(sessionId))
                    return Task.FromResult(Result.Failure($"Session '{sessionId}' was not found."));
                _stats[sessionId] = metadata;
                UpdatedStats.Add((sessionId, metadata));
                return Task.FromResult(Result.Success());
            }
        }

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<int>("DeleteMessagesAfter is not supported by this test fake."));
    }

    /// <summary>
    ///     Provider that never answers: the only exit is the run token firing,
    ///     which surfaces as <see cref="StopReason.Aborted" /> and ends the run
    ///     on the loop's cancel path. Deterministic — no timing involved.
    /// </summary>
    private sealed class HangingLlmClient : ILlmClient
    {
        public ProviderId ProviderId => ProviderId.Create("test");

        public async IAsyncEnumerable<LlmEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { ScriptedLlmClient.TestModel }));
    }

    /// <summary>Spy registry recording which sessions the loop cancels. Extra method pre-fix, interface member post-fix.</summary>
    private sealed class RecordingBackgroundRegistry : IBackgroundTaskRegistry
    {
        public List<string> CancelledSessions { get; } = [];

        public int MaxBackgroundTasks => 8;

        public int PendingCount => 0;

        public Result<string> Start(
            string agentName,
            string sessionId,
            Func<CancellationToken, Task<Result<SubAgentRunResult>>> run,
            CancellationToken ct) =>
            Result.Failure<string>("RecordingBackgroundRegistry never starts runs.");

        public IReadOnlyList<BackgroundTaskCompletion> DrainCompleted(string sessionId) => [];

        public int CancelSession(string sessionId)
        {
            CancelledSessions.Add(sessionId);
            return 0;
        }
    }

    private static AgentLoop Loop(ILlmClient client, FakeEventBus bus, AgentDefinition agent, IBackgroundTaskRegistry? backgroundTasks = null)
    {
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            bus,
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            backgroundTasks: backgroundTasks);
    }

    [Test]
    public async Task ParentCancel_StopsBackgroundChildren()
    {
        var registry = new BackgroundTaskRegistry();
        int entered = 0;
        int observedCancel = 0;
        Func<CancellationToken, Task<Result<SubAgentRunResult>>> child = async ct =>
        {
            Interlocked.Increment(ref entered);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref observedCancel);
                throw;
            }

            return Result.Success(new SubAgentRunResult("child", "explore", "unreachable", 0));
        };

        // Orphaned launch tokens: nothing but the registry itself can stop
        // these runs — the captured token never fires.
        var first = registry.Start("explore", "parent-1", child, CancellationToken.None);
        var second = registry.Start("explore", "parent-1", child, CancellationToken.None);
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(second.IsSuccess).IsTrue();

        DateTimeOffset enterDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref entered) < 2 && DateTimeOffset.UtcNow < enterDeadline)
            await Task.Delay(20).ConfigureAwait(false);
        await Assert.That(Volatile.Read(ref entered)).IsEqualTo(2);

        int cancelled = registry.CancelSession("parent-1");
        await Assert.That(cancelled).IsEqualTo(2);

        IReadOnlyList<BackgroundTaskCompletion> done = [];
        DateTimeOffset drainDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (done.Count < 2 && DateTimeOffset.UtcNow < drainDeadline)
        {
            done = registry.DrainCompleted("parent-1");
            if (done.Count < 2)
                await Task.Delay(20).ConfigureAwait(false);
        }

        await Assert.That(done.Count).IsEqualTo(2);
        for (int i = 0; i < done.Count; i++)
            await Assert.That(done[i].Result.IsFailure).IsTrue();
        await Assert.That(Volatile.Read(ref observedCancel)).IsEqualTo(2);
    }

    [Test]
    public async Task AgentLoopCancel_CancelsBackgroundChildren()
    {
        var bus = new FakeEventBus();
        var spy = new RecordingBackgroundRegistry();
        var agent = LoopAgent();
        var loop = Loop(new HangingLlmClient(), bus, agent, spy);
        var session = NewLoopSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await loop.RunAsync(session, agent, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(spy.CancelledSessions.Count).IsEqualTo(1);
        await Assert.That(spy.CancelledSessions[0]).IsEqualTo(session.Session.Id);
        AgentEndEvent? end = null;
        foreach (AgentEvent evt in bus.Events)
        {
            if (evt is AgentEndEvent agentEnd)
                end = agentEnd;
        }

        await Assert.That(end).IsNotNull();
        await Assert.That(end!.Cancelled).IsTrue();
        await Assert.That(end.Limit).IsNull();
    }

    [Test]
    public async Task CancelledChildUsage_StillDebitsParentStats()
    {
        var parent = NewParent();
        var store = new CancelAwareCostStore();
        store.Seed(parent);
        using var cts = new CancellationTokenSource();
        var loop = new CancellingLoop(
            null,
            AssistantWithUsage("done", new Usage(100, 50)))
        {
            MidRun = () =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            }
        };
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go", ParentSessionId: parent.Id), cts.Token);

        await Assert.That(result.IsSuccess).IsTrue();
        var stats = await store.GetStatsAsync(parent.Id);
        await Assert.That(stats.IsSuccess).IsTrue();
        await Assert.That(stats.Value.TokensInput).IsEqualTo(100);
        await Assert.That(stats.Value.TokensOutput).IsEqualTo(50);
    }

    [Test]
    public async Task CancelledChild_StampsAbortedNotError()
    {
        var parent = NewParent();
        var store = new CancelAwareCostStore();
        store.Seed(parent);
        using var cts = new CancellationTokenSource();
        var loop = new CancellingLoop(
            Result.Failure("Agent run was cancelled."),
            AssistantWithUsage("partial", new Usage(40, 10)))
        {
            MidRun = () =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            }
        };
        var runner = new SubAgentRunner(store, loop, NullLogger<SubAgentRunner>.Instance);

        var result = await runner.RunAsync(SubAgent(), new SubAgentRunRequest("go", ParentSessionId: parent.Id), cts.Token);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("was cancelled");
        await Assert.That(store.CreatedSessions.Count).IsEqualTo(1);
        string childId = store.CreatedSessions[0].Id;
        await Assert.That(result.Error).Contains($"[resume-session:{childId}]");
        await Assert.That(store.UpdatedSessions.Count).IsNotEqualTo(0);
        await Assert.That(store.UpdatedSessions[^1].Status).IsEqualTo(SessionStatus.Aborted);
        var stats = await store.GetStatsAsync(parent.Id);
        await Assert.That(stats.Value.TokensInput).IsEqualTo(40);
        await Assert.That(stats.Value.TokensOutput).IsEqualTo(10);
    }
}
