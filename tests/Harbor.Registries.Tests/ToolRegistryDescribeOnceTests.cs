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
///         The scaling gate is relative, not absolute: a first-time filtered
///         resolve over twice the tools must cost at most 3x the time and
///         allocations of the single-size run, measured in the same process
///         (<c>t(2N) &lt;= 3*t(N)</c>). A quadratic regression lands at 4x and
///         cannot hide under it; runner speed cancels in the ratio. Each measured
///         resolve uses a fresh ruleset instance (the per-ruleset cache is keyed
///         by reference), so every iteration pays the filter cost. Synchronous
///         body, so <c>AllocationProbe.MeasureThread</c> (exact per-thread
///         counter, no serialisation needed). Measured through
///         <see cref="Harbor.TestKit.TestScalingGate" /> (#1652): GC-fenced
///         legs scaled to ~100 ms of real work, medians over five rounds, and
///         a x1.05 noise band on the time branch only. Linux-only (GC
///         accounting varies by OS).
///     </para>
/// </summary>
public class ToolRegistryDescribeOnceTests
{
    /// <summary>Tools in the small leg; the large one doubles it.</summary>
    private const int ToolCount = 100;

    /// <summary>Resolves per leg's unit: one fresh-ruleset filtered resolve.</summary>
    private const int Iterations = 50;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

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
    private static TestScalingGateLeg MeasureFirstTimeResolves(ToolRegistry registry, int units)
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
        return new TestScalingGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    [Test]
    [Timeout(120_000)]
    public async Task ResolveTools_DoubledTools_StaysWithinGrowthLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // GC accounting varies across OS runtimes.
        }

        var small = CreateFrozenRegistry(ToolCount);
        var large = CreateFrozenRegistry(ToolCount * 2);

        TestScalingGateResult result = await TestScalingGate.MeasureAsync(
            measureSmallLeg: units => Task.FromResult(MeasureFirstTimeResolves(small, units)),
            measureLargeLeg: units => Task.FromResult(MeasureFirstTimeResolves(large, units)),
            initialUnits: Iterations,
            options: new TestScalingGateOptions
            {
                Label = "toolregistry-describeonce-scaling",
                GrowthLimit = GrowthLimit,
            });

        await result.AssertWithinLimitsAsync(
            topic: "resolve(2N)/resolve(N)",
            linearWhy: "Linear is 2.00, so this bound separates linear from quadratic without depending on how fast this runner is.");
    }
}
