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
// #1769: the permission double is generated — the fake only declares what it needs.
using FakePermissionServiceBase = Harbor.TestKit.Generated.FakePermissionService;

using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     Issue #1100 (one slice): <c>ToolDispatcher.ExecuteAsync</c> resolved
///     every tool name twice per turn — once in <c>HasSequentialTool</c>
///     (<c>ToolName.TryCreate</c> + <c>GetTool</c> per call) and again in
///     <c>ExecuteSingleAsync.ResolveTool</c> for the same names. The batch is
///     now resolved once up front; the pre-resolved outcomes feed both the
///     sequential gate and dispatch.
///     <para>
///         The scaling gate is relative, not absolute: dispatching twice the
///         calls must cost at most 3x the time and allocations of the
///         single-size batch, measured in the same run
///         (<c>t(2N) &lt;= 3*t(N)</c>). A double-resolve regression lands
///         above it only when paired with the exact-count test below, which
///         pins the registry hits at N, not 2N. Measured through
///         <see cref="Harbor.TestKit.TestScalingGate" /> (#1652): GC-fenced
///         legs scaled to ~100 ms of real work, medians over five rounds, and
///         a x1.05 noise band on the time branch only — the millisecond-leg
///         best-of-5 shape that flaked three gates in one day is gone.
///         Linux-only (GC accounting varies by OS).
///     </para>
/// </summary>
[NotInParallel("dispatcher-scale")]
public class ToolDispatcherResolveOnceTests
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

    [Test]
    public async Task ExecuteAsync_ResolvesEachToolOnce()
    {
        var registry = new CountingRegistry(new NoopTool("noop", ExecutionMode.Parallel));
        var dispatcher = NewDispatcher(registry);

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
        var dispatcher = NewDispatcher(registry);

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
        var dispatcher = NewDispatcher(registry);

        ToolResultMessage result = await dispatcher.ExecuteAsync(
            [new ToolCallPart("tc_0", "nope", ArgsDoc.RootElement)],
            NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None);

        await Assert.That(result.Results.Count).IsEqualTo(1);
        await Assert.That(result.Results[0].IsError).IsTrue();
        await Assert.That(result.Results[0].Output).Contains("Unknown tool");
        await Assert.That(result.Results[0].Output).Contains("noop");
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> dispatches of the same
    ///     batch, timed and allocation-counter'd as one window. The #1652 leg
    ///     scaler multiplies <paramref name="units" /> until the window reaches
    ///     a measurable duration; the batch itself stays fixed so the ratio
    ///     stays about the dispatch path, not about batch size.
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
    public async Task ExecuteAsync_DoubledBatch_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var registry = new CountingRegistry(new NoopTool("noop", ExecutionMode.Parallel));
        var dispatcher = NewDispatcher(registry);
        var small = Batch("noop", ToolCalls);
        var large = Batch("noop", ToolCalls * 2);

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => DispatchLegAsync(dispatcher, small, units),
            measureLargeLeg: units => DispatchLegAsync(dispatcher, large, units),
            initialUnits: 1,
            options: new TestScalingGateOptions
            {
                Label = "dispatcher-dispatch-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "dispatch(2N)/dispatch(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
    }
}
