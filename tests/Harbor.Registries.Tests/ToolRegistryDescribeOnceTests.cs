using System.Diagnostics;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Tools;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #1100 (ToolRegistry slice): <c>FrozenToolView.Resolve</c> re-described
///     every allowed tool from <c>_tools.Values</c> on each first-time filtered
///     resolve (N <c>_describe</c> calls + a re-sort), even though the frozen
///     <c>_all</c> array already holds the built descriptors in sorted order.
///     The filter now reuses the prebuilt instances (describe-once): a first-time
///     resolve pays N evaluates + one array, an allow-all ruleset shares the
///     <c>_all</c> instance itself, and no re-sort is needed.
///     <para>
///         THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
///         to be <c>t(2N) &lt;= 3*t(N)</c> and <c>alloc(2N) &lt;= 3*alloc(N)</c>
///         over 100 against 200 tools, decided by the shared
///         <see cref="Harbor.TestKit.TestScalingGate" /> (#1652). It is now:
///         <code>
///         bytes per first-time resolve &lt;= 1 500   (machine-independent, HARD)
///         probe units per resolve      &lt;= 40     (machine-scaled time, SOFT)
///         </code>
///         measured over a FIXED amount of work (100 000 first-time filtered
///         resolves over a 100-tool frozen registry) on the SAME runner in the
///         SAME run. Each measured resolve uses a fresh ruleset instance (the
///         per-ruleset cache is keyed by reference), so every iteration pays
///         the filter cost. Synchronous body, so
///         <c>AllocationProbe.MeasureThread</c> (exact per-thread counter, no
///         serialisation needed). The allocation branch is machine-independent
///         and is asserted hard; the time branch is in PROBE UNITS and stands
///         down when the rounds do not reproduce each other. The report prints
///         on every run, pass or fail. Linux-only (GC accounting varies by OS).
///     </para>
/// </summary>
[NotInParallel]
public class ToolRegistryDescribeOnceTests
{
    /// <summary>Tools in the frozen registry the gate resolves against.</summary>
    private const int ToolCount = 100;

    /// <summary>
    ///     First-time filtered resolves per measured leg - the FIXED amount of
    ///     work. 100 000 resolves is about 64 ms of real work on the CI runner.
    /// </summary>
    private const int ResolvesPerLeg = 100_000;

    /// <summary>
    ///     Allocation budget for ONE first-time filtered resolve, in bytes.
    ///     Machine-independent, so it is compared as measured, with no band.
    ///     Pinned from the CI measurement of the ratio gate this replaced (run
    ///     39423151864): 98 400 000 B over 100 000 resolves = 984 B per
    ///     resolve. The budget leaves ~1.52x headroom.
    /// </summary>
    private const long MaxBytesPerResolve = 1_500;

    /// <summary>
    ///     Time budget for ONE first-time filtered resolve, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 64.487 ms over 100 000 resolves = 0.645 us
    ///     per resolve against a probe that costs 0.000380 ms/unit, i.e. ~1.7
    ///     probe units per resolve.
    ///     The budget is wide on purpose: the probe's OWN measured cost moved
    ///     5.3x between two CI runs of the SAME tree (0.000380 ms/unit in run
    ///     39423151864, 0.000072 ms/unit in run 39436405496), so the
    ///     probe-unit currency is only as stable as the yardstick behind it.
    ///     The BYTE budget is the branch that can fail on any runner - it is
    ///     exact - and the time budget is the gross-regression detector.
    /// </summary>
    private const double MaxProbeUnitsPerResolve = 40.0;

    private static readonly JsonDocument SchemaDoc = JsonDocument.Parse("""{"type":"object"}""");

