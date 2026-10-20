using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakePermissionServiceBase = Harbor.TestKit.Generated.FakePermissionService; // #1769: generated double

using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1529 (#97 slice, per-tool-call): <c>ToolDispatcher.CreateToolContext</c>
///     wired the two per-call callbacks as async lambdas, each compiling to a
///     closure display-class (progress captured toolCall/eventBus/logger,
///     Ask captured toolCall/permissions/logger) — 2 display-classes per tool
///     call on the hot path. Both now route through one small sealed state
///     object per call (<c>ToolCallGates</c>) whose bodies are static and take
///     that record as an explicit state parameter, so zero display-class
///     allocations remain per call; behaviour is byte-identical (the callback
///     pins below, plus <c>ToolDispatcherAskFailClosedTests</c> for the Ask
///     fail-closed half).
///     <para>
///         THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
///         to be <c>t(2N) &lt;= 3*t(N)</c> and <c>alloc(2N) &lt;= 3*alloc(N)</c>
///         over 100 against 200 tool calls, decided by
///         <see cref="Harbor.TestKit.TestScalingGate" /> (#1652) — and it
///         reddened on 7 of 151 <c>core</c> runs (4.6 %), at ratios of
///         3.19–4.69 against a 3.15 limit, on trees nobody had touched.
///     </para>
///     <para>
///         <b>WHY THE RATIO WAS A RATIO OF THE HARNESS, NOT OF THE CODE.</b>
///         The measured receipt is run 39423151864: the small leg read
///         47.139 ms / 135 563 152 B and the large leg 221.250 ms /
///         269 391 952 B — time x4.69 with allocations x1.99. Linear memory,
///         super-linear wall clock. The cause is in the HARNESS: the fake event
///         bus keeps every published event forever, so the live set grows with
///         the number of dispatches, and the GC's marking cost per dispatch
///         grows with it. The 2N leg therefore paid for a bigger pile of
///         retained events, and the ratio measured the retention, not the
///         dispatcher. The number of dispatches per leg came from the leg
///         SCALER, which raises it until a leg lasts ~100 ms — so the faster
///         the runner, the fewer dispatches, the smaller the retained pile and
///         the lower the ratio. That is why the same tree read x2.03 on one
///         run and x4.69 on another: the gate's verdict moved with the
///         runner's speed, which is exactly what a merge gate must not do.
///     </para>
///     <para>
///         The gate is now:
///         <code>
///         bytes per tool call   &lt;= 4 000    (machine-independent, HARD)
///         probe units per call   &lt;= 60       (machine-scaled time, SOFT)
///         </code>
///         measured over a FIXED amount of work (50 000 tool calls, in batches
///         of 100) on the SAME runner in the SAME run, with the harness's
///         retained event pile cleared between dispatches so the live set is
///         BOUNDED and the measurement is of the dispatch path. The allocation
///         branch is machine-independent and is asserted hard; the time branch
///         is in PROBE UNITS (the cost of one call divided by the cost of one
///         unit of the fixed calibration probe measured on the same runner in
///         the same run, so the runner's speed cancels) and is asserted only
///         when the rounds reproduce each other. The report prints on every
///         run, pass or fail. Linux-only (GC accounting varies by OS).
///     </para>
///     <para>
///         This is a second gate next to
///         <c>ToolDispatcherResolveOnceTests.ExecuteAsync_CostPerToolCall_StaysWithinBudget</c>
///         (label <c>dispatcher-dispatch-cost</c>) on purpose: that one
///         dispatches a no-op tool, so it never enters the callback this
///         slice rebuilt. The tool here reports progress twice per call, so
///         every measured call goes through the rebuilt progress callback.
///     </para>
/// </summary>
[NotInParallel]
public class ToolDispatcherToolContextScalingTests
{
    /// <summary>Tool calls in one dispatch — the batch the gate measures.</summary>
    private const int ToolCalls = 100;

    /// <summary>
    ///     Tool calls per measured leg — the FIXED amount of work. 50 000 calls
    ///     is 500 dispatches, about 40 ms of real work on the CI runner: long
    ///     enough for the round-to-round spread to mean something, short enough
    ///     that the leg does not dominate the shard.
    /// </summary>
    private const int ToolCallsPerLeg = 50_000;

    /// <summary>
    ///     Allocation budget for ONE tool call, in bytes. Machine-independent,
    ///     so it is compared as measured, with no band.
    ///     <para>
    ///         Pinned from the CI measurement of the ratio gate this replaced
    ///         (run 39423151864): 135 563 152 B over 569 dispatches of 100
    ///         calls = 2 382 B per tool call. The budget leaves ~1.68x
    ///         headroom for the process-wide counter's share of the other
    ///         threads' allocations and for the harness's own bookkeeping. The
    ///         report line prints the measured value on every run, so the
    ///         constant can be tightened against what the runner does.
    ///     </para>
    /// </summary>
    private const long MaxBytesPerToolCall = 4_000;

    /// <summary>
    ///     Time budget for ONE tool call, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT:
    ///     the dispatch path awaits, so a thread hop lands inside the window
    ///     and no CPU yardstick scales a descheduled thread. Pinned from the
    ///     same run: 47.139 ms over 56 900 calls = 0.828 us per call against a
    ///     probe that costs 0.000380 ms/unit, i.e. ~2.2 probe units per call.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerToolCall = 60.0;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{}""");

    private sealed class AllowPermissions : FakePermissionServiceBase
    {
        public AllowPermissions()
        {
            CheckAsyncResult = Result.Success(new PermissionResponse(PermissionAction.Allow, false));
            AskUserAsyncResult = Result.Success(new PermissionResponse(PermissionAction.Deny, false));
            GetRulesetValue = PermissionRuleset.Empty;
            SaveAsyncResult = Result.Success();
        }
    }

    private sealed class ProgressTool(string name) : ITool
    {
        public ToolName Name => ToolName.Create(name);
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string DisplayName => name;
        public string Description => "Tool-context harness tool.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public async Task<ToolResult> ExecuteAsync(
            JsonElement args, ToolContext context, CancellationToken ct = default)
        {
            await context.ReportProgress(new ToolProgressUpdate("working"), ct).ConfigureAwait(false);
            await context
                .ReportProgress(new ToolProgressUpdate("half", 50, "partial-preview"), ct)
                .ConfigureAwait(false);
            return ToolResult.Success("ok");
        }
    }

    private sealed class CountingRegistry(params ITool[] tools) : IToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools =
            tools.ToDictionary(t => t.Name.Value, StringComparer.Ordinal);

        public IReadOnlyList<ToolDescriptor> GetAllTools()
        {
            var list = new List<ToolDescriptor>(_tools.Values.Count);
            foreach (ITool t in _tools.Values)
            {
                list.Add(new ToolDescriptor(
                    t.Name, t.DisplayName, t.Description, t.ParameterSchema,
                    t.ExecutionMode, t.PromptSnippet, t.PromptGuidelines));
            }

            return list;
        }

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) =>
            GetAllTools();

        public IReadOnlyList<IArgSafetyPolicy> SafetyPolicies => ToolSafetyPolicies.Build(
            _tools.Values.Select(t => new ToolSafetyDeclaration(t.Name.Value, t.SafetyProfile)));

        public Result<ITool> GetTool(ToolName name) =>
            _tools.TryGetValue(name.Value, out ITool? tool)
                ? Result.Success(tool)
                : Result.Failure<ITool>($"Unknown tool '{name.Value}'.");

        public Result Register(ITool tool) =>
            _tools.TryAdd(tool.Name.Value, tool)
                ? Result.Success()
                : Result.Failure($"Tool '{tool.Name.Value}' is already registered.");

        public Result Unregister(ToolName name) =>
            _tools.Remove(name.Value)
                ? Result.Success()
                : Result.Failure($"Tool '{name.Value}' is not registered.");
    }

    /// <summary>
    ///     #1529: a tool that keeps the callbacks of the <see cref="ToolContext" />
    ///     it was handed, so the test can look at WHERE the per-call state
    ///     lives instead of counting bytes.
    /// </summary>
    private sealed class ContextCapturingTool(string name) : ITool
    {
        public ToolName Name => ToolName.Create(name);
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string DisplayName => name;
        public string Description => "Tool-context capturing harness tool.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        /// <summary>The progress callback the dispatcher wired for this call.</summary>
        public Func<ToolProgressUpdate, CancellationToken, Task>? Progress { get; private set; }

        /// <summary>The ask callback the dispatcher wired for this call.</summary>
        public Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? Ask { get; private set; }

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args, ToolContext context, CancellationToken ct = default)
        {
            Progress = context.ReportProgress;
            Ask = context.Ask;
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "tool-context harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-tool-context-scaling-tests", "code", "test", "test-model"));

    private static ToolDispatcher NewDispatcher(CountingRegistry registry, FakeEventBus bus) =>
        new(registry, new AllowPermissions(), bus,
            NullLogger<ToolDispatcher>.Instance);

    private static IReadOnlyList<ToolCallPart> Batch(string toolName, int count)
    {
        var calls = new ToolCallPart[count];
        JsonElement args = ArgsDoc.RootElement;
        for (int i = 0; i < count; i++)
        {
            calls[i] = new ToolCallPart($"tc_{i}", toolName, args);
        }

        return calls;
    }

    /// <summary>
    ///     One measured leg: <paramref name="toolCalls" /> tool calls, driven
    ///     in batches of <see cref="ToolCalls" />, timed and allocation-
    ///     counter'd as ONE window. The amount of work is fixed, so the
    ///     budget is a per-call number and the runner's load cannot move it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The fake event bus's retained event pile is CLEARED between
    ///         dispatches. It is the harness's own bookkeeping, and an
    ///         unbounded pile makes the GC's marking cost per dispatch grow
    ///         with the number of dispatches - which is what turned the old
    ///         2N leg into 4.69x while the allocations stayed at 1.99x (run
    ///         39423151864). Clearing it bounds the live set, so the measured
    ///         leg is the dispatch path and nothing else.
    ///     </para>
    ///     <para>
    ///         The allocation window uses the PROCESS-WIDE counter (#741): the
    ///         dispatch path awaits, so a per-thread counter sampled on both
    ///         sides of an <c>await</c> is the difference between two unrelated
    ///         thread counters. That is why the class is keyless
    ///         <c>[NotInParallel]</c> - the process counter bills every
    ///         allocation in the process for the duration of the window.
    ///     </para>
    /// </remarks>
    private static async Task<PerfGateLeg> DispatchLegAsync(
        ToolDispatcher dispatcher, FakeEventBus bus, IReadOnlyList<ToolCallPart> calls, int toolCalls)
    {
        int dispatches = toolCalls / ToolCalls;
        var sw = Stopwatch.StartNew();
        long allocated = await AllocationProbe.MeasureProcessAsync(async () =>
        {
            for (int i = 0; i < dispatches; i++)
            {
                bus.Events.Clear();
                await dispatcher
                    .ExecuteAsync(calls, NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        sw.Stop();

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(300_000)]
    public async Task ToolContextBuilds_CostPerToolCall_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var registry = new CountingRegistry(new ProgressTool("noop"));
        var bus = new FakeEventBus();
        var dispatcher = NewDispatcher(registry, bus);
        var calls = Batch("noop", ToolCalls);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: toolCalls => DispatchLegAsync(dispatcher, bus, calls, toolCalls),
            units: ToolCallsPerLeg,
            options: new PerfGateOptions
            {
                Label = "dispatcher-tool-context-cost",
                MaxBytesPerUnit = MaxBytesPerToolCall,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerToolCall,
            });

        await result.AssertWithinBudgetsAsync("tool context build per tool call");
    }

    [Test]
    public async Task ReportsProgress_PublishesUpdateEvents_ForThisCall()
    {
        var registry = new CountingRegistry(new ProgressTool("progressy"));
        var bus = new FakeEventBus();
        var dispatcher = new ToolDispatcher(registry, new AllowPermissions(), bus, NullLogger<ToolDispatcher>.Instance);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            Batch("progressy", 1),
            NewSession(),
            AssistantMessage.Empty("s", "m"),
            CodeAgent(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].IsError).IsFalse();

        List<ToolExecutionUpdateEvent> updates = [.. bus.Events.OfType<ToolExecutionUpdateEvent>()];
        await Assert.That(updates.Count).IsEqualTo(2)
            .Because("the tool reported progress twice; both reports must reach the bus through the rebuilt callback");

        await Assert.That(updates.All(u => u.ToolCallId == "tc_0")).IsTrue()
            .Because("the progress callback must carry the call id of the ToolCallPart its ToolContext was built from");

        // A bare ToolProgressUpdate (no PartialResult) coalesces to the update
        // itself on the event — the `update.PartialResult ?? update` contract.
        ToolProgressUpdate bare = AssertProgressPayload(updates[0]);
        await Assert.That(bare).IsEqualTo(new ToolProgressUpdate("working"));

        await Assert.That(updates[1].PartialResult).IsEqualTo("partial-preview")
            .Because("a partial result rides the event unchanged instead of being rewrapped");
    }

    /// <summary>
    ///     #1529: the steady-state allocation SHAPE, pinned directly. The two
    ///     per-call callbacks used to be inline async lambdas, so each one
    ///     compiled to its own closure display-class — two per tool call.
    ///     They now bind to ONE small sealed state record per call, and this
    ///     test says so without counting bytes: both delegates must carry the
    ///     SAME target object, and that target must not be a compiler-generated
    ///     closure class. It fails the moment either callback goes back to
    ///     capturing state of its own.
    /// </summary>
    [Test]
    public async Task Callbacks_ShareOneStateObject_AndNoClosureDisplayClass()
    {
        var tool = new ContextCapturingTool("capturing");
        var dispatcher = NewDispatcher(new CountingRegistry(tool), new FakeEventBus());

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            Batch("capturing", 1),
            NewSession(),
            AssistantMessage.Empty("s", "m"),
            CodeAgent(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results[0].IsError).IsFalse();

        object? progressTarget = tool.Progress?.Target;
        object? askTarget = tool.Ask?.Target;

        await Assert.That(progressTarget).IsNotNull()
            .Because("the progress callback binds to the per-call state record as its target");
        await Assert.That(progressTarget).IsSameReferenceAs(askTarget)
            .Because("both callbacks must share ONE per-call state object, not one closure display-class each");
        await Assert.That(progressTarget!.GetType().Name).DoesNotContain("DisplayClass")
            .Because("a compiler-generated closure display-class is exactly what #1529 removed from this path");
    }

    /// <summary>
    ///     #1529: a progress publish failure is contained (logged and
    ///     swallowed, §FP-003) — tool execution keeps running and the tool
    ///     result is unaffected.
    /// </summary>
    [Test]
    public async Task ProgressPublishFailure_IsContained_ToolResultUnaffected()
    {
        var bus = new ThrowingUpdateBus();
        var dispatcher = new ToolDispatcher(
            new CountingRegistry(new ProgressTool("progressy")),
            new AllowPermissions(),
            bus,
            NullLogger<ToolDispatcher>.Instance);

        ToolResultMessage message = await dispatcher.ExecuteAsync(
            Batch("progressy", 1),
            NewSession(),
            AssistantMessage.Empty("s", "m"),
            CodeAgent(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(message.Results.Count).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse()
            .Because("§FP-003: a progress publish failure is logged and swallowed, never thrown into the tool run");

        // The start and end events still flowed around the failed update.
        await Assert.That(bus.Events.OfType<ToolExecutionStartEvent>().Count()).IsEqualTo(1);
        await Assert.That(bus.Events.OfType<ToolExecutionEndEvent>().Count()).IsEqualTo(1);
    }

    /// <summary>The progress payload behind the event, asserted to be an unwrapped <see cref="ToolProgressUpdate" />.</summary>
    private static ToolProgressUpdate AssertProgressPayload(ToolExecutionUpdateEvent update)
    {
        if (update.PartialResult is ToolProgressUpdate progress)
        {
            return progress;
        }

        throw new InvalidOperationException(
            "a bare ToolProgressUpdate must reach the event as itself "
            + "(the `update.PartialResult ?? update` coalescing), not rewrapped; got "
            + update.PartialResult?.GetType().Name);
    }
}

/// <summary>Bus that records everything except <see cref="ToolExecutionUpdateEvent" />, whose publish throws.</summary>
internal sealed class ThrowingUpdateBus : IEventBus
{
    public List<AgentEvent> Events { get; } = [];

    public Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        if (@event is ToolExecutionUpdateEvent)
        {
            throw new InvalidOperationException("bus back-pressure (#1529 §FP-003 probe)");
        }

        Events.Add(@event);
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) =>
        new Disposer(() => { });

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : AgentEvent =>
        Subscribe((e, ct) => e is TEvent typed ? handler(typed, ct) : ValueTask.CompletedTask);

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
