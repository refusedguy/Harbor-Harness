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
///         speed cancels in the ratio. Measured through
///         <see cref="Harbor.TestKit.TestScalingGate" /> (#1652): GC-fenced
///         legs scaled to ~100 ms of real work, medians over five rounds, and
///         a x1.05 noise band on the time branch only — millisecond legs under
///         best-of-5 kept deciding the ratio on CI noise. Linux-only (GC
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

    /// <summary>Runs per measured leg's unit: one text-only turn; the #1652 leg scaler repeats it.</summary>
    private const int Iterations = 20;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

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

    /// <summary>
    ///     One measured leg: <paramref name="units" /> text-only runs, timed and
    ///     allocated as one window (#741: process-wide counter, the body
    ///     awaits). The #1652 leg scaler multiplies <paramref name="units" />
    ///     until the window reaches a measurable duration; every unit is a
    ///     fresh cold-table run, so a build-path regression still lands on the
    ///     2N leg and not the N leg.
    /// </summary>
    private static async Task<TestScalingGateLeg> MeasureLegAsync(StableToolRegistry registry, AgentDefinition agent, int units)
    {
        var sw = Stopwatch.StartNew();
        long allocated = await AllocationProbe.MeasureProcessAsync(async () =>
        {
            for (int i = 0; i < units; i++)
            {
                await RunTextOnlyAsync(registry, agent).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        sw.Stop();
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
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

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => MeasureLegAsync(small, agent, units),
            measureLargeLeg: units => MeasureLegAsync(large, agent, units),
            initialUnits: Iterations,
            options: new TestScalingGateOptions
            {
                Label = "turnrunner-tooltable-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "run(2N)/run(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
    }
}
