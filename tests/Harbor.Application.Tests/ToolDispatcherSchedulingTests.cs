using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1766: the tool scheduler — a cap on concurrent parallel-safe calls,
///     and a per-call decision that makes an exclusive call a BARRIER instead
///     of a reason to run the whole batch serially.
/// </summary>
/// <remarks>
///     <para>
///         The batch is model-controlled, so its size is untrusted input. The
///         cap is the bound on that: fifty parallel reads meant fifty file
///         descriptors, fifty processes or fifty sockets opened at once. The
///         barrier is the ordering: a call that declares
///         <see cref="ExecutionMode.Sequential" /> owns the workspace for the
///         length of its call, so the parallel-safe group before it drains
///         first and the group after it starts only once it has finished.
///     </para>
///     <para>
///         The old behaviour — one sequential call making the WHOLE batch
///         serial — is what the barrier tests here exist to contradict: the
///         reads around the exclusive call must still overlap, and the
///         exclusive call must overlap nothing.
///     </para>
/// </remarks>
public class ToolDispatcherSchedulingTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    /// <summary>How long a probe call stays in flight — long enough to overlap.</summary>
    private const int CallDelayMs = 40;

    private static TestSessionContext NewSession() =>
        new(Session.Create("/tmp/harbor-1766-scheduler", "code", "test", "test-model"));

    private static ToolCallPart Call(string id, string tool) => new(id, tool, EmptyArgs);

    private static ToolDispatcher NewDispatcher(
        IToolRegistry registry,
        IPermissionService permissions,
        int? cap = null) =>
        new(
            registry,
            permissions,
            new FakeEventBus(),
            NullLogger<ToolDispatcher>.Instance,
            maxParallelToolCalls: cap);

    private static IPermissionService AllowAll() =>
        new PermissionService(
            new FakeAgentRegistry(TestAgents.AllowAll()),
            NullLogger<PermissionService>.Instance);

    private static IPermissionService DenyAll() =>
        new PermissionService(
            new FakeAgentRegistry(TestAgents.WithPermission(
                ruleset: new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Deny) }))),
            NullLogger<PermissionService>.Instance);

    // ── (a) the cap ────────────────────────────────────────────────────────

    /// <summary>
    ///     A batch of N parallel-safe calls with a cap below N must never have
    ///     more than the cap in flight. The old dispatcher started all of them
    ///     at once.
    /// </summary>
    [Test]
    public async Task ParallelBatch_NeverExceedsTheCap()
    {
        const int cap = 3;
        const int calls = 9;

        var log = new ScheduleLog();
        var probe = new ProbeTool("probe", ExecutionMode.Parallel, log, CallDelayMs);
        var dispatcher = NewDispatcher(new FakeToolRegistry(probe), AllowAll(), cap);

        var batch = new List<ToolCallPart>();
        for (int i = 0; i < calls; i++)
        {
            batch.Add(Call($"c{i}", "probe"));
        }

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            batch, NewSession(), AssistantMessage.Empty("s", "m"), TestAgents.AllowAll(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(calls);
        await Assert.That(log.MaxRunning).IsEqualTo(cap)
            .Because($"the cap is {cap} and the batch carries {calls} parallel-safe calls; "
                   + $"the observed peak concurrency was {log.MaxRunning}. A peak above the cap "
                   + "means the excess started instead of waiting for a slot; a peak below it "
                   + "means the pool serialised the batch instead of bounding it.");
    }

    /// <summary>
    ///     The cap is a BOUND, not a serialiser: a batch smaller than the cap
    ///     still runs concurrently, and a batch larger than it still finishes
    ///     with one result per call.
    /// </summary>
    [Test]
    public async Task ParallelBatch_UnderTheCap_RunsConcurrently()
    {
        var log = new ScheduleLog();
        var probe = new ProbeTool("probe", ExecutionMode.Parallel, log, CallDelayMs);
        var dispatcher = NewDispatcher(new FakeToolRegistry(probe), AllowAll(), cap: 4);

        var batch = new List<ToolCallPart>();
        for (int i = 0; i < 4; i++)
        {
            batch.Add(Call($"c{i}", "probe"));
        }

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            batch, NewSession(), AssistantMessage.Empty("s", "m"), TestAgents.AllowAll(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(4);
        await Assert.That(log.MaxRunning).IsEqualTo(4)
            .Because("four parallel-safe calls under a cap of four must all be in flight at once — "
                   + "a peak below the cap means the pool serialised the batch.");
    }

    /// <summary>
    ///     A cap of one serialises the batch — the documented floor, and the
    ///     shape a user asks for when a workspace must not be touched twice
    ///     at once.
    /// </summary>
    [Test]
    public async Task CapOfOne_SerialisesTheBatch()
    {
        var log = new ScheduleLog();

        // Three DISTINCT tools, so the timeline names each call: the peak of
        // one would also be true of a batch that ran them concurrently by
        // accident, and the ordering below is what says "one at a time".
        var registry = new FakeToolRegistry(
            new ProbeTool("probe0", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("probe1", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("probe2", ExecutionMode.Parallel, log, CallDelayMs));
        var dispatcher = NewDispatcher(registry, AllowAll(), cap: 1);

        var batch = new List<ToolCallPart>
        {
            Call("c0", "probe0"),
            Call("c1", "probe1"),
            Call("c2", "probe2"),
        };

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            batch, NewSession(), AssistantMessage.Empty("s", "m"), TestAgents.AllowAll(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(3);
        await Assert.That(log.MaxRunning).IsEqualTo(1)
            .Because("a cap of one means one call at a time; the observed peak was "
                   + $"{log.MaxRunning}.");
        await Assert.That(log.IndexOf("end:probe0")).IsLessThan(log.IndexOf("start:probe1"))
            .Because("with a cap of one the calls run in sequence, not side by side.");
        await Assert.That(log.IndexOf("end:probe1")).IsLessThan(log.IndexOf("start:probe2"))
            .Because("with a cap of one the calls run in sequence, not side by side.");
    }

    // ── (b) the barrier ───────────────────────────────────────────────────

    /// <summary>
    ///     An exclusive call in the middle of a batch is a barrier: the
    ///     parallel-safe group before it drains first, it runs alone, and the
    ///     group after it starts only once it has finished.
    /// </summary>
    [Test]
    public async Task ExclusiveCall_ActsAsBarrier_NothingOverlapsIt()
    {
        var log = new ScheduleLog();
        var registry = new FakeToolRegistry(
            new ProbeTool("readA", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("readB", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("write", ExecutionMode.Sequential, log, CallDelayMs),
            new ProbeTool("readC", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("readD", ExecutionMode.Parallel, log, CallDelayMs));

        var dispatcher = NewDispatcher(registry, AllowAll(), cap: 4);

        var batch = new List<ToolCallPart>
        {
            Call("c0", "readA"),
            Call("c1", "readB"),
            Call("c2", "write"),
            Call("c3", "readC"),
            Call("c4", "readD"),
        };

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            batch, NewSession(), AssistantMessage.Empty("s", "m"), TestAgents.AllowAll(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(5);
        await Assert.That(log.ExclusiveOverlap).IsEqualTo(0)
            .Because("an exclusive call must run ALONE: it started while another call was still "
                   + $"running {log.ExclusiveOverlap} time(s).");

        int writeStart = log.IndexOf("start:write");
        int writeEnd = log.IndexOf("end:write");
        int readAEnd = log.IndexOf("end:readA");
        int readBEnd = log.IndexOf("end:readB");
        int readCStart = log.IndexOf("start:readC");
        int readDStart = log.IndexOf("start:readD");

        await Assert.That(writeStart).IsGreaterThanOrEqualTo(0)
            .Because("the exclusive call must have run at all — without it the ordering "
                   + "assertions below would compare against -1 and pass for the wrong reason.");
        await Assert.That(writeEnd).IsGreaterThanOrEqualTo(0);
        await Assert.That(readAEnd).IsGreaterThanOrEqualTo(0);
        await Assert.That(readBEnd).IsGreaterThanOrEqualTo(0);
        await Assert.That(readCStart).IsGreaterThanOrEqualTo(0);
        await Assert.That(readDStart).IsGreaterThanOrEqualTo(0);

        await Assert.That(writeStart).IsGreaterThan(readAEnd)
            .Because("the group before the barrier must drain first: readA must finish before the "
                   + "exclusive call starts.");
        await Assert.That(writeStart).IsGreaterThan(readBEnd)
            .Because("the group before the barrier must drain first: readB must finish before the "
                   + "exclusive call starts.");
        await Assert.That(writeEnd).IsLessThan(readCStart)
            .Because("the group after the barrier must wait: readC must start only after the "
                   + "exclusive call has finished.");
        await Assert.That(writeEnd).IsLessThan(readDStart)
            .Because("the group after the barrier must wait: readD must start only after the "
                   + "exclusive call has finished.");
    }

    /// <summary>
    ///     The barrier is what the old all-or-nothing rule cost: the reads
    ///     AROUND the exclusive call must still overlap each other. Under the
    ///     old rule one sequential call made the whole batch serial.
    /// </summary>
    [Test]
    public async Task ExclusiveCall_DoesNotSerialiseTheReadsAroundIt()
    {
        var log = new ScheduleLog();
        var registry = new FakeToolRegistry(
            new ProbeTool("readA", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("readB", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("write", ExecutionMode.Sequential, log, CallDelayMs));

        var dispatcher = NewDispatcher(registry, AllowAll(), cap: 4);

        var batch = new List<ToolCallPart>
        {
            Call("c0", "readA"),
            Call("c1", "readB"),
            Call("c2", "write"),
        };

        await dispatcher.ExecuteAsync(
            batch, NewSession(), AssistantMessage.Empty("s", "m"), TestAgents.AllowAll(), CancellationToken.None);

        await Assert.That(log.MaxRunning).IsEqualTo(2)
            .Because("the two reads before the barrier must overlap — a peak of one means the "
                   + "sequential call still poisons the whole batch, which is the #1766 defect.");
    }

    // ── (c) the semantics the scheduler must not break ────────────────────

    /// <summary>
    ///     Results come back in the MODEL's order, whatever the schedule did.
    /// </summary>
    [Test]
    public async Task Results_KeepTheModelOrder()
    {
        var log = new ScheduleLog();
        var registry = new FakeToolRegistry(
            new ProbeTool("readA", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("readB", ExecutionMode.Parallel, log, CallDelayMs),
            new ProbeTool("write", ExecutionMode.Sequential, log, CallDelayMs),
            new ProbeTool("readC", ExecutionMode.Parallel, log, CallDelayMs));

        var dispatcher = NewDispatcher(registry, AllowAll(), cap: 4);

        var batch = new List<ToolCallPart>
        {
            Call("c0", "readA"),
            Call("c1", "readB"),
            Call("c2", "write"),
            Call("c3", "readC"),
        };

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            batch, NewSession(), AssistantMessage.Empty("s", "m"), TestAgents.AllowAll(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(4);
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(result.Results[i].ToolCallId).IsEqualTo($"c{i}")
                .Because($"result {i} must answer the model's call {i}, not the order the "
                       + "schedule happened to finish in.");
        }
    }

    /// <summary>
    ///     A failing tool is an ERROR ENTRY, not a thrown exception, and the
    ///     rest of the batch still runs.
    /// </summary>
    [Test]
    public async Task ToolError_BecomesAnErrorEntry_AndTheBatchContinues()
    {
        var log = new ScheduleLog();
        var registry = new FakeToolRegistry(
            new ProbeTool("boom", ExecutionMode.Parallel, log, 0, throwOnExecute: true),
            new ProbeTool("fine", ExecutionMode.Parallel, log, 0));

        var dispatcher = NewDispatcher(registry, AllowAll(), cap: 2);

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            [Call("c0", "boom"), Call("c1", "fine")],
            NewSession(),
            AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(),
            CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(2);
        await Assert.That(result.Results[0].IsError).IsTrue();
        await Assert.That(result.Results[0].Output).Contains("probe failure");
        await Assert.That(result.Results[1].IsError).IsFalse()
            .Because("a failing call must not take the rest of the batch down with it.");
    }

    /// <summary>
    ///     The permission gate is NOT bypassed by the scheduler: a denied call
    ///     fails closed and the tool never runs.
    /// </summary>
    [Test]
    public async Task PermissionDenied_FailsClosed_AndTheToolNeverRuns()
    {
        var log = new ScheduleLog();
        var probe = new ProbeTool("probe", ExecutionMode.Parallel, log, 0);
        var dispatcher = NewDispatcher(new FakeToolRegistry(probe), DenyAll(), cap: 2);

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            [Call("c0", "probe")],
            NewSession(),
            AssistantMessage.Empty("s", "m"),
            TestAgents.WithPermission(
                ruleset: new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Deny) })),
            CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(1);
        await Assert.That(result.Results[0].IsError).IsTrue();
        await Assert.That(result.Results[0].Output).Contains("Permission denied");
        await Assert.That(log.Entered).IsEqualTo(0)
            .Because("a denied call must never reach the tool — the scheduler must not become a "
                   + "way around the permission gate.");
    }

    // ── the policy surface ────────────────────────────────────────────────

    /// <summary>
    ///     The cap below the floor is raised, not thrown at: the dispatcher is
    ///     also constructed directly (tests, the agent loop's no-DI fallback),
    ///     where no config validation ran.
    /// </summary>
    [Test]
    public async Task CapBelowTheFloor_IsClamped()
    {
        await Assert.That(ToolBatchScheduler.NormalizeCap(0)).IsEqualTo(1);
        await Assert.That(ToolBatchScheduler.NormalizeCap(-5)).IsEqualTo(1);
        await Assert.That(ToolBatchScheduler.NormalizeCap(7)).IsEqualTo(7);

        var log = new ScheduleLog();
        var probe = new ProbeTool("probe", ExecutionMode.Parallel, log, CallDelayMs);
        var dispatcher = NewDispatcher(new FakeToolRegistry(probe), AllowAll(), cap: 0);

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            [Call("c0", "probe"), Call("c1", "probe")],
            NewSession(),
            AssistantMessage.Empty("s", "m"),
            TestAgents.AllowAll(),
            CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(2);
        await Assert.That(log.MaxRunning).IsEqualTo(1)
            .Because("a cap of zero must serialise the batch, not throw at dispatch time.");
    }

    /// <summary>
    ///     The classification is the tool's own declaration: a call that
    ///     declares <see cref="ExecutionMode.Sequential" /> is a barrier, and
    ///     a parallel-safe one is not. An unresolvable call is not exclusive —
    ///     it produces its own error entry and touches nothing.
    /// </summary>
    [Test]
    public async Task Classification_ComesFromTheToolDeclaration()
    {
        var sequential = new ProbeTool("seq", ExecutionMode.Sequential, new ScheduleLog(), 0);
        var parallel = new ProbeTool("par", ExecutionMode.Parallel, new ScheduleLog(), 0);

        await Assert.That(ToolBatchScheduler.IsExclusive(Result.Success<ITool>(sequential))).IsTrue();
        await Assert.That(ToolBatchScheduler.IsExclusive(Result.Success<ITool>(parallel))).IsFalse();
        await Assert.That(ToolBatchScheduler.IsExclusive(Result.Failure<ITool>("unknown"))).IsFalse();
    }

    /// <summary>
    ///     The group boundary: the first exclusive call after the start, or the
    ///     end of the batch when there is none.
    /// </summary>
    [Test]
    public async Task ParallelGroup_EndsAtTheFirstExclusiveCall()
    {
        var log = new ScheduleLog();
        Result<ITool>[] resolved =
        [
            Result.Success<ITool>(new ProbeTool("par", ExecutionMode.Parallel, log, 0)),
            Result.Success<ITool>(new ProbeTool("par", ExecutionMode.Parallel, log, 0)),
            Result.Success<ITool>(new ProbeTool("seq", ExecutionMode.Sequential, log, 0)),
            Result.Success<ITool>(new ProbeTool("par", ExecutionMode.Parallel, log, 0)),
        ];

        await Assert.That(ToolBatchScheduler.EndOfParallelGroup(resolved, 0)).IsEqualTo(2);
        await Assert.That(ToolBatchScheduler.EndOfParallelGroup(resolved, 2)).IsEqualTo(2);
        await Assert.That(ToolBatchScheduler.EndOfParallelGroup(resolved, 3)).IsEqualTo(4);
    }

    // ── the harness ────────────────────────────────────────────────────────

    /// <summary>
    ///     The record of what ran when: the ordered timeline, the peak
    ///     concurrency, and the times an exclusive call started while
    ///     something else was still running.
    /// </summary>
    private sealed class ScheduleLog
    {
        private readonly object _lock = new();
        private readonly List<string> _timeline = [];
        private int _running;
        private int _maxRunning;
        private int _exclusiveOverlap;
        private int _entered;

        /// <summary>How many calls reached the tool at all.</summary>
        public int Entered => Volatile.Read(ref _entered);

        /// <summary>The peak number of calls in flight at once.</summary>
        public int MaxRunning
        {
            get
            {
                lock (_lock)
                {
                    return _maxRunning;
                }
            }
        }

        /// <summary>How many times an exclusive call started alongside another call.</summary>
        public int ExclusiveOverlap
        {
            get
            {
                lock (_lock)
                {
                    return _exclusiveOverlap;
                }
            }
        }

        /// <summary>The ordered record of starts and ends.</summary>
        public IReadOnlyList<string> Timeline
        {
            get
            {
                lock (_lock)
                {
                    return [.. _timeline];
                }
            }
        }

        /// <summary>Where the marker sits in the timeline, or -1 when absent.</summary>
        public int IndexOf(string marker)
        {
            lock (_lock)
            {
                return _timeline.IndexOf(marker);
            }
        }

        public void Enter(string name, bool exclusive)
        {
            Interlocked.Increment(ref _entered);
            lock (_lock)
            {
                if (exclusive && _running > 0)
                {
                    _exclusiveOverlap++;
                }

                _running++;
                if (_running > _maxRunning)
                {
                    _maxRunning = _running;
                }

                _timeline.Add($"start:{name}");
            }
        }

        public void Exit(string name)
        {
            lock (_lock)
            {
                _running--;
                _timeline.Add($"end:{name}");
            }
        }
    }

    /// <summary>
    ///     A tool that records when it runs and how long it stays in flight.
    /// </summary>
    private sealed class ProbeTool(
        string name,
        ExecutionMode mode,
        ScheduleLog log,
        int delayMs,
        bool throwOnExecute = false) : ITool
    {
        public ToolName Name => ToolName.Create(name);

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => name;

        public string Description => "Scheduling probe.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public ExecutionMode ExecutionMode => mode;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            log.Enter(name, mode == ExecutionMode.Sequential);
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                if (throwOnExecute)
                {
                    throw new InvalidOperationException("probe failure");
                }

                return ToolResult.Success($"{name}-ok");
            }
            finally
            {
                log.Exit(name);
            }
        }
    }
}
