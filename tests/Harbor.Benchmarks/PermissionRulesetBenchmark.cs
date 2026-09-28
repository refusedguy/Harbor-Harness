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
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>PermissionRuleset.Evaluate</c> call — the decision
///          every tool call makes.</item>
///          <item><c>Payload:</c> the tool name + argument path (<c>("read", "README.md")</c>,
///          <c>("bash", "rm -rf /")</c>, <c>("unknown-tool", "anything")</c>) — a string pair,
///          no JSON document.</item>
///          <item><c>StateReset:</c> none — the default ruleset and a <c>RuleCount</c>-rule
///          custom ruleset are built once in <c>Setup</c>; <c>Evaluate</c> is pure.</item>
///          <item><c>Drain:</c> none — <c>Evaluate</c> is synchronous.</item>
///          <item><c>RetainedState:</c> the pre-sorted rule array inside each ruleset (sorted
///          once in the constructor); no per-call memoisation.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the default-ruleset rows are allocation-free (0 B)
///          — the safety policies short-circuit before any collection is materialized; the 488
///          B on the bash row is the argv pre-walk. A non-zero value on a row is a regression,
///          not overhead.</item>
/// </list>
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
