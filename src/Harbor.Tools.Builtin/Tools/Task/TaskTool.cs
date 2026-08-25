using Harbor.Abstractions.Agents;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Sub-agent delegation surface. VALIDATES the requested sub-agent (name,
///     registry membership, IsSubAgent flag) but does NOT run it yet — a real
///     sub-agent runner does not exist in this build, so execution fails with an
///     explicit "not implemented" error instead of fabricating success (G4).
/// </summary>
public sealed class TaskTool : ITool
{
    private readonly IAgentRegistry _agents;
    private readonly ILogger<TaskTool> _logger;

    public TaskTool(IAgentRegistry agents, ILogger<TaskTool> logger)
    {
        _agents = agents;
        _logger = logger;
    }

    public ToolName Name => ToolName.Create("task");
    public string DisplayName => "Task";
    public string Description =>
        "Delegate a task to a sub-agent (currently NOT functional: sub-agent execution is not implemented " +
        "in this build and calls return an error). Reserved for future use.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "task: sub-agent delegation (not implemented in this build — do not call)";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "`task` is not functional yet — never call it; do the work yourself with the available tools"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "agent": {
                                                                            "type": "string",
                                                                            "description": "Name of the sub-agent to use (e.g. 'explore', 'plan')"
                                                                          },
                                                                          "prompt": {
                                                                            "type": "string",
                                                                            "description": "Task description for the sub-agent. Should be self-contained."
                                                                          }
                                                                        },
                                                                        "required": ["agent", "prompt"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("agent", out var agentEl) || agentEl.ValueKind != JsonValueKind.String
                                                           || string.IsNullOrWhiteSpace(agentEl.GetString()))
            return Result.Failure("Missing required argument 'agent'.");

        if (!args.TryGetProperty("prompt", out var promptEl) || promptEl.ValueKind != JsonValueKind.String
                                                             || string.IsNullOrWhiteSpace(promptEl.GetString()))
            return Result.Failure("Missing required argument 'prompt'.");

        return Result.Success();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // Honor the cancellation token without ever producing a ToolResult if cancelled
        // before the (cheap) validation work below. We do not need a linked CTS here
        // because no async I/O is performed — this method is fully synchronous aside
        // from returning a completed Task.
        cancellationToken.ThrowIfCancellationRequested();

        string agentName = args.GetProperty("agent").GetString()!;
        string prompt = args.GetProperty("prompt").GetString()!;

        // ROP-A Z1 п.6: the three hand-threaded guard blocks compose into one
        // railway; each failure keeps its own message at its source, and the
        // available-agents hint is built only on the failure path.
        return Task.FromResult(
            AgentName.TryCreate(agentName)
                .MapError(e => $"Invalid agent name: {e}")
                .Bind(name => _agents.GetAgent(name).MapError(_ =>
                    $"Unknown sub-agent: '{agentName}'. Available sub-agents: " +
                    string.Join(", ", _agents.GetAllAgents()
                        .Where(a => a.IsSubAgent).Select(a => a.Name.Value))))
                .Ensure(a => a.IsSubAgent,
                    $"Agent '{agentName}' is not a sub-agent. Only agents with IsSubAgent=true can be used with task.")
                .Match(
                    _ => NotImplementedResult(agentName, prompt),
                    err => ToolResult.Error(err)));
    }

    private ToolResult NotImplementedResult(string agentName, string prompt)
    {
        // G4: this used to fabricate a Success ("task queued… result merged next
        // turn") while NOTHING was enqueued anywhere — no sub-agent runner exists
        // in the codebase. The model would hallucinate results of a run that never
        // happened. An honest error keeps the contract clean until a real runner lands.
        _logger.LogWarning(
            "Sub-agent execution requested but not implemented: agent={Agent} promptLength={Length}",
            agentName, prompt.Length);
        return ToolResult.Error(
            $"Sub-agent execution is not implemented yet. Agent '{agentName}' exists, but Harbor cannot run sub-agents in this build. Do the work yourself with the available tools instead.");
    }
}
