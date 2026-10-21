using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
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
///     Issue #1100 (one slice): <c>ToolDispatcher.ExecuteAsync</c> resolved
///     every tool name twice per turn - once in <c>HasSequentialTool</c>
///     (<c>ToolName.TryCreate</c> + <c>GetTool</c> per call) and again in
///     <c>ExecuteSingleAsync.ResolveTool</c> for the same names. The batch is
///     now resolved once up front; the pre-resolved outcomes feed both the
///     sequential gate and dispatch.
///     <para>
///         THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
///         to be <c>t(2N) &lt;= 3*t(N)</c> and <c>alloc(2N) &lt;= 3*alloc(N)</c>
///         over 100 against 200 tool calls, decided by
///         <see cref="Harbor.TestKit.TestScalingGate" /> (#1652). The gate is
///         now:
///         <code>
///         bytes per tool call   &lt;= 3 500    (machine-independent, HARD)
///         probe units per call   &lt;= 175      (machine-scaled time, SOFT)
///         </code>
///         measured over a FIXED amount of work (25 000 tool calls, in batches
///         of 100) on the SAME runner in the SAME run. The allocation branch
///         is machine-independent and is asserted hard; the time branch is in
///         PROBE UNITS (the cost of one call divided by the cost of one unit
///         of the fixed calibration probe measured on the same runner in the
///         same run, so the runner's speed cancels) and is asserted only when
///         the rounds reproduce each other. The report prints on every run,
///         pass or fail. Linux-only (GC accounting varies by OS).
///     </para>
///     <para>
///         A double-resolve regression lands far above the byte budget: it
///         pays a second <c>GetTool</c> hit per call, and the budget is
///         pinned to the one-resolve measurement.
///     </para>
/// </summary>
[NotInParallel]
public class ToolDispatcherResolveOnceTests
{
    /// <summary>Tool calls in one dispatch - the batch the gate measures.</summary>
    private const int ToolCalls = 100;

    /// <summary>
    ///     Tool calls per measured leg - the FIXED amount of work. 25 000 calls
    ///     is 250 dispatches, about 60 ms of real work on the CI runner.
    /// </summary>
    private const int ToolCallsPerLeg = 25_000;

    /// <summary>
    ///     Allocation budget for ONE tool call, in bytes. Machine-independent,
    ///     so it is compared as measured, with no band. Pinned from the CI
    ///     measurement of the ratio gate this replaced (run 39423151864):
    ///     58 421 064 B over 281 dispatches of 100 calls = 2 079 B per tool
    ///     call. The budget leaves ~1.68x headroom.
    /// </summary>
    private const long MaxBytesPerToolCall = 3_500;

    /// <summary>
    ///     Time budget for ONE tool call, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 68.499 ms over 28 100 calls = 2.44 us per
    ///     call against a probe that costs 0.000380 ms/unit, i.e. ~6.4 probe
    ///     units per call.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerToolCall = 175.0;

    private static readonly JsonDocument ArgsDoc = JsonDocument.Parse("""{}""");

    private sealed class AllowPermissions : FakePermissionServiceBase
    {
        public AllowPermissions()
        {
            CheckAsyncResult = Result.Success(TestToolContext.AllowWithoutPersist());
            AskUserAsyncResult = Result.Success(TestToolContext.DenyWithoutPersist());
            GetRulesetValue = PermissionRuleset.Empty;
            SaveAsyncResult = Result.Success();
        }
    }

    private sealed class NoopTool(string name, ExecutionMode mode) : ITool
    {
        public ToolName Name => ToolName.Create(name);
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string DisplayName => name;
        public string Description => "Resolve-once harness tool.";
        public ExecutionMode ExecutionMode => mode;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class CountingRegistry(params ITool[] tools) : IToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools =
            tools.ToDictionary(t => t.Name.Value, StringComparer.Ordinal);

        private int _getToolCalls;
        public int GetToolCalls => Volatile.Read(ref _getToolCalls);

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