    private sealed class NamedStubTool(string name) : ITool
    {
        public ToolName Name { get; } = ToolName.Create(name);
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string DisplayName => Name.Value;
        public string Description => "Describe-once test stub.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema => SchemaDoc;
        public Result ValidateArguments(JsonElement args) => Result.Success();
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default)
            => Task.FromResult(ToolResult.Success("stub"));
    }

    private static ToolRegistry CreateFrozenRegistry(int count)
    {
        var registry = new ToolRegistry();
        for (int i = 0; i < count; i++)
        {
            registry.Register(new NamedStubTool($"tool_{i:000}"));
        }

        registry.Freeze();
        return registry;
    }

    private static PermissionRuleset NewAllowAll() => new(
        [new PermissionRule("*", "*", PermissionAction.Allow)],
        Array.Empty<IArgSafetyPolicy>());

    [Test]
    public async Task ResolveTools_FilteredAllowAll_SharesUnfilteredInstance()
    {
        var registry = CreateFrozenRegistry(4);

        var unfiltered = registry.ResolveTools("code");
        var filtered = registry.ResolveTools("code", NewAllowAll());

        await Assert.That(ReferenceEquals(filtered, unfiltered)).IsTrue()
            .Because("describe-once: an allow-all filter reuses the prebuilt array itself instead of copying N descriptors.");
    }

    [Test]
    public async Task ResolveTools_FilteredPartial_ReusesDescriptorInstancesInOrder()
    {
        var registry = new ToolRegistry();
        foreach (var name in new[] { "zeta_tool", "beta_tool", "alpha_tool", "delta_tool" })
        {
            registry.Register(new NamedStubTool(name));
        }

        registry.Freeze();

        var permission = new PermissionRuleset(
            [
                new PermissionRule("beta_tool", "*", PermissionAction.Allow),
                new PermissionRule("delta_tool", "*", PermissionAction.Allow),
            ],
            Array.Empty<IArgSafetyPolicy>());

        var unfiltered = registry.ResolveTools("code");
        var filtered = registry.ResolveTools("code", permission);

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered[0].Name.Value).IsEqualTo("beta_tool");
        await Assert.That(filtered[1].Name.Value).IsEqualTo("delta_tool");

        // Describe-once: the filtered entries are the same instances as in _all,
        // found by ordinal position (the unfiltered array is pre-sorted).
        int beta = -1;
        int delta = -1;
        for (int i = 0; i < unfiltered.Count; i++)
        {
            if (unfiltered[i].Name.Value == "beta_tool")
            {
                beta = i;
            }

            if (unfiltered[i].Name.Value == "delta_tool")
            {
                delta = i;
            }
        }

        await Assert.That(beta).IsGreaterThanOrEqualTo(0);
        await Assert.That(delta).IsGreaterThanOrEqualTo(0);
        await Assert.That(ReferenceEquals(filtered[0], unfiltered[beta])).IsTrue()
            .Because("a filtered resolve must reuse the frozen descriptor, not re-describe the tool.");
        await Assert.That(ReferenceEquals(filtered[1], unfiltered[delta])).IsTrue()
            .Because("a filtered resolve must reuse the frozen descriptor, not re-describe the tool.");

        // Repeat with the same ruleset instance hits the memoised array.
        var again = registry.ResolveTools("code", permission);
        await Assert.That(ReferenceEquals(filtered, again)).IsTrue();
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> first-time filtered
    ///     resolves, timed and allocation-counter'd as one window. A fresh
    ///     ruleset per iteration is INSIDE the window (the per-ruleset cache is
    ///     keyed by reference), so every iteration pays the filter cost.
    /// </summary>
    private static PerfGateLeg MeasureFirstTimeResolves(ToolRegistry registry, int units)
    {
        var sw = Stopwatch.StartNew();
        long allocated = AllocationProbe.MeasureThread(() =>
        {
            for (int i = 0; i < units; i++)
            {
                // Fresh ruleset instance per iteration: the per-ruleset cache is
                // keyed by reference, so each resolve pays the filter cost.
                _ = registry.ResolveTools("code", NewAllowAll());
            }
        });
        sw.Stop();
        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(300_000)]
    public async Task ResolveTools_CostPerResolve_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // GC accounting varies across OS runtimes.
        }

        var registry = CreateFrozenRegistry(ToolCount);

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: units => Task.FromResult(MeasureFirstTimeResolves(registry, units)),
            units: ResolvesPerLeg,
            options: new PerfGateOptions
            {
                Label = "toolregistry-describeonce-cost",
                MaxBytesPerUnit = MaxBytesPerResolve,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerResolve,
            });

        await result.AssertWithinBudgetsAsync("first-time filtered tool resolve");
    }
}
