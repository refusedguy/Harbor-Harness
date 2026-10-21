using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Mutating peer-supervision surface (issue #165): deliver a directive to a
///     neighboring session — <c>message</c> (ping/context/result),
///     <c>redirect</c> (change its task), or <c>restart</c> (re-run with the
///     same context).
/// </summary>
/// <remarks>
///     <para>
///         Approval-gated: the default ruleset maps <c>session_steer</c> to
///         <c>Ask</c>, so the dispatcher prompts the user before execution —
///         the tool itself never bypasses that gate.
///     </para>
///     <para>
///         Guards: no self-steer, and depth-1 (a session cannot steer the
///         supervisor that steered it — detected via the
///         <c>[steer-from:]</c> provenance trailer in the caller's own
///         history). Delivery is durable: the directive is appended to the
///         neighbor's store and picked up on its next run — a live run is
///         never interrupted.
///     </para>
/// </remarks>
[ToolInfo(
    "session_steer",
    "Session Steer",
    "Send a directive to a peer session: message (share context or a result), redirect (change its task), or restart (ask it to re-run with the same context). Requires user approval. The directive lands in the peer's history for its next run; a live run is never interrupted.",
    ExecutionMode = ExecutionMode.Sequential,
    PromptSnippet = "session_steer: send a message, redirect, or restart directive to a peer session (needs approval)",
    PromptGuidelines = new[]
    {
        "read the peer with session_read first — steer only on stuck/failed verdict",
        "message shares context, redirect changes its task, restart re-runs it",
        "never steer your own supervisor (the session that steered you)"
    })]
[ToolSchema]
[ToolParam("id", "string", Description = "Id of the peer session to steer", Required = true)]
[ToolParam("instruction", "string", Description = "The directive for the peer session", Required = true)]
[ToolParam("operation", "string", Enum = new[] { "message", "redirect", "restart" }, Description = "Peer operation (default message)")]
public sealed partial class SessionSteerTool : ITool
{
    private readonly ISessionStore _sessions;
    private readonly ILogger<SessionSteerTool> _logger;

    public SessionSteerTool(ISessionStore sessions, ILogger<SessionSteerTool> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public Result ValidateArguments(JsonElement args)
    {
        Result<string> id = JsonArgValidator.RequiredString(
            args, "id", "Missing required argument 'id'.");
        if (id.IsFailure)
            return id;

        Result<string> instruction = JsonArgValidator.RequiredString(
            args, "instruction", "Missing required argument 'instruction'.");
        if (instruction.IsFailure)
            return instruction;

        if (args.TryGetProperty("operation", out var opEl))
        {
            if (opEl.ValueKind != JsonValueKind.String
                || !SessionSupervision.IsKnownOperation(opEl.GetString() ?? string.Empty))
            {
                return Result.Failure("Optional argument 'operation' must be one of: message, redirect, restart.");
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

        string id = args.GetProperty("id").GetString()!;
        string instruction = args.GetProperty("instruction").GetString()!;
        string operation = SessionSupervision.OpMessage;
        if (args.TryGetProperty("operation", out var opEl)
            && opEl.ValueKind == JsonValueKind.String
            && SessionSupervision.IsKnownOperation(opEl.GetString() ?? string.Empty))
        {
            operation = opEl.GetString()!;
        }

        if (string.Equals(id, context.SessionId, StringComparison.Ordinal))
        {
            return ToolResult.Error("Cannot steer your own session. Do the work with your own tools instead.");
        }

        IReadOnlyList<string> supervisors = SessionSupervision.FindSteerAuthors(context.Messages);
        for (int i = 0; i < supervisors.Count; i++)
        {
            if (string.Equals(supervisors[i], id, StringComparison.Ordinal))
            {
                SessionSteerToolLog.RefusedSupervisorSteer(_logger, context.SessionId, id);
                return ToolResult.Error(
                    $"Cannot steer session '{id}': it is your supervisor (it steered you) — supervision depth is 1.");
            }
        }

        Result<Session> target = await _sessions.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (target.IsFailure)
        {
            SessionSteerToolLog.UnknownSession(_logger, id, context.SessionId);
            return ToolResult.Error($"Session '{id}' was not found.");
        }

        Session peer = target.Value;
        var steer = new UserMessage(
            Guid.NewGuid().ToString("N"),
            peer.Id,
            DateTimeOffset.UtcNow,
            SessionSupervision.BuildSteerContent(context.SessionId, operation, instruction),
            peer.Agent,
            peer.Model);

        Result appended = await _sessions.AppendMessageAsync(peer.Id, steer, cancellationToken).ConfigureAwait(false);
        if (appended.IsFailure)
        {
            SessionSteerToolLog.AppendFailed(_logger, peer.Id, appended.Error);
            return ToolResult.Error($"Failed to steer session '{id}': {appended.Error}");
        }

        SessionSteerToolLog.Steered(_logger, operation, context.SessionId, peer.Id);

        string pickup = peer.Status == SessionStatus.Working
            ? "It is working now: the directive is queued in its history and applies at its next run — the live run is not interrupted."
            : "It will pick the directive up on its next run.";
        string restartNote = operation == SessionSupervision.OpRestart
            ? " Restart re-uses the session's existing context; nothing was aborted."
            : string.Empty;

        return ToolResult.Success(
            $"[{operation} delivered to session '{peer.Id}' ({peer.Title})] {pickup}{restartNote}");
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="SessionSteerTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SessionSteerToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "session_steer refused: {Caller} tried to steer its own supervisor {Target}")]
    public static partial void RefusedSupervisorSteer(ILogger logger, string caller, string target);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "session_steer: unknown session {SessionId} (caller {Caller})")]
    public static partial void UnknownSession(ILogger logger, string sessionId, string caller);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "session_steer: append to {SessionId} failed: {Error}")]
    public static partial void AppendFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "session_steer: {Operation} from {Caller} to {Target}")]
    public static partial void Steered(ILogger logger, string operation, string caller, string target);
}
