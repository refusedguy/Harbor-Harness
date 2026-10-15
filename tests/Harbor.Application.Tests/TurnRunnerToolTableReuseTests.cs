using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
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
///     Issue #1100 (one slice): <c>TurnRunner.RunTurnAsync</c> rebuilt the
///     <c>ToolDefinition[]</c> request table on every turn via
///     <c>ToolTableBuilder.BuildToolDefinitions</c>, even though the resolved
///     set is stable across turns — the frozen registry already memoizes
///     <c>ResolveTools</c> per ruleset, so repeat turns hand back the same
///     array instance. The turn now reuses the built table while the resolved
///     reference is unchanged and rebuilds once when it moves (re-freeze,
///     ruleset swap).
///     <para>
///         Resolve itself stays per-turn (the call is cheap and correctly
///         invalidated by the registry): the cache sits on the build, not on
///         the resolve, so a mid-run registry change is visible on the very
///         next turn instead of served stale.
///     </para>
///     <para>
///         The scaling gate is relative, not absolute: a text-only run with
///         twice the tools must cost at most 3x the time and allocations of
///         the single-size run, measured in the same process
///         (<c>t(2N) &lt;= 3*t(N)</c>). A quadratic regression in the
///         request-build path lands at 4x and cannot hide under it; runner
///         speed cancels in the ratio. Best-of-5 rounds, linux-only (GC
///         accounting varies by OS). Allocations ride
///         <c>AllocationProbe.MeasureProcessAsync</c> (#741: the body awaits,
///         so the per-thread counter would compare two unrelated threads),
///         hence the keyless <c>[NotInParallel]</c>.
///     </para>
/// </summary>
[NotInParallel]
public class TurnRunnerToolTableReuseTests
{
    /// <summary>Tools in the small leg; the large one doubles it.</summary>
    private const int ToolCount = 100;

    /// <summary>Runs per measured leg (one run is sub-millisecond noise).</summary>
    private const int Iterations = 20;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    /// <summary>Rounds per leg, best-of (a descheduled or collected leg only inflates).</summary>
    private const int Rounds = 5;

    private static readonly JsonDocument SchemaDoc = JsonDocument.Parse("""{"type":"object"}""");

    private sealed class NoopTool(string name) : ITool
    {
        public ToolName Name => ToolName.Create(name);
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string DisplayName => name;
        public string Description => "TurnRunner tool-table harness tool.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema => SchemaDoc;

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    /// <summary>
    ///     Registry with a frozen-registry shape: every <c>ResolveTools</c>
    ///     returns the same array instance until <c>Reset</c> swaps the set
    ///     (a re-freeze), which publishes a new instance. Counts resolves so
    ///     the tests pin that resolve stays per-turn while the build is cached.
    /// </summary>
    private sealed class StableToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);
        private IReadOnlyList<ToolDescriptor> _snapshot = [];

        private int _resolveCalls;
        public int ResolveCalls => Volatile.Read(ref _resolveCalls);

        public StableToolRegistry(params ITool[] tools)
        {
            Reset(tools);
        }

        public void Reset(params ITool[] tools)
        {
            _tools.Clear();
            foreach (ITool t in tools)
            {
                _tools[t.Name.Value] = t;
            }

            _snapshot = Snapshot();
        }

