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
// #1769: the permission double is generated — the fake only declares what it needs.
using FakePermissionServiceBase = Harbor.TestKit.Generated.FakePermissionService;

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
///         The scaling gate is relative, not absolute: building twice the
///         tool contexts must cost at most 3x the time and allocations of
///         the single-size batch, measured in the same run
///         (<c>t(2N) &lt;= 3*t(N)</c>, <c>alloc(2N) &lt;= 3*alloc(N)</c> —
///         the #410/#1373 shape on the shared
///         <see cref="Harbor.TestKit.TestScalingGate" /> #1652 recipe).
///         Absolute columns print for info only. Linux-only (GC accounting
///         varies by OS).
///     </para>
///     <para>
///         This is a second gate next to
///         <c>ToolDispatcherResolveOnceTests.ExecuteAsync_DoubledBatch_StaysWithinGrowthLimit</c>
///         (label <c>dispatcher-dispatch-scaling</c>) on purpose: that one
///         dispatches a no-op tool, so it never enters the callback this
///         slice rebuilt. The tool here reports progress twice per call, so
///         every measured call goes through the rebuilt progress callback.
///     </para>
/// </summary>
[NotInParallel("dispatcher-scale")]
public class ToolDispatcherToolContextScalingTests
{
    /// <summary>Tool calls in the small batch; the large one doubles it.</summary>
    private const int ToolCalls = 100;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

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

    private static ToolDispatcher NewDispatcher(CountingRegistry registry) =>
        new(registry, new AllowPermissions(), new FakeEventBus(),
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
    ///     One measured leg: <paramref name="units" /> dispatches of the same
    ///     batch — each dispatch builds one <see cref="ToolContext" /> per
    ///     tool call (batch N = N builds), timed and allocation-counter'd as
    ///     one window. The #1652 leg scaler multiplies <paramref name="units" />
    ///     until the window reaches a measurable duration; the batch stays
    ///     fixed so the ratio stays about the per-build cost, not about batch
    ///     size.
    /// </summary>
    private static async Task<TestScalingGateLeg> DispatchLegAsync(
        ToolDispatcher dispatcher, IReadOnlyList<ToolCallPart> calls, int units)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < units; i++)
        {
            await dispatcher.ExecuteAsync(calls, NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
                .ConfigureAwait(false);
        }

        sw.Stop();
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Test]
    [Timeout(120_000)]
    public async Task ToolContextBuilds_DoubledBatch_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var registry = new CountingRegistry(new ProgressTool("noop"));
        var dispatcher = NewDispatcher(registry);
        var small = Batch("noop", ToolCalls);
        var large = Batch("noop", ToolCalls * 2);

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => DispatchLegAsync(dispatcher, small, units),
            measureLargeLeg: units => DispatchLegAsync(dispatcher, large, units),
            initialUnits: 1,
            options: new TestScalingGateOptions
            {
                Label = "dispatcher-tool-context-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "tool-context(2N)/tool-context(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
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
        var dispatcher = NewDispatcher(new CountingRegistry(tool));

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
