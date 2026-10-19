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
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     #1529 (#97 slice, per-tool-call): <c>ToolDispatcher.CreateToolContext</c>
///     wired the two per-call callbacks as async lambdas, each compiling to a
///     closure display-class (progress captured toolCall/eventBus/logger,
///     Ask captured toolCall/permissions/logger) — 2 display-classes per tool
///     call on the hot path. Both now route through one small sealed state
///     object per call (<c>ToolCallGates</c>) with the bodies hoisted to
///     dispatcher instance methods, so zero display-class allocations remain
///     per call; behaviour is byte-identical (the callback pins below, plus
///     <c>ToolDispatcherAskFailClosedTests</c> for the Ask fail-closed half).
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
/// </summary>
[NotInParallel("dispatcher-scale")]
public class ToolDispatcherToolContextScalingTests
{
    /// <summary>Tool calls in the small batch; the large one doubles it.</summary>
    private const int ToolCalls = 100;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{}""");

    private sealed class AllowPermissions : IPermissionService
    {
        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args, CancellationToken ct = default,
            string? invocationId = null, int generation = 1) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Allow, false)));

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Deny, false)));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result> SaveAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
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