        public Result<ITool> GetTool(ToolName name)
        {
            Interlocked.Increment(ref _getToolCalls);
            return _tools.TryGetValue(name.Value, out ITool? tool)
                ? Result.Success(tool)
                : Result.Failure<ITool>($"Unknown tool '{name.Value}'.");
        }

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
        "resolve-once harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-resolve-once-tests", "code", "test", "test-model"));

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

    [Test]
    public async Task ExecuteAsync_ResolvesEachToolOnce()
    {
        var registry = new CountingRegistry(new NoopTool("noop", ExecutionMode.Parallel));
        var dispatcher = NewDispatcher(registry, new FakeEventBus());

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            Batch("noop", 8), NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(8);
        await Assert.That(registry.GetToolCalls).IsEqualTo(8)
            .Because($"one resolve per call: 8 calls must cost 8 GetTool hits, got {registry.GetToolCalls} (2N = 16 is the pre-#1100 shape).");
    }

    [Test]
    public async Task ExecuteAsync_SequentialBatch_ResolvesEachToolOnce()
    {
        var registry = new CountingRegistry(
            new NoopTool("seq", ExecutionMode.Sequential),
            new NoopTool("noop", ExecutionMode.Parallel));
        var dispatcher = NewDispatcher(registry, new FakeEventBus());

        var calls = new ToolCallPart[]
        {
            new("tc_0", "seq", ArgsDoc.RootElement),
            new("tc_1", "noop", ArgsDoc.RootElement),
            new("tc_2", "seq", ArgsDoc.RootElement),
            new("tc_3", "noop", ArgsDoc.RootElement),
        };

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            calls, NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(4);
        await Assert.That(result.Results.All(r => !r.IsError)).IsTrue();
        await Assert.That(registry.GetToolCalls).IsEqualTo(4)
            .Because($"sequential gate must reuse the pre-resolved batch: 4 calls must cost 4 GetTool hits, got {registry.GetToolCalls}.");
    }

    [Test]
    public async Task ExecuteAsync_UnknownTool_KeepsDiagnostic()
    {
        var registry = new CountingRegistry(new NoopTool("noop", ExecutionMode.Parallel));
        var dispatcher = NewDispatcher(registry, new FakeEventBus());

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            [new ToolCallPart("tc_0", "nope", ArgsDoc.RootElement)],
            NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(1);
        await Assert.That(result.Results[0].IsError).IsTrue();
        await Assert.That(result.Results[0].Output).Contains("Unknown tool");
        await Assert.That(result.Results[0].Output).Contains("noop");
    }

    /// <summary>
    ///     One measured leg: <paramref name="toolCalls" /> tool calls, driven
    ///     in batches of <see cref="ToolCalls" />, timed and allocation-
    ///     counter'd as ONE window.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The fake event bus's retained event pile is CLEARED between
    ///         dispatches: an unbounded pile makes the GC's marking cost per
    ///         dispatch grow with the number of dispatches, which is a property
    ///         of the harness and not of the dispatcher.
    ///     </para>
    ///     <para>
    ///         The allocation window uses the PROCESS-WIDE counter (#741): the
    ///         dispatch path awaits, so a per-thread counter sampled on both
    ///         sides of an <c>await</c> is the difference between two unrelated
    ///         thread counters. That is why the class is keyless
    ///         <c>[NotInParallel]</c>.
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
    public async Task ExecuteAsync_CostPerToolCall_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var registry = new CountingRegistry(new NoopTool("noop", ExecutionMode.Parallel));
        var bus = new FakeEventBus();
        var dispatcher = NewDispatcher(registry, bus);
        var calls = Batch("noop", ToolCalls);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: toolCalls => DispatchLegAsync(dispatcher, bus, calls, toolCalls),
            units: ToolCallsPerLeg,
            options: new PerfGateOptions
            {
                Label = "dispatcher-dispatch-cost",
                MaxBytesPerUnit = MaxBytesPerToolCall,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerToolCall,
            });

        await result.AssertWithinBudgetsAsync("tool dispatch per tool call");
    }
}
