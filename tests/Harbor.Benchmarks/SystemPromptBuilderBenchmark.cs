using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Core.Sessions;
using System.Text.Json;

namespace Harbor.Benchmarks;

/// <summary>
/// Benchmarks <see cref="SystemPromptBuilder.BuildAsync"/>. The builder
/// assembles the system prompt from: base template + environment + agent
/// instructions + tools + skills + MCP + context files, using a pooled
/// <c>StringBuilder</c>.
///
/// The benchmark varies the number of tools and context files to measure
/// how the prompt size scales with input complexity.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SystemPromptBuilderBenchmark
{
    private SystemPromptBuilder _builder = null!;
    private SystemPromptContext _smallContext = null!;
    private SystemPromptContext _largeContext = null!;
    private SystemPromptContext _withSkillsAndContext = null!;

    [Params(0, 4, 8, 16)]
    public int ToolCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _builder = new SystemPromptBuilder();

        var agent = AgentDefinition.CodeDefault("stub-1", "stub");
        var model = new ModelInfo(
            Id: "stub-1",
            ProviderId: "stub",
            DisplayName: "Stub Model",
            ContextWindow: 32_768,
            MaxOutputTokens: 4_096,
            SupportsReasoning: false,
            SupportsVision: false,
            SupportsToolUse: true,
            Pricing: Pricing.Unknown,
            PromptTemplate: "openai");

        var tools = BuildTools(ToolCount);
        _smallContext = new SystemPromptContext(
            Agent: agent, Model: model, Tools: tools,
            ContextFiles: Array.Empty<ContextFile>(),
            Skills: Array.Empty<SkillDescriptor>(),
            McpInstructions: null,
            WorkingDirectory: "/home/user/project");

        _largeContext = new SystemPromptContext(
            Agent: agent with { SystemPromptAppend = LargeAgentInstructions },
            Model: model, Tools: tools,
            ContextFiles: BuildContextFiles(4),
            Skills: Array.Empty<SkillDescriptor>(),
            McpInstructions: "server-a: tools for X\nserver-b: tools for Y",
            WorkingDirectory: "/home/user/project");

        _withSkillsAndContext = new SystemPromptContext(
            Agent: agent, Model: model, Tools: tools,
            ContextFiles: BuildContextFiles(2),
            Skills: BuildSkills(5),
            McpInstructions: null,
            WorkingDirectory: "/home/user/project");
    }

    [Benchmark(Description = "BuildAsync (small context)", Baseline = true)]
    public Task<string> BuildAsync_Small() => _builder.BuildAsync(_smallContext);

    [Benchmark(Description = "BuildAsync (large context + context files)")]
    public Task<string> BuildAsync_Large() => _builder.BuildAsync(_largeContext);

    [Benchmark(Description = "BuildAsync (with skills + context files)")]
    public Task<string> BuildAsync_WithSkills() => _builder.BuildAsync(_withSkillsAndContext);

    private static IReadOnlyList<ToolDescriptor> BuildTools(int count)
    {
        if (count == 0)
        {
            return Array.Empty<ToolDescriptor>();
        }

        var tools = new ToolDescriptor[count];
        for (var i = 0; i < count; i++)
        {
            tools[i] = new ToolDescriptor(
                Name: ToolName.Create($"tool-{i}"),
                DisplayName: $"Tool {i}",
                Description: $"Tool number {i} for benchmarking the system prompt builder with a realistic-length description.",
                Schema: JsonDocument.Parse("{}"),
                ExecutionMode: ExecutionMode.Parallel,
                PromptSnippet: $"Use tool-{i} when you need to perform action {i}.",
                PromptGuidelines: new[]
                {
                    $"Always pass the 'path' argument to tool-{i}.",
                    $"Tool-{i} returns plain text; do not parse it as JSON.",
                });
        }
        return tools;
    }

    private static IReadOnlyList<ContextFile> BuildContextFiles(int count)
    {
        if (count == 0)
        {
            return Array.Empty<ContextFile>();
        }

        var files = new ContextFile[count];
        for (var i = 0; i < count; i++)
        {
            files[i] = new ContextFile(
                Path: $"CLAUDE-{i}.md",
                Content: $"# Project conventions {i}\n\n- Use PascalCase for public members.\n- Use file-scoped namespaces.\n- Prefer records for immutable data.\n- Use Result<T> for error handling.\n");
        }
        return files;
    }

    private static IReadOnlyList<SkillDescriptor> BuildSkills(int count)
    {
        if (count == 0)
        {
            return Array.Empty<SkillDescriptor>();
        }

        var skills = new SkillDescriptor[count];
        for (var i = 0; i < count; i++)
        {
            skills[i] = new SkillDescriptor(
                Name: $"skill-{i}",
                Description: $"Skill {i} provides specialized instructions for task {i}.",
                FilePath: $"/home/user/.harbor/skills/skill-{i}.md");
        }
        return skills;
    }

    private const string LargeAgentInstructions = """
        You are operating in a complex multi-step coding environment.

        Additional rules:
        - Always read files before editing them.
        - Make minimal, targeted edits — never rewrite a whole file when a single line change suffices.
        - Verify changes after editing by re-reading the affected region.
        - Use the bash tool sparingly; prefer dedicated tools when available.
        - When you encounter an error, read the error message carefully and respond minimally.
        - Use `git status` and `git diff` to track your changes.
        - When in doubt about a design decision, ask the user before proceeding.
        - Show file paths clearly when working with files.
        - Prefer immutable data structures (records) over mutable classes.
        - Use `Result<T>` for operations that can fail; do not throw exceptions for expected failures.
        - Always pass a `CancellationToken` to async methods.
        - Use `IAsyncEnumerable<T>` for streaming.
        - Use `FrozenDictionary<TKey, TValue>` for read-only dictionaries built once at startup.
        - Use `ArrayPool<T>.Shared` for rented buffers in hot paths.
        - Use `StringBuilder` (pooled) for concatenation in loops.
        - Use `Channel<T>` for producer-consumer scenarios.
        """;
}