        public IReadOnlyList<ToolDescriptor> GetAllTools() => _snapshot;

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null)
        {
            Interlocked.Increment(ref _resolveCalls);
            return _snapshot;
        }

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

        private IReadOnlyList<ToolDescriptor> Snapshot()
        {
            var list = new List<ToolDescriptor>(_tools.Count);
            foreach (ITool t in _tools.Values)
            {
                list.Add(new ToolDescriptor(
                    t.Name, t.DisplayName, t.Description, t.ParameterSchema,
                    t.ExecutionMode, t.PromptSnippet, t.PromptGuidelines));
            }

            return list;
        }
    }

    private static AgentDefinition ToolAgent(int maxSteps) => new(
        AgentName.Create("code"),
        "Code",
        "turnrunner tool-table harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }),
        MaxSteps: maxSteps);

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-turnrunner-tooltable-tests", "code", "test", "test-model"));

    private static AgentLoop NewLoop(ScriptedLlmClient client, StableToolRegistry tools, AgentDefinition agent)
    {
        var agents = new FakeAgentRegistry(agent);
        return new AgentLoop(
            new FakeProviderRegistry(client),
            tools,
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(agents, NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);
    }

    private static ScriptedLlmClient ThreeTurnClient() => new(
    [
        new LlmEvent[]
        {
            new ToolCallStartEvent("call-1", "noop"),
            new ToolCallDeltaEvent("call-1", """{}"""),
            new StepFinishEvent(0, "tool_use", new Usage(2, 1)),
        },
        new LlmEvent[]
        {
            new ToolCallStartEvent("call-2", "noop"),
            new ToolCallDeltaEvent("call-2", """{}"""),
            new StepFinishEvent(0, "tool_use", new Usage(2, 1)),
        },
        new LlmEvent[]
        {
            new TextDeltaEvent("t", "done"),
            new StepFinishEvent(0, "stop", new Usage(1, 1)),
        },
    ]);

    private static ScriptedLlmClient TextOnlyClient() => new(
    [
        new LlmEvent[]
        {
            new TextDeltaEvent("t", "done"),
            new StepFinishEvent(0, "stop", new Usage(1, 1)),
        },
    ]);

    [Test]
    public async Task RunAsync_ReusesToolTableAcrossTurns()
    {
        var registry = new StableToolRegistry(new NoopTool("noop"));
        var client = ThreeTurnClient();
        var agent = ToolAgent(maxSteps: 10);
        var loop = NewLoop(client, registry, agent);

        Result result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
        // Three streamed turns (tool, tool, text-only stop): the reuse must
        // span real turns, not a single-turn run.
        await Assert.That(client.Requests.Count).IsEqualTo(3);
        await Assert.That(ReferenceEquals(client.Requests[0].Tools, client.Requests[1].Tools)).IsTrue()
            .Because("turns 1 and 2 resolve the same set, so the second turn must share the built table instead of rebuilding N entries.");
        await Assert.That(ReferenceEquals(client.Requests[1].Tools, client.Requests[2].Tools)).IsTrue()
            .Because("the text-only closing turn resolves the same set too — no rebuild on the way out either.");
        // Resolve itself stays per-turn: the cache sits on the build, so a
        // mid-run registry change (see the staleness test) stays visible.
        await Assert.That(registry.ResolveCalls).IsEqualTo(3)
            .Because($"resolve is cheap and correctly invalidated by the registry — 3 turns must cost 3 resolves, got {registry.ResolveCalls}.");
    }

    [Test]
    public async Task RunAsync_ToolSetChange_RebuildsTable()
    {
        var registry = new StableToolRegistry(new NoopTool("noop"));
        var agent = ToolAgent(maxSteps: 5);
        var client = TextOnlyClient();
        var loop = NewLoop(client, registry, agent);

        Result first = await loop.RunAsync(NewSession(), agent);
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(1);
        await Assert.That(client.Requests[0].Tools.Count).IsEqualTo(1);

        // A re-freeze with one more tool: the next run resolves a NEW array
        // instance on the SAME loop (warm table cache holding the 1-tool
        // array), so the table must rebuild instead of serving the stale one.
        registry.Reset(new NoopTool("noop"), new NoopTool("extra"));
        Result second = await loop.RunAsync(NewSession(), agent);

        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(client.Requests[1].Tools.Count).IsEqualTo(2)
            .Because("the rebuilt table must carry the post-freeze set (noop + extra), not the stale 1-tool table.");
        await Assert.That(ReferenceEquals(client.Requests[0].Tools, client.Requests[1].Tools)).IsFalse()
            .Because("a new resolved reference must rebuild, never share the previous run's array.");
    }

    private static NoopTool[] NumberedTools(int count)
    {
        var tools = new NoopTool[count];
        for (int i = 0; i < count; i++)
        {
            tools[i] = new NoopTool($"tool_{i:000}");
        }

        return tools;
    }

    /// <summary>
    ///     One measured run: fresh client + loop + session, one text-only turn.
    ///     The loop is fresh per iteration (cold table cache) so every iteration
    ///     pays the build — sharing one loop would cache after the first run
    ///     and blind the gate to build-path regressions. Setup cost is constant
    ///     across legs, so it cancels in the ratio.
    /// </summary>
    private static async Task RunTextOnlyAsync(StableToolRegistry registry, AgentDefinition agent)
    {
        var client = TextOnlyClient();
        var loop = NewLoop(client, registry, agent);
        Result result = await loop.RunAsync(NewSession(), agent).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"scaling harness run failed: {result.Error}");
        }

        if (client.Requests.Count != 1)
        {
            throw new InvalidOperationException($"scaling harness expected 1 turn, streamed {client.Requests.Count}.");
        }
    }

    private static async Task<(double Ms, long Allocated)> MeasureAsync(StableToolRegistry registry, AgentDefinition agent)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long allocated = await AllocationProbe.MeasureProcessAsync(async () =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                await RunTextOnlyAsync(registry, agent).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(120_000)]
    public async Task RunAsync_DoubledTools_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var agent = ToolAgent(maxSteps: 5);
        var small = new StableToolRegistry(NumberedTools(ToolCount));
        var large = new StableToolRegistry(NumberedTools(ToolCount * 2));

        // Warm up (tiered JIT over the turn path) before any measurement.
        await RunTextOnlyAsync(small, agent);
        await RunTextOnlyAsync(large, agent);

        double msSmall = double.MaxValue;
        double msLarge = double.MaxValue;
        long allocSmall = long.MaxValue;
        long allocLarge = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            (double tSmall, long aSmall) = await MeasureAsync(small, agent);
            (double tLarge, long aLarge) = await MeasureAsync(large, agent);
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
            $"turnrunner-tooltable-scaling: {ToolCount} tools x{Iterations} = {msSmall:F3} ms / {allocSmall} B, "
            + $"{ToolCount * 2} tools x{Iterations} = {msLarge:F3} ms / {allocLarge} B "
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
                $"run(2N)/run(N) = {timeRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{msLarge:F3} ms for {ToolCount * 2} tools versus {msSmall:F3} ms for {ToolCount}. "
                + "Linear is 2.00, so this bound separates linear from quadratic without depending on "
                + "how fast this runner is.");
        await Assert.That(allocRatio).IsLessThanOrEqualTo(GrowthLimit)
            .Because(
                $"alloc(2N)/alloc(N) = {allocRatio:F2} against a limit of {GrowthLimit:F2}: "
                + $"{allocLarge} B versus {allocSmall} B. "
                + "Allocations do not move with the runner's speed at all, so this ratio holds on every machine.");
    }
}
