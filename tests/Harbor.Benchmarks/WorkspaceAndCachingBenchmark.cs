using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Sessions;

namespace Harbor.Benchmarks;

/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one workspace scan — <c>LoadContextFiles</c>,
///          <c>LoadSkills</c>, or a <c>GetOrLoadCached</c> hit/miss — over a prepared
///          directory.</item>
///          <item><c>Payload:</c> prepared temp trees: empty, 25 KB of context files, and a
///          5-skill <c>.harbor/skills</c> tree, built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> once per class, not per iteration — the trees are created
///          in <c>Setup</c> and the caches are pre-warmed there; <c>Cleanup</c> invalidates
///          every cache entry and deletes the root.</item>
///          <item><c>Drain:</c> none — loading is synchronous directory I/O.</item>
///          <item><c>RetainedState:</c> <b>the process-wide <c>WorkspaceContextSource</c>
///          cache</b> is the thing under test: <c>GetOrLoadCached_Hit</c> only hits because it
///          is left warm, so it is deliberately not invalidated between iterations.
///          <c>GetOrLoadCached_Miss</c> invalidates its own directory inside the measured
///          op.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the parsed
///          <c>ContextFile</c>/<c>SkillDescriptor</c> lists on the load rows; the cache-hit
///          row allocates nothing but the returned references.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class WorkspaceContextSourceBenchmark
{
    private string _emptyDir = null!;
    private string _withFilesDir = null!;
    private string _emptySkillsDir = null!;
    private string _with5SkillsDir = null!;
    private string _hitDir = null!;
    private string _missDir = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = Path.Combine(Path.GetTempPath(), "harbor-bench-ws-" + Guid.NewGuid().ToString("N"));
        _emptyDir = Path.Combine(root, "empty");
        _withFilesDir = Path.Combine(root, "withFiles");
        _emptySkillsDir = Path.Combine(root, "emptySkills");
        _with5SkillsDir = Path.Combine(root, "with5Skills");
        _hitDir = Path.Combine(root, "hit");
        _missDir = Path.Combine(root, "miss");

        Directory.CreateDirectory(_emptyDir);
        Directory.CreateDirectory(_withFilesDir);
        Directory.CreateDirectory(Path.Combine(_emptySkillsDir, ".harbor", "skills"));
        Directory.CreateDirectory(Path.Combine(_with5SkillsDir, ".harbor", "skills"));
        Directory.CreateDirectory(_hitDir);
        Directory.CreateDirectory(_missDir);

        // withFiles: AGENTS.md 20KB + CLAUDE.md 5KB
        File.WriteAllText(Path.Combine(_withFilesDir, "AGENTS.md"), new string('A', 20 * 1024));
        File.WriteAllText(Path.Combine(_withFilesDir, "CLAUDE.md"), new string('C', 5 * 1024));

        // with5Skills: 5 *.md files with frontmatter
        string with5SkillsSub = Path.Combine(_with5SkillsDir, ".harbor", "skills");
        for (int i = 0; i < 5; i++)
        {
            string content = $$"""
                ---
                description: Skill {{i}} does something useful for benchmarking the workspace loader.
                ---
                # Skill {{i}}

                Body of skill {{i}} with some extra prose to make the file realistic.
                """;
            File.WriteAllText(Path.Combine(with5SkillsSub, $"skill-{i}.md"), content);
        }

        // hit/miss dirs: realistic content so cached vs uncached delta is meaningful
        foreach (string dir in new[] { _hitDir, _missDir })
        {
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), new string('A', 20 * 1024));
            File.WriteAllText(Path.Combine(dir, "CLAUDE.md"), new string('C', 5 * 1024));
            string skillsSub = Path.Combine(dir, ".harbor", "skills");
            Directory.CreateDirectory(skillsSub);
            for (int i = 0; i < 5; i++)
            {
                string content = $$"""
                    ---
                    description: Skill {{i}} for hit/miss benchmark.
                    ---
                    # Skill {{i}}
                    Extra content {{i}}.
                    """;
                File.WriteAllText(Path.Combine(skillsSub, $"skill-{i}.md"), content);
            }
        }

        // Pre-warm hit dir cache
        WorkspaceContextSource.GetOrLoadCached(_hitDir);
        // Pre-warm miss dir once so Invalidate+GetOrLoadCached in benchmark exercises miss path cleanly
        WorkspaceContextSource.GetOrLoadCached(_missDir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (string dir in new[] { _emptyDir, _withFilesDir, _emptySkillsDir, _with5SkillsDir, _hitDir, _missDir })
        {
            WorkspaceContextSource.Invalidate(dir);
        }

        try
        {
            string? root = _emptyDir != null ? Path.GetDirectoryName(_emptyDir) : null;
            if (root != null && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Benchmark(Description = "LoadContextFiles_EmptyDir", Baseline = true)]
    public IReadOnlyList<ContextFile> LoadContextFiles_EmptyDir() =>
        WorkspaceContextSource.LoadContextFiles(_emptyDir);

    [Benchmark(Description = "LoadContextFiles_WithFiles")]
    public IReadOnlyList<ContextFile> LoadContextFiles_WithFiles() =>
        WorkspaceContextSource.LoadContextFiles(_withFilesDir);

    [Benchmark(Description = "LoadSkills_Empty")]
    public IReadOnlyList<SkillDescriptor> LoadSkills_Empty() =>
        WorkspaceContextSource.LoadSkills(_emptySkillsDir, globalSkillsDir: null);

    [Benchmark(Description = "LoadSkills_With5Skills")]
    public IReadOnlyList<SkillDescriptor> LoadSkills_With5Skills() =>
        WorkspaceContextSource.LoadSkills(_with5SkillsDir, globalSkillsDir: null);

    [Benchmark(Description = "GetOrLoadCached_Hit")]
    public (IReadOnlyList<ContextFile> Files, IReadOnlyList<SkillDescriptor> Skills) GetOrLoadCached_Hit() =>
        WorkspaceContextSource.GetOrLoadCached(_hitDir);

    [Benchmark(Description = "GetOrLoadCached_Miss")]
    public (IReadOnlyList<ContextFile> Files, IReadOnlyList<SkillDescriptor> Skills) GetOrLoadCached_Miss()
    {
        WorkspaceContextSource.Invalidate(_missDir);
        return WorkspaceContextSource.GetOrLoadCached(_missDir);
    }
}

/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one <c>CachingSystemPromptBuilder.BuildAsync</c> that hits
///          the cache, or one that misses on a freshly minted context.</item>
///          <item><c>Payload:</c> <c>ToolCount</c> (4 / 16) stub tool descriptors plus two
///          context files and two skills, all sharing one pre-built <c>JsonDocument</c> so the
///          static schema-text cache stays at one entry.</item>
///          <item><c>StateReset:</c> per iteration — <c>ResetBuilder</c>
///          (<c>[IterationSetup]</c>) drops the previous cache dictionary and rebuilds the hit
///          context, then re-warms the hit key. Without it the miss row's unique keys would
///          grow the cache without bound across iterations.</item>
///          <item><c>Drain:</c> none — prompt assembly is synchronous (no MCP, no tool
///          execution).</item>
///          <item><c>RetainedState:</c> the per-instance prompt cache, re-created per
///          iteration on purpose so every iteration measures the same cache size; the hit key
///          is re-warmed inside the iteration.</item>
///          <item><c>AwaitSemantics:</c> every row awaits <c>BuildAsync</c>; on these contexts
///          it completes synchronously.</item>
///          <item><c>AllocAttribution:</c> the key computation (a 512-char
///          <c>StringBuilder</c> + SHA-256) dominates the hit row; the miss row adds the
///          assembled prompt.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class CachingPromptBenchmark
{
    private static readonly JsonDocument SharedSchema = JsonDocument.Parse("{}");

    private CachingSystemPromptBuilder _caching = null!;
    private SystemPromptContext _hitContext = null!;
    private int _missCounter;

    [Params(4, 16)]
    public int ToolCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _hitContext = CreateContext(ToolCount, "/tmp/harbor-bench-hit");
        _missCounter = 0;
        ResetBuilder();
    }

    [IterationSetup]
    public void ResetBuilder()
    {
        // Build_Miss mints a unique context key on every call, so a long-lived
        // cache dictionary would grow one entry per invocation and the later
        // iterations would not measure the same cache size as the earlier ones.
        // Drop it per iteration and re-warm the hit key inside the same step.
        _caching = new CachingSystemPromptBuilder(new SystemPromptBuilder());
        _caching.BuildAsync(_hitContext).GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void ReleaseBuilder()
    {
        // Nothing disposable here — the point is to make the "no cache entry
        // survives an iteration" contract explicit at the cleanup hook instead
        // of implied by the reset above.
        _caching = null!;
    }

    [Benchmark(Description = "Build_Hit", Baseline = true)]
    public Task<string> Build_Hit() => _caching.BuildAsync(_hitContext);

    [Benchmark(Description = "Build_Miss")]
    public Task<string> Build_Miss()
    {
        int n = Interlocked.Increment(ref _missCounter);
        var ctx = CreateContext(ToolCount, "/tmp/harbor-bench-miss-" + n);
        return _caching.BuildAsync(ctx);
    }

    private static SystemPromptContext CreateContext(int toolCount, string workingDirectory)
    {
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

        var tools = BuildTools(toolCount);
        var files = new[]
        {
            new ContextFile("AGENTS.md", new string('A', 1024)),
            new ContextFile("CLAUDE.md", new string('C', 512)),
        };
        var skills = new[]
        {
            new SkillDescriptor("skill-0", "Skill 0 description", "/tmp/.harbor/skills/skill-0.md"),
            new SkillDescriptor("skill-1", "Skill 1 description", "/tmp/.harbor/skills/skill-1.md"),
        };

        return new SystemPromptContext(agent, model, tools, files, skills, null, workingDirectory);
    }

    private static IReadOnlyList<ToolDescriptor> BuildTools(int count)
    {
        if (count == 0)
            return Array.Empty<ToolDescriptor>();

        var tools = new ToolDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            tools[i] = new ToolDescriptor(
                ToolName.Create($"tool_{i}"),
                $"Tool {i}",
                $"Tool number {i} for benchmarking the caching prompt builder.",
                SharedSchema,
                ExecutionMode.Parallel,
                $"Use tool_{i} when you need action {i}.",
                new[]
                {
                    $"Always pass the 'path' argument to tool_{i}.",
                    $"tool_{i} returns plain text; do not parse it as JSON."
                });
        }

        return tools;
    }
}
