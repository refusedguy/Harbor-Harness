using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
namespace Harbor.Benchmarks;
/// <summary>
///     Benchmarks <see cref="PermissionRuleset.Evaluate" />, a hot path invoked for every tool
///     call to decide Allow/Ask/Deny. Measures:
///     - against the <see cref="PermissionRuleset.Default" /> ruleset (baseline, early Allow match),
///     - against custom rulesets of varying size (4/16/64 rules) to show rule-scan cost as
///     rulesets grow,
///     - Allow vs Ask vs Deny paths for a tool, to capture differing early-exit behaviour.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PermissionRulesetBenchmark
{
    private PermissionRuleset _customRuleset = null!;
    private PermissionRuleset _defaultRuleset = null!;
    private ToolName _readTool = null!;

    [Params(4, 16, 64)]
    public int RuleCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _defaultRuleset = PermissionRuleset.Default;
        _readTool = ToolName.Create("read");

        // Build a custom ruleset of RuleCount rules. All rules target distinct tools
        // with a wildcard pattern so Evaluate must scan the whole array before falling
        // through to the Ask default — except the last rule, which matches the probed tool.
        var rules = new PermissionRule[RuleCount];
        for (int i = 0; i < RuleCount; i++)
        {
            rules[i] = new PermissionRule($"tool-{i}", "*", PermissionAction.Allow);
        }

        rules[RuleCount - 1] = new PermissionRule("read", "*", PermissionAction.Allow);
        _customRuleset = new PermissionRuleset(rules);
    }

    [Benchmark(Description = "Evaluate (Default ruleset, Allow)", Baseline = true)]
    public PermissionAction Evaluate_Default_Allow()
        => _defaultRuleset.Evaluate(_readTool.Value, "README.md");

    [Benchmark(Description = "Evaluate (custom ruleset, Allow at end-of-scan)")]
    public PermissionAction Evaluate_Custom_Allow()
        => _customRuleset.Evaluate(_readTool.Value, "README.md");

    [Benchmark(Description = "Evaluate (Default ruleset, Deny on bash rm -rf /)")]
    public PermissionAction Evaluate_Default_Deny()
        => _defaultRuleset.Evaluate("bash", "rm -rf /");

    [Benchmark(Description = "Evaluate (Default ruleset, Ask fallback)")]
    public PermissionAction Evaluate_Default_Ask()
        => _defaultRuleset.Evaluate("unknown-tool", "anything");
}
