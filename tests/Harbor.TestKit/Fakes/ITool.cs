using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

/// <summary>Tool that counts executions and records raw args — a canary for permission/timeout tests.</summary>
public sealed class CountingTool : ITool
{
    private readonly object _lock = new();
    private int _executions;

    public int Executions => Volatile.Read(ref _executions);

    public List<string> ExecutedArgs { get; } = [];

    public ToolName Name => ToolName.Create("counter");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => "Counter";

    public string Description => "Counts executions.";

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"n":{"type":"number"}}}""");

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public string? PromptSnippet => null;

    public IReadOnlyList<string> PromptGuidelines => [];

    public Result ValidateArguments(JsonElement args) => Result.Success();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _executions);
        lock (_lock)
        {
            ExecutedArgs.Add(args.GetRawText());
        }

        return Task.FromResult(ToolResult.Success("counted"));
    }
}
