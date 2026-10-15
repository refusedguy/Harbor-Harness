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
///         counter, no serialisation needed). Best-of-5 rounds, linux-only (GC
///         accounting varies by OS).
///     </para>
/// </summary>
public class ToolRegistryDescribeOnceTests
{
    /// <summary>Tools in the small leg; the large one doubles it.</summary>
    private const int ToolCount = 100;

    /// <summary>Resolves per measured leg (one resolve is sub-millisecond noise).</summary>
    private const int Iterations = 50;

    /// <summary>Growth allowed for a doubled input, on cost and allocations.</summary>
    private const double GrowthLimit = 3.0;

    /// <summary>Rounds per leg, best-of (a descheduled or collected leg only inflates).</summary>
    private const int Rounds = 5;

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

    private static (double Ms, long Allocated) MeasureFirstTimeResolves(ToolRegistry registry)
    {
        // Warm up (tiered JIT over the filter path) before measuring.
        _ = registry.ResolveTools("code", NewAllowAll());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        long allocated = AllocationProbe.MeasureThread(() =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                // Fresh ruleset instance per iteration: the per-ruleset cache is
                // keyed by reference, so each resolve pays the filter cost.
                _ = registry.ResolveTools("code", NewAllowAll());
            }
        });
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, allocated);
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

        double msSmall = double.MaxValue;
        double msLarge = double.MaxValue;
        long allocSmall = long.MaxValue;
        long allocLarge = long.MaxValue;
        for (int round = 0; round < Rounds; round++)
        {
            (double tSmall, long aSmall) = MeasureFirstTimeResolves(small);
            (double tLarge, long aLarge) = MeasureFirstTimeResolves(large);
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
            $"toolregistry-describeonce-scaling: {ToolCount} tools x{Iterations} = {msSmall:F3} ms / {allocSmall} B, "
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
