using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using System.Text.Json;

namespace Harbor.Benchmarks;

/// <summary>
/// Benchmarks <see cref="ToolRegistry"/> hot paths:
/// - <see cref="ToolRegistry.ResolveTools"/>: builds a list of descriptors
///   for an agent. With frozen snapshot, returns a sized array directly.
/// - <see cref="ToolRegistry.GetTool"/>: O(1) lookup by name.
///
/// The frozen path uses <c>FrozenDictionary</c>; the unfrozen path falls back
/// to <c>NonBlocking.ConcurrentDictionary</c>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ToolRegistryBenchmark
{
    private ToolRegistry _frozenRegistry = null!;
    private ToolRegistry _unfrozenRegistry = null!;
    private ToolName _toolName = null!;
    private PermissionRuleset _permission = null!;

    [Params(4, 8, 16)]
    public int ToolCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frozenRegistry = new ToolRegistry();
        _unfrozenRegistry = new ToolRegistry();
        _toolName = ToolName.Create("tool-0");
        _permission = PermissionRuleset.Default;

        for (var i = 0; i < ToolCount; i++)
        {
            var tool = new StubTool($"tool-{i}", $"Tool {i}");
            _frozenRegistry.Register(tool);
            _unfrozenRegistry.Register(tool);
        }

        _frozenRegistry.Freeze();
    }

    [Benchmark(Description = "ResolveTools (frozen, no permission)", Baseline = true)]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Frozen_NoPermission()
        => _frozenRegistry.ResolveTools("code", null);

    [Benchmark(Description = "ResolveTools (frozen, with permission)")]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Frozen_WithPermission()
        => _frozenRegistry.ResolveTools("code", _permission);

    [Benchmark(Description = "ResolveTools (unfrozen)")]
    public IReadOnlyList<ToolDescriptor> ResolveTools_Unfrozen()
        => _unfrozenRegistry.ResolveTools("code", null);

    [Benchmark(Description = "GetTool (frozen)")]
    public Result<ITool> GetTool_Frozen() => _frozenRegistry.GetTool(_toolName);

    [Benchmark(Description = "GetTool (unfrozen)")]
    public Result<ITool> GetTool_Unfrozen() => _unfrozenRegistry.GetTool(_toolName);
}

/// <summary>
/// Minimal stub tool for benchmarking the registry without side effects.
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
    public string DisplayName { get; }
    public string Description { get; }
    public JsonDocument ParameterSchema => Schema;
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => null;
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(ToolResult.Success("ok"));
}
