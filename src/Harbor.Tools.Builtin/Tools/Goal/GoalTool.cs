// GoalTool.cs — the model-facing channel of the goal axis (#1761).
//
// The agent is the only party that can say "the objective is met" or "I
// cannot get there from here". This tool is that channel: it records the
// agent's verdict on the session's durable goal, and the round driver reads
// the verdict back at the next quiescence point.
//
// WHY A TOOL AND NOT A PARAGRAPH
// ------------------------------
// The harness does not take the agent's word for a change (ADR-020: "the
// agent's prose is not an input"). The same rule holds for the agent's word
// about its own progress: "I have finished the task" in an assistant message
// is a CLAIM. A tool call is an ACTION — it goes through the tool
// dispatcher, it is recorded in the transcript, and it moves a durable
// record. The verdict is therefore the same kind of fact as the rest of the
// session's state: readable, dated, and auditable.
//
// WHAT THE TOOL DOES NOT DO
// -------------------------
// It does not START a goal. The objective is the user's, not the model's;
// a model that could set its own objective would be marking its own
// homework. It also does not RESUME one: the goal's phase moves to complete
// or blocked, and the round driver stops there.

using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Reports the agent's verdict on the session's goal: the objective was
///     met, or it cannot be reached from here (#1761).
/// </summary>
[ToolInfo(
    "goal",
    "Goal",
    "Report the outcome of the session's goal. Use operation \"complete\" when the objective is actually met and verified, and operation \"block\" with a reason when you cannot proceed and know what is missing. Do not call it while the objective is still open.",
    PromptSnippet = "goal: report the goal as complete (with evidence) or blocked (with the reason)",
    PromptGuidelines = new[]
    {
        "call goal with operation \"complete\" only after you verified the objective",
        "call goal with operation \"block\" and a reason when something is missing",
        "the goal's phase is durable — say what is true, not what would look good"
    },
    ExecutionMode = ExecutionMode.Sequential)]
public sealed partial class GoalTool : ITool
{
    /// <summary>
    ///     The operation that records the goal as met.
    /// </summary>
    internal const string CompleteOperation = "complete";

    /// <summary>
    ///     The operation that records the goal as blocked.
    /// </summary>
    internal const string BlockOperation = "block";

    /// <summary>
    ///     The goal store, or null when the session backend keeps no goals.
    /// </summary>
    private readonly ISessionGoalStore? _goals;

    /// <summary>
    ///     The tool's logger.
    /// </summary>
    private readonly ILogger<GoalTool> _logger;

    /// <summary>
    ///     Construct the tool.
    /// </summary>
    /// <param name="goals">
    ///     The goal store of the session backend, or null when the backend
    ///     keeps no goals — the tool then says so instead of pretending.
    /// </param>
    /// <param name="logger">The tool's logger.</param>
    public GoalTool(ISessionGoalStore? goals, ILogger<GoalTool> logger)
    {
        _goals = goals;
        _logger = logger;
    }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <summary>
    ///     The tool's parameter schema. Parsed once per process (#1574) — the JSON
    ///     below is a compile-time constant, so re-parsing it for every tool
    ///     instance was pure allocation churn.
    /// </summary>
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "operation": {
                                                                            "type": "string",
                                                                            "enum": ["complete", "block"],
                                                                            "description": "complete — the objective was met; block — it cannot be reached from here"
                                                                          },
                                                                          "reason": {
                                                                            "type": "string",
                                                                            "description": "Why the goal is blocked (required for operation \"block\")"
                                                                          }
                                                                        },
                                                                        "required": ["operation"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public JsonDocument ParameterSchema => Schema;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<string> operation = JsonArgValidator.RequiredEnum(
            args,
            "operation",
            "Missing required argument 'operation'.",
            new[] { CompleteOperation, BlockOperation },
            value => $"Argument 'operation' must be '{CompleteOperation}' or '{BlockOperation}', not '{value}'.");
        if (operation.IsFailure)
        {
            return operation;
        }

        if (string.Equals(operation.Value, BlockOperation, StringComparison.Ordinal))
        {
            // A block without a reason is a shrug. The reason is the whole
            // point of the phase: it is what the user reads to decide whether
            // to unblock, escalate, or give up.
            Result<string> reason = JsonArgValidator.RequiredString(
                args, "reason", "Argument 'reason' is required when 'operation' is 'block'.");
            if (reason.IsFailure)
            {
                return reason;
            }
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A backend that keeps no goals has no goal to report on. Saying so is
        // the honest answer: the alternative — pretending the goal is
        // complete — would put a phase in the record that was never set.
        if (_goals is null)
        {
            return ToolResult.Error("This session backend keeps no goals.");
        }

        ISessionGoalStore goals = _goals;

        string operation = args.GetProperty("operation").GetString() ?? CompleteOperation;
        string? reason = args.TryGetProperty("reason", out var reasonEl)
                         && reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString()
            : null;

        // The read and the write are a read-modify-write, so the write may be
        // refused as stale: another writer (the round driver) changed the goal
        // between the two. The refusal is not an error — the goal it wrote is
        // newer than the one this call read, so the call re-reads and tries
        // once more against the newer revision.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<Maybe<Goal>> read = await goals
                .GetGoalAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure)
            {
                GoalToolLog.GoalReadFailed(_logger, context.SessionId, read.Error);
                return ToolResult.Error($"The goal of session '{context.SessionId}' could not be read: {read.Error}");
            }

            if (read.Value.HasNoValue)
            {
                return ToolResult.Error($"Session '{context.SessionId}' has no goal.");
            }

            Goal goal = read.Value.Value;
            if (goal.Phase is not GoalPhase.Active)
            {
                return ToolResult.Error(
                    $"The goal of session '{context.SessionId}' is already {goal.Phase.ToString().ToLowerInvariant()}.");
            }

            bool completing = string.Equals(operation, CompleteOperation, StringComparison.Ordinal);
            Goal updated = goal with
            {
                Phase = completing ? GoalPhase.Complete : GoalPhase.Blocked,
                BlockedCode = completing ? null : GoalBlockCode.AgentReported,
                BlockedReason = completing ? null : reason,
                Revision = goal.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            Result<Goal> appended = await goals
                .AppendGoalAsync(context.SessionId, updated, cancellationToken).ConfigureAwait(false);
            if (appended.IsFailure)
            {
                GoalToolLog.GoalWriteRefused(_logger, context.SessionId, appended.Error);
                continue;
            }

            return completing
                ? ToolResult.Success(
                    $"The goal of session '{context.SessionId}' is recorded as complete.")
                : ToolResult.Success(
                    $"The goal of session '{context.SessionId}' is recorded as blocked: {reason}");
        }

        return ToolResult.Error(
            $"The goal of session '{context.SessionId}' kept changing while it was being recorded; try again.");
    }
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="GoalTool" />.
/// </summary>
internal static partial class GoalToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "goal tool: reading the goal of session {SessionId} failed: {Error}")]
    public static partial void GoalReadFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "goal tool: writing the goal of session {SessionId} was refused: {Error}")]
    public static partial void GoalWriteRefused(ILogger logger, string sessionId, string error);
}
