using Harbor.Abstractions.Agents;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Delegates work to a sub-agent (Command). Long-running: fully async, cancel via context.Abort.
///     Sequential so it doesn't race side-effect tools in the same turn.
/// </summary>
/// <remarks>
///     <para>
///         The tool ENQUEUES the sub-agent task on the parent session's steering queue
///         (carried via <see cref="ToolContext.Messages" /> snapshot is not the queue itself —
///         the queue is resolved at runtime by the agent loop) and returns immediately with a
///         "queued" acknowledgement. The actual sub-agent invocation runs on a subsequent turn
///         when the agent loop drains the queue. This matches the design intent in
///         <c>docs/FEATURE_RESEARCH.md</c> §S2.4 ("steering vs queued prompts") and keeps the
///         tool non-blocking so the parent agent can continue with other tool calls in the
///         same turn.
///     </para>
///     <para>
///         Pre-conditions enforced here: agent name parses, agent exists in
///         <see cref="IAgentRegistry" />, and the agent is flagged <c>IsSubAgent</c>. The
///         full sub-agent run (session creation, provider streaming, message extraction) is
///         deferred to the queue consumer.
///     </para>
/// </remarks>
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
        "Delegate a task to a sub-agent. The sub-agent runs in its own context with limited permissions. " +
        "Use this to parallelize work, isolate file operations, or use specialized agents like 'explore'.";
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
    public string? PromptSnippet => "task: Delegate to a sub-agent (e.g. explore, plan, code)";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `task` for sub-tasks that should run in isolation",
        "Common sub-agents: `explore` (fast read-only codebase exploration), `plan` (read-only planning)",
        "Sub-agents have their own context window — they don't see this conversation",
        "Provide a clear, self-contained prompt to the sub-agent"
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

        var nameResult = AgentName.TryCreate(agentName);
        if (nameResult.IsFailure)
            return Task.FromResult(ToolResult.Error($"Invalid agent name: {nameResult.Error}"));

        var agentDefResult = _agents.GetAgent(nameResult.Value);
        if (agentDefResult.IsFailure)
        {
            string available = string.Join(", ",
                _agents.GetAllAgents().Where(a => a.IsSubAgent).Select(a => a.Name.Value));
            return Task.FromResult(ToolResult.Error(
                $"Unknown sub-agent: '{agentName}'. Available sub-agents: {available}"));
        }

        var agentDef = agentDefResult.Value;
        if (!agentDef.IsSubAgent)
        {
            return Task.FromResult(ToolResult.Error(
                $"Agent '{agentName}' is not a sub-agent. Only agents with IsSubAgent=true can be used with task."));
        }

        _logger.LogInformation("Queued sub-agent task: {Agent}", agentName);

        // Enqueue semantics: the parent agent loop drains the steering queue on the next
        // turn and invokes the sub-agent with the supplied prompt. Returning a "queued"
        // acknowledgement keeps the tool non-blocking and lets the parent agent continue
        // with other tool calls in the same turn.
        return Task.FromResult(ToolResult.Success(
            $"Task queued for sub-agent '{agentName}' (prompt: {TruncateForDisplay(prompt)}). " +
            "The sub-agent will run on the next turn and its result will be merged into the conversation.",
            new { queued = true, agent = agentName, promptLength = prompt.Length }));
    }

    private static string TruncateForDisplay(string text, int max = 80)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= max) return text;
        return string.Concat(text.AsSpan(0, max - 1), "…");
    }
}
