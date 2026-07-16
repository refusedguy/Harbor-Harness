using Harbor.Abstractions.Agents;
namespace Harbor.Tools.Builtin;
/// <summary>
///     Delegates work to a sub-agent. Implements Command pattern (GOF).
///     Sub-agents run in their own context with limited permissions and tools.
/// </summary>
public sealed class TaskTool : ITool
{
    private readonly IAgentRegistry _agents;

    public TaskTool(IAgentRegistry agents)
    {
        _agents = agents;
    }

    public ToolName Name => ToolName.Create("task");
    public string DisplayName => "Task";
    public string Description => "Delegate a task to a sub-agent. The sub-agent runs in its own context with limited permissions. Use this to parallelize work, isolate file operations, or use specialized agents like 'explore'.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "task: Delegate to a sub-agent (e.g. explore, plan, code)";

    public IReadOnlyList<string> PromptGuidelines { get; } = new[]
    {
        "Use `task` for sub-tasks that should run in isolation",
        "Common sub-agents: `explore` (fast read-only codebase exploration), `plan` (read-only planning)",
        "Sub-agents have their own context window — they don't see this conversation",
        "Provide a clear, self-contained prompt to the sub-agent"
    };

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
        if (!args.TryGetProperty("agent", out var agentEl) || agentEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'agent'.");
        if (!args.TryGetProperty("prompt", out var promptEl) || promptEl.ValueKind != JsonValueKind.String)
            return Result.Failure("Missing required argument 'prompt'.");
        return Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string agentName = args.GetProperty("agent").GetString()!;
        string prompt = args.GetProperty("prompt").GetString()!;

        var nameResult = AgentName.TryCreate(agentName);
        if (nameResult.IsFailure)
            return ToolResult.Error($"Invalid agent name: {nameResult.Error}");

        var agentDef = _agents.GetAgent(nameResult.Value);
        if (agentDef.IsFailure)
        {
            string available = string.Join(", ", _agents.GetAllAgents().Where(a => a.IsSubAgent).Select(a => a.Name.Value));
            return ToolResult.Error($"Unknown sub-agent: '{agentName}'. Available sub-agents: {available}");
        }

        if (!agentDef.Value.IsSubAgent)
        {
            return ToolResult.Error(
                $"Agent '{agentName}' is not a sub-agent. Only agents marked IsSubAgent=true can be used with task tool.");
        }

        // Note: actual sub-agent invocation requires access to IAgent instance.
        // This is a placeholder — in production, TaskTool would receive IAgent via ToolContext.Services
        // and invoke it with the prompt, returning the final result.
        return ToolResult.Success(
            $"[Sub-agent '{agentName}' queued for prompt: {prompt[..Math.Min(100, prompt.Length)]}...]\n" +
            $"Note: Sub-agent execution requires wiring through ToolContext.Services.GetRequiredService<IAgent>(). " +
            $"See TaskTool.cs for implementation details.");
    }
}
