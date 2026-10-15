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
///         pins the registry hits at N, not 2N. Best-of-5 rounds, linux-only
///         (GC accounting varies by OS).
///     </para>
/// </summary>
[NotInParallel("dispatcher-scale")]
public class ToolDispatcherResolveOnceTests
{
    /// <summary>Tool calls in the small batch; the large one doubles it.</summary>
    private const int ToolCalls = 100;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    /// <summary>Rounds per leg, best-of (a descheduled or collected leg only inflates).</summary>
    private const int Rounds = 5;

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

    private static async Task<(double Ms, long Allocated)> MeasureAsync(ToolDispatcher dispatcher, IReadOnlyList<ToolCallPart> calls)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        await dispatcher.ExecuteAsync(calls, NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .ConfigureAwait(false);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (sw.Elapsed.TotalMilliseconds, allocated);
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

        // Warm up (tiered JIT over the dispatch path) before any measurement.
        await MeasureAsync(dispatcher, small);
        await MeasureAsync(dispatcher, large);

        double msSmall = double.MaxValue;
        double msLarge = double.MaxValue;
        long allocSmall = long.MaxValue;
        long allocLarge = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            (double tSmall, long aSmall) = await MeasureAsync(dispatcher, small);
            (double tLarge, long aLarge) = await MeasureAsync(dispatcher, large);
            msSmall = Math.Min(msSmall, tSmall);
            msLarge = Math.Min(msLarge, tLarge);
            allocSmall = Math.Min(allocSmall, aSmall);
            allocLarge = Math.Min(allocLarge, aLarge);
        }

        double timeRatio = msLarge / msSmall;
        double allocRatio = (double)allocLarge / allocSmall;

        // Printed, not only asserted, so the deciding ratio is readable next
        // to the test instead of existing only in a failure message. The
        // absolute columns are informational and runner-dependent.
        Console.WriteLine(
            $"dispatcher-dispatch-scaling: {ToolCalls} calls x1 = {msSmall:F3} ms / {allocSmall} B, "
            + $"{ToolCalls * 2} calls x1 = {msLarge:F3} ms / {allocLarge} B "
            + $"(time x{timeRatio:F2}, alloc x{allocRatio:F2}, limit x{GrowthLimit:F2}, best of {Rounds}; "
            + "the absolute columns are informational and runner-dependent)");

        // Non-vacuity: both ratios divide by the small leg, so a zero there
        // means "measured nothing" — the #591 shape.
        await Assert.That(msSmall).IsGreaterThan(0.0)
            .Because($"the small-leg baseline must be a real measurement, got {msSmall:F3} ms.");
        await Assert.That(allocSmall).IsGreaterThan(0L)
            .Because($"the small-leg allocation baseline must be a real measurement, got {allocSmall} B.");

        await Assert.That(timeRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"dispatch(2N)/dispatch(N) = {timeRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{msLarge:F3} ms for {ToolCalls * 2} tool calls versus {msSmall:F3} ms for {ToolCalls}. "
                + "Linear is 2.00, so this bound separates linear from quadratic without depending on "
                + "how fast this runner is.");
        await Assert.That(allocRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"alloc(2N)/alloc(N) = {allocRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{allocLarge} B versus {allocSmall} B. "
                + "Allocations do not move with the runner's speed at all, so this ratio holds on every machine.");
    }
}
