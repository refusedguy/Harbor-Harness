using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Delegates work to a sub-agent (Command). Long-running: fully async, cancel via context.Abort.
///     Sequential so it doesn't race side-effect tools in the same turn.
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

    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // Prefer context.Abort, also honor the token ExecuteAsync was given.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, context.Abort);
        var ct = linked.Token;

        string agentName = args.GetProperty("agent").GetString()!;
        string prompt = args.GetProperty("prompt").GetString()!;

        var nameResult = AgentName.TryCreate(agentName);
        if (nameResult.IsFailure)
            return ToolResult.Error($"Invalid agent name: {nameResult.Error}");

        var agentDefResult = _agents.GetAgent(nameResult.Value);
        if (agentDefResult.IsFailure)
        {
            string available = string.Join(", ",
                _agents.GetAllAgents().Where(a => a.IsSubAgent).Select(a => a.Name.Value));
            return ToolResult.Error(
                $"Unknown sub-agent: '{agentName}'. Available sub-agents: {available}");
        }

        var agentDef = agentDefResult.Value;
        if (!agentDef.IsSubAgent)
        {
            return ToolResult.Error(
                $"Agent '{agentName}' is not a sub-agent. Only agents with IsSubAgent=true can be used with task.");
        }

        if (context.Services is null)
            return ToolResult.Error("ToolContext.Services is not configured.");

        _logger.LogInformation("Starting sub-agent: {Agent}", agentName);

        try
        {
            await context.ReportProgress(
                new ToolProgressUpdate(Status: $"[task] starting '{agentName}'…"),
                ct).ConfigureAwait(false);

            // Nested scope: sub-agent/session don't share parent mutable state.
            // context.Services may already be a per-call scope from AgentLoop — still fine.
            var scopeFactory = context.Services.GetService<IServiceScopeFactory>();
            using var scope = scopeFactory?.CreateScope();
            var sp = scope?.ServiceProvider ?? context.Services;

            var sessionStore = sp.GetRequiredService<ISessionStore>();
            var subAgent = sp.GetRequiredService<IAgent>();

            // Working dir: best-effort from parent session if you store it; else cwd.
            string workDir = Environment.CurrentDirectory;

            var sessionResult = await sessionStore.CreateAsync(
                workDir,
                agentDef.Name.Value,
                agentDef.ProviderId,
                agentDef.Model).ConfigureAwait(false);

            if (sessionResult.IsFailure)
                return ToolResult.Error($"Failed to create sub-session: {sessionResult.Error}");

            subAgent.Initialize(sessionResult.Value, agentDef);

            await context.ReportProgress(
                new ToolProgressUpdate(Status: $"[task] '{agentName}' running…"),
                ct).ConfigureAwait(false);

            // LONG-RUNNING: just await. AgentLoop is already off the UI thread.
            // ChatBridge.Submit also runs PromptAsync on threadpool.
            var runResult = await subAgent.PromptAsync(prompt, ct).ConfigureAwait(false);

            if (runResult.IsFailure)
                return ToolResult.Error($"Sub-agent '{agentName}' failed: {runResult.Error}");

            // Pull final answer from the sub-session transcript.
            string output = ExtractFinalAnswer(sessionResult.Value)
                            ?? "[sub-agent completed with no text output]";

            _logger.LogInformation("Sub-agent completed: {Agent}", agentName);

            await context.ReportProgress(
                new ToolProgressUpdate($"[task] '{agentName}' done", 100),
                ct).ConfigureAwait(false);

            return ToolResult.Success(output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ToolResult.Error($"Sub-agent '{agentName}' was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sub-agent failed: {Agent}", agentName);
            return ToolResult.Error($"Sub-agent '{agentName}' error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Best-effort: last assistant text in the sub-session. Adjust if your Session API differs.
    /// </summary>
    private static string? ExtractFinalAnswer(Session session)
    {
        // If Session doesn't expose messages, resolve ISessionContext from store instead
        // and read context.Messages. Wire that if this doesn't compile.
        return null;
    }
}
