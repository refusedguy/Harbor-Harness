using Harbor.Abstractions.Agents;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Sub-agent delegation surface. Validates the requested sub-agent (name,
///     registry membership, IsSubAgent flag) and then EXECUTES it through
///     <see cref="ISubAgentRunner" />: the run happens in an isolated session bound to the
///     sub-agent definition, and the sub-run's final assistant text is returned as this
///     tool's output for the parent agent.
/// </summary>
/// <remarks>
///     <para>
///         When no runner is wired (e.g. legacy call sites), execution fails with an
///         explicit error instead of fabricating success — never fake a run that did not
///         happen (G4). Nesting is refused up front via <see cref="ISubAgentRunner.CanSpawn" />
///         so a sub-agent cannot recurse into <c>task</c>.
///     </para>
/// </remarks>
public sealed class TaskTool : ITool
{
    private readonly IAgentRegistry _agents;
    private readonly ILogger<TaskTool> _logger;
    private readonly ISubAgentRunner? _subAgents;

    public TaskTool(IAgentRegistry agents, ILogger<TaskTool> logger, ISubAgentRunner? subAgents = null)
    {
        _agents = agents;
        _logger = logger;
        _subAgents = subAgents;
    }

    public ToolName Name => ToolName.Create("task");
    public string DisplayName => "Task";
    public string Description =>
        "Delegate a self-contained task to a sub-agent (explore, plan, or custom agents marked as sub-agents). " +
        "The sub-agent runs in its own isolated session with its own context window and tool access; " +
        "only its final answer is returned here. Use it for wide read-only reconnaissance or focused planning " +
        "that would otherwise flood your context. The sub-agent cannot spawn further sub-agents.";

    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    public string? PromptSnippet => "task: delegate a self-contained task to a sub-agent and receive its final report";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "`prompt` must be fully self-contained: include file paths, constraints, and exactly what to report back",
        "sub-agents cannot see this conversation and cannot delegate further — do not ask them to call `task`",
        "prefer `task(explore)` for broad code searches, keep small lookups in your own session"
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
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string agentName = args.GetProperty("agent").GetString()!;
        string prompt = args.GetProperty("prompt").GetString()!;

        // ROP-A Z1 п.6: validation composes into one railway; each failure keeps its own
        // message at its source; the available-agents hint is built only on failure paths.
        Result<AgentDefinition> validated = AgentName.TryCreate(agentName)
            .MapError(e => $"Invalid agent name: {e}")
            .Bind(name => _agents.GetAgent(name).MapError(_ =>
                $"Unknown sub-agent: '{agentName}'. Available sub-agents: " +
                string.Join(", ", _agents.GetAllAgents()
                    .Where(a => a.IsSubAgent).Select(a => a.Name.Value))))
            .Ensure(a => a.IsSubAgent,
                $"Agent '{agentName}' is not a sub-agent. Only agents with IsSubAgent=true can be used with task.");

        if (validated.IsFailure)
            return ToolResult.Error(validated.Error);

        if (_subAgents is null)
            return NotImplementedResult(agentName, prompt);

        if (!_subAgents.CanSpawn)
        {
            _logger.LogWarning("Nested 'task' invocation refused: agent={Agent} caller={Caller}",
                agentName, context.Agent);
            return ToolResult.Error(
                "Sub-agents cannot invoke 'task'. Finish your part of the work with your own tools; " +
                "the parent agent will aggregate results.");
        }

        // Surface activity while the sub-run streams elsewhere.
        await context.ReportProgress(
            new ToolProgressUpdate($"Running sub-agent '{agentName}'…"), cancellationToken);

        var result = await _subAgents.RunAsync(
            validated.Value,
            new SubAgentRunRequest(prompt, ParentSessionId: context.SessionId),
            cancellationToken);

        return result.Match(
            run => SuccessResult(run),
            err => ToolResult.Error(err));
    }

    private ToolResult SuccessResult(SubAgentRunResult run)
    {
        _logger.LogInformation(
            "Sub-agent completed: agent={Agent} session={SessionId} messages={Messages} outputChars={Length}",
            run.AgentName, run.SessionId, run.NewMessages, run.FinalOutput.Length);
        return ToolResult.Success($"""
                                   [sub-agent '{run.AgentName}' finished — session {run.SessionId}, {run.NewMessages} message(s)]

                                   {run.FinalOutput}
                                   """);
    }

    private ToolResult NotImplementedResult(string agentName, string prompt)
    {
        // G4: honest failure when the host did not wire a runner — never fabricate a run
        // that did not happen.
        _logger.LogWarning(
            "No sub-agent runner wired: agent={Agent} promptLength={Length}",
            agentName, prompt.Length);
        return ToolResult.Error(
            $"Sub-agent execution is unavailable in this configuration (no runner wired). Do the work yourself with the available tools instead.");
    }
}
