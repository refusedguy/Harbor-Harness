using System.Text.Json;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Sessions;

namespace Harbor.Application.Tests.Fakes;

public sealed class CountingTool : ITool
{
    private readonly object _lock = new();
    private int _executions;

    public int Executions => Volatile.Read(ref _executions);

    public List<string> ExecutedArgs { get; } = [];

    public ToolName Name => ToolName.Create("counter");

    public string DisplayName => "Counter";

    public string Description => "Counts executions for red-team lifecycle tests.";

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

public sealed class FakeTokenTracker(bool shouldCompact = false) : ITokenTracker
{
    public void RecordTurnUsage(Usage usage)
    {
    }

    public int Estimate(string text) => 0;

    public int EstimateMessage(AgentMessage message) => 0;

    public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => 0;

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => shouldCompact;

    public TokenStats GetStats() => new(0, 0, null, null, null);
}

public sealed class FakeCompactionService : ICompactionService
{
    private readonly Result<CompactionResult> _outcome =
        Result.Failure<CompactionResult>("simulated compaction failure");

    public int Calls { get; private set; }

    public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) => false;

    public Task<Result<CompactionResult>> CompactAsync(
        string sessionId,
        IReadOnlyList<AgentMessage> messages,
        ModelInfo model,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_outcome);
    }
}

