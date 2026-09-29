using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="ToolRegistry" /> hot paths:
///     - <see cref="ToolRegistry.ResolveTools" />: builds a list of descriptors
///     for an agent. With frozen snapshot, returns a sized array directly.
///     - <see cref="ToolRegistry.GetTool" />: O(1) lookup by name.
///     The frozen path uses <c>FrozenDictionary</c>; the unfrozen path falls back
///     to <c>NonBlocking.ConcurrentDictionary</c>.
///     <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
///     <list type="bullet">
///         <item><c>Operation:</c> one <c>ResolveTools</c> or one <c>GetTool</c> call
///         against a registry holding <c>ToolCount</c> stub tools.</item>
///         <item><c>Payload:</c> the agent name <c>"code"</c> (ignored by the registry — permission
///         filtering uses only the session ruleset) and, for the permission rows, an explicit
///         ruleset holding one <c>Allow</c> rule per stub tool.</item>
///         <item><c>StateReset:</c> none — both registries and the ruleset are built once in
///         <c>Setup</c>; nothing is reset between iterations.</item>
///         <item><c>Drain:</c> none — every row is synchronous and returns before the op ends.</item>
///         <item><c>RetainedState:</c> the frozen snapshot's cached descriptor arrays plus its
///         per-ruleset filtered cache. Both are populated on the first op and reused by every
///         later iteration — that memoisation is the thing being measured, so it is NOT reset.</item>
///         <item><c>AwaitSemantics:</c> n/a — none of the rows is async.</item>
///         <item><c>AllocAttribution:</c> the frozen rows allocate zero once warm (cached array is
///         returned as-is); the unfrozen rows allocate a fresh <c>List&lt;ToolDescriptor&gt;</c> per
///         call — that difference is the frozen-vs-unfrozen contract, not measurement noise.</item>
///     </list>
///     <para>
///         <b>Comparability (#408).</b> Only the frozen no-permission / allow-all pair is
///         directly comparable: both resolve all <c>ToolCount</c> descriptors, so the delta is
///         the permission filter, not a result-shape change. <c>Setup</c> proves that with
///         <c>AssertComparable</c> (equal resolved count, identical name set, identical element
///         type) and <b>throws</b> instead of printing an incomparable number.
///         <see cref="ResolveTools_Frozen_DeniedAll" /> resolves <b>0</b> tools and is therefore
///         NOT comparable to the other rows — it prices the empty/denied path only.
///     </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ToolRegistryBenchmark
{
    private ToolRegistry _frozenRegistry = null!;
    private PermissionRuleset _permission = null!;
    private PermissionRuleset _permissionDenyAll = null!;
    private ToolName _toolName = null!;
    private ToolRegistry _unfrozenRegistry = null!;

    [Params(4, 8, 16)]
    public int ToolCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frozenRegistry = new ToolRegistry();
        _unfrozenRegistry = new ToolRegistry();
        _toolName = ToolName.Create("tool_0");

        // PermissionRuleset.Default denies every stub tool (no rule matches
        // "tool_N", so Evaluate falls through to Ask and the filter drops them).
        // That made the old "with permission" row resolve 0 of N tools, i.e. a
        // SHAPE change, not a performance change. Explicit Allow-per-stub-tool
        // rules make the compared cases resolve the same N descriptors.
        var rules = new PermissionRule[ToolCount];
        var denyRules = new PermissionRule[ToolCount];
        for (int i = 0; i < ToolCount; i++)
        {
            rules[i] = new PermissionRule($"tool_{i}", "*", PermissionAction.Allow);
            denyRules[i] = new PermissionRule($"tool_{i}", "*", PermissionAction.Deny);
        }

        // No safety policies: the fixture rules are the whole decision, so a new
        // builtin PathGuard/Bash policy can never silently suppress the Allow and
        // turn the comparable pair back into a 0-of-N shape change.
        _permission = new PermissionRuleset(rules, Array.Empty<IArgSafetyPolicy>());
        _permissionDenyAll = new PermissionRuleset(denyRules, Array.Empty<IArgSafetyPolicy>());

        for (int i = 0; i < ToolCount; i++)
        {
            var tool = new StubTool($"tool_{i}", $"Tool {i}");
            _frozenRegistry.Register(tool);
            _unfrozenRegistry.Register(tool);
        }

        _frozenRegistry.Freeze();

        AssertComparable(
            compared: "frozen no-permission vs frozen allow-all",
            left: _frozenRegistry.ResolveTools("code"),
            right: _frozenRegistry.ResolveTools("code", _permission));

        AssertComparable(
            compared: "frozen allow-all vs unfrozen allow-all",
            left: _frozenRegistry.ResolveTools("code", _permission),
            right: _unfrozenRegistry.ResolveTools("code", _permission));

        // Warm the unfrozen path once so the comparison above does not measure a
        // descriptor-construction cold start.
        var unfrozenWarm = _unfrozenRegistry.ResolveTools("code", _permission);
        AssertComparable(
            compared: "frozen allow-all vs unfrozen allow-all (warm)",
            left: _frozenRegistry.ResolveTools("code", _permission),
            right: unfrozenWarm);

        // The denied row is intentionally a different shape — pin the fact so a
        // future ruleset change cannot silently turn it into a comparable case.
        var denied = _frozenRegistry.ResolveTools("code", _permissionDenyAll);
        if (denied.Count != 0)
        {
            throw new InvalidOperationException(
                $"ToolRegistryBenchmark: the deny-all ruleset is expected to resolve 0 tools, "
                + $"but it resolved {denied.Count}. The DeniedAll row's contract ('different "
                + "result shape, not comparable') would no longer hold.");
        }
    }

    /// <summary>
    ///     Baseline row. Resolves all <c>ToolCount</c> descriptors from the frozen
    ///     snapshot's cached array — no filtering, zero allocation once warm.
    /// </summary>
    [Benchmark(Description = "ResolveTools (frozen, no permission)", Baseline = true)]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Frozen_NoPermission()
        => _frozenRegistry.ResolveTools("code");

    /// <summary>
    ///     Comparable with <see cref="ResolveTools_Frozen_NoPermission" />: the allow-all
    ///     ruleset resolves the same <c>ToolCount</c> descriptors, so the delta is the
    ///     permission filter (evaluated on every call, cached per ruleset instance).
    /// </summary>
    [Benchmark(Description = "ResolveTools (frozen, allow-all permission)")]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Frozen_WithPermission()
        => _frozenRegistry.ResolveTools("code", _permission);

    /// <summary>
    ///     NOT comparable with the rows above — the deny-all ruleset resolves <b>0</b>
    ///     descriptors, so this row prices the empty/denied path (lock + cache probe on a
    ///     cached empty snapshot), not "ResolveTools with permission". Kept because the
    ///     denied path is a real production shape; <c>Setup</c> pins its 0-count.
    /// </summary>
    [Benchmark(Description = "ResolveTools (frozen, deny-all — 0 tools; NOT comparable)")]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Frozen_DeniedAll()
        => _frozenRegistry.ResolveTools("code", _permissionDenyAll);

    [Benchmark(Description = "ResolveTools (unfrozen, no permission)")]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Unfrozen()
        => _unfrozenRegistry.ResolveTools("code");

    /// <summary>
    ///     Comparable with <see cref="ResolveTools_Frozen_WithPermission" /> on tool count
    ///     and result shape; the difference is the frozen snapshot (cached array, zero alloc)
    ///     versus a fresh list per call.
    /// </summary>
    [Benchmark(Description = "ResolveTools (unfrozen, allow-all permission)")]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Unfrozen_WithPermission()
        => _unfrozenRegistry.ResolveTools("code", _permission);

    [Benchmark(Description = "GetTool (frozen)")]
    public Result<ITool> GetTool_Frozen() => _frozenRegistry.GetTool(_toolName);

    [Benchmark(Description = "GetTool (unfrozen)")]
    public Result<ITool> GetTool_Unfrozen() => _unfrozenRegistry.GetTool(_toolName);

    /// <summary>
    ///     Fail loudly when two rows that get printed side by side would not be
    ///     comparable: different resolved-tool counts, a different set of tool names, or
    ///     a different descriptor type. BenchmarkDotNet would happily print both numbers
    ///     next to each other and let the reader assume they measure the same work.
    /// </summary>
    /// <remarks>
    ///     Name comparison is order-insensitive on purpose: the frozen path walks a
    ///     <c>FrozenDictionary</c> and the unfrozen path a <c>ConcurrentDictionary</c>, so
    ///     the two iterate in different orders by construction. Order is not part of the
    ///     contract; membership and count are.
    /// </remarks>
    private static void AssertComparable(string compared, IReadOnlyList<ToolDescriptor> left, IReadOnlyList<ToolDescriptor> right)
    {
        if (left.Count != right.Count)
        {
            throw new InvalidOperationException(
                $"ToolRegistryBenchmark: {compared} is not comparable — resolved-tool count "
                + $"differs ({left.Count} vs {right.Count}). The delta would be a result-shape "
                + "change, not a performance change. Fix the fixture rulesets.");
        }

        var rightNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < right.Count; i++)
        {
            rightNames.Add(right[i].Name.Value);
        }

        for (int i = 0; i < left.Count; i++)
        {
            string name = left[i].Name.Value;
            if (!rightNames.Contains(name))
            {
                throw new InvalidOperationException(
                    $"ToolRegistryBenchmark: {compared} is not comparable — the left side "
                    + $"resolves '{name}', which the right side does not.");
            }

            Type descriptorType = left[i].GetType();
            if (descriptorType != right[i].GetType())
            {
                throw new InvalidOperationException(
                    $"ToolRegistryBenchmark: {compared} is not comparable — descriptor type "
                    + $"differs ({descriptorType.Name} vs {right[i].GetType().Name}).");
            }
        }
    }
}

/// <summary>
///     Minimal stub tool for benchmarking the registry without side effects.
/// </summary>
internal sealed class StubTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("{}");

    public StubTool(string name, string description)
    {
        Name = ToolName.Create(name);
        DisplayName = name;
        Description = description;
    }

    public ToolName Name { get; }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName { get; }
    public string Description { get; }
    public JsonDocument ParameterSchema => Schema;
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => null;
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(ToolResult.Success("ok"));
}
