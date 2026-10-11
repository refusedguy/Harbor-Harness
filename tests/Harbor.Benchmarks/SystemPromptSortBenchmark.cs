using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Sessions;
namespace Harbor.Benchmarks;
/// <summary>
///     Microbenchmark for #1240: pre-sorted frozen prompt inputs vs per-turn
///     sorting. The tools/skills sections used to pay an <c>OrderBy</c> on every
///     turn; the registries now sort once (freeze/load time) and the builder
///     iterates pre-sorted input as-is, falling back to one copy+sort only for
///     unsorted callers.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what these numbers include:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>SystemPromptBuilder.BuildAsync</c> over a
///          tools+skills context, once with pre-sorted (frozen) input and once with
///          reversed (unsorted) input.</item>
///          <item><c>Payload:</c> <c>ToolCount</c> stub tool descriptors (each with a
///          <c>{}</c> schema, a prompt snippet and two guidelines) plus the same count
///          of skills. Names are zero-padded so lexicographic order matches numeric order.</item>
///          <item><c>StateReset:</c> none — the builder is stateless and both contexts are
///          built once in <c>Setup</c>; nothing is reset between iterations. The fallback
///          row allocates its sorted copy per op, which is the cost being measured, not state.</item>
///          <item><c>Drain:</c> none — assembly is synchronous.</item>
///          <item><c>RetainedState:</c> only the immutable context objects built in
///          <c>Setup</c>; the pooled <c>StringBuilder</c> is returned before the op ends.</item>
///          <item><c>AwaitSemantics:</c> both rows await <c>BuildAsync</c>; it completes
///          synchronously on these contexts, so the number is the assembly cost with no
///          scheduling noise.</item>
///          <item><c>AllocAttribution:</c> the final prompt string dominates both rows; the
///          unsorted row additionally pays one descriptor-array copy plus one skills-array
///          copy and two sorts per op — that delta is the pre-sort saving, not noise.</item>
/// </list>
/// <para>
///     <b>Comparability (#408).</b> Both rows render the same descriptor SET, so the
///     prompts must be byte-identical: <c>Setup</c> builds both prompts and
///     <b>throws</b> when they differ, instead of printing an incomparable number.
/// </para>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SystemPromptSortBenchmark
{
    private SystemPromptBuilder _builder = null!;
    private SystemPromptContext _sortedContext = null!;
    private SystemPromptContext _unsortedContext = null!;

    [Params(8, 20)]
    public int ToolCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _builder = new SystemPromptBuilder();

        var agent = AgentDefinition.CodeDefault("stub-1", "stub");
        var model = new ModelInfo(
            "stub-1",
            "stub",
            "Stub Model",
            32_768,
            4_096,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai");

        ToolDescriptor[] sortedTools = BuildTools(ToolCount);
        SkillDescriptor[] sortedSkills = BuildSkills(ToolCount);

        ToolDescriptor[] reversedTools = new ToolDescriptor[sortedTools.Length];
        Array.Copy(sortedTools, reversedTools, sortedTools.Length);
        Array.Reverse(reversedTools);

        SkillDescriptor[] reversedSkills = new SkillDescriptor[sortedSkills.Length];
        Array.Copy(sortedSkills, reversedSkills, sortedSkills.Length);
        Array.Reverse(reversedSkills);

        _sortedContext = new SystemPromptContext(
            agent, model, sortedTools,
            Array.Empty<ContextFile>(),
            sortedSkills,
            null,
            "/home/user/project");

        _unsortedContext = new SystemPromptContext(
            agent, model, reversedTools,
            Array.Empty<ContextFile>(),
            reversedSkills,
            null,
            "/home/user/project");

        string sorted = _builder.BuildAsync(_sortedContext).GetAwaiter().GetResult();
        string unsorted = _builder.BuildAsync(_unsortedContext).GetAwaiter().GetResult();
        if (!string.Equals(sorted, unsorted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SystemPromptSortBenchmark: the sorted and unsorted contexts render different prompts. "
                + "The rows would measure different work, not the sort cost. Fix the fixture.");
        }
    }

    /// <summary>
    ///     Baseline row. Pre-sorted (frozen) input — the warm per-turn path:
    ///     an O(n) sorted probe, then iteration with zero allocation.
    /// </summary>
    [Benchmark(Description = "BuildAsync (pre-sorted tools+skills)", Baseline = true)]
    public Task<string> BuildAsync_PreSorted() => _builder.BuildAsync(_sortedContext);

    /// <summary>
    ///     Comparable with <see cref="BuildAsync_PreSorted" /> on the descriptor set
    ///     (Setup pins byte-identical output): reversed input forces the one
    ///     copy+sort fallback per section — the old per-turn cost.
    /// </summary>
    [Benchmark(Description = "BuildAsync (reversed tools+skills — fallback sort)")]
    public Task<string> BuildAsync_Unsorted() => _builder.BuildAsync(_unsortedContext);

    private static ToolDescriptor[] BuildTools(int count)
    {
        var tools = new ToolDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            string name = $"tool_{i:D2}";
            tools[i] = new ToolDescriptor(
                ToolName.Create(name),
                $"Tool {i}",
                $"Tool number {i} for benchmarking the system prompt builder with a realistic-length description.",
                JsonDocument.Parse("{}"),
                ExecutionMode.Parallel,
                $"Use {name} when you need to perform action {i}.",
                new[]
                {
                    $"Always pass the 'path' argument to {name}.",
                    $"{name} returns plain text; do not parse it as JSON."
                });
        }

        return tools;
    }

    private static SkillDescriptor[] BuildSkills(int count)
    {
        var skills = new SkillDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            string name = $"skill-{i:D2}";
            skills[i] = new SkillDescriptor(
                name,
                $"Skill {i} provides specialized instructions for task {i}.",
                $"/home/user/.harbor/skills/{name}.md");
        }

        return skills;
    }
}
