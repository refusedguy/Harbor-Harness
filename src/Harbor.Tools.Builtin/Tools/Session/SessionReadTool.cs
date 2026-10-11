using System.Text;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Read-only peer-supervision surface (issue #165): inspect a neighboring
///     session's status, terminal run outcome, and recent transcript.
/// </summary>
/// <remarks>
///     Reads a point-in-time snapshot (message count is reported as
///     <c>revision</c>): a <c>working</c> session keeps streaming while the
///     snapshot is rendered, so re-read before acting on one.
/// </remarks>
public sealed class SessionReadTool : ITool
{
    private readonly ISessionStore _sessions;
    private readonly ILogger<SessionReadTool> _logger;

    public SessionReadTool(ISessionStore sessions, ILogger<SessionReadTool> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public ToolName Name => ToolName.Create("session_read");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => "Session Read";

    public string Description =>
        "Inspect a peer session: its status, terminal run outcome, and recent transcript. " +
        "Use it to check whether a neighboring session is ok, stuck, or failed before steering it. " +
        "Read-only: never mutates the target session.";

    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    public string? PromptSnippet => "session_read: inspect a peer session's status, outcome and recent transcript";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "pass a peer session id (see /sessions); never your own",
        "reads are point-in-time snapshots — re-read a working session before acting",
        "verdict first (ok/stuck/failed), steer only when needed"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "id": {
                                                                            "type": "string",
                                                                            "description": "Id of the peer session to inspect"
                                                                          },
                                                                          "limit": {
                                                                            "type": "integer",
                                                                            "description": "Max transcript messages to return (default 20, max 50)"
                                                                          },
                                                                          "include_transcript": {
                                                                            "type": "boolean",
                                                                            "description": "Include the recent transcript (default true)"
                                                                          }
                                                                        },
                                                                        "required": ["id"]
                                                                      }
                                                                      """);

    public Result ValidateArguments(JsonElement args)
    {
        Result<string> id = JsonArgValidator.RequiredString(
            args, "id", "Missing required argument 'id'.");
        if (id.IsFailure)
            return id;

        Result limit = JsonArgValidator.OptionalIntInRange(
            args, "limit", 1, SessionSupervision.MaxTailLimit,
            $"Optional argument 'limit' must be an integer between 1 and {SessionSupervision.MaxTailLimit}.");
        if (limit.IsFailure)
            return limit;

        return JsonArgValidator.OptionalBool(
            args, "include_transcript", "Optional argument 'include_transcript' must be a boolean.");
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string id = args.GetProperty("id").GetString()!;
        int limit = SessionSupervision.DefaultTailLimit;
        if (args.TryGetProperty("limit", out var limitEl)
            && limitEl.ValueKind == JsonValueKind.Number
            && limitEl.TryGetInt32(out int parsed))
        {
            limit = Math.Clamp(parsed, 1, SessionSupervision.MaxTailLimit);
        }

        bool includeTranscript = true;
        if (args.TryGetProperty("include_transcript", out var transcriptEl)
            && (transcriptEl.ValueKind == JsonValueKind.True || transcriptEl.ValueKind == JsonValueKind.False))
        {
            includeTranscript = transcriptEl.ValueKind == JsonValueKind.True;
        }

        Result<Session> target = await _sessions.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (target.IsFailure)
        {
            SessionReadToolLog.UnknownSession(_logger, id, context.SessionId);
            return ToolResult.Error($"Session '{id}' was not found.");
        }

        Result<IReadOnlyList<AgentMessage>> history =
            await _sessions.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        if (history.IsFailure)
        {
            SessionReadToolLog.HistoryUnavailable(_logger, id, history.Error);
            return ToolResult.Error($"Session '{id}' transcript is unavailable: {history.Error}");
        }

        // Snapshot the revision up front: the store may keep appending while we render.
        IReadOnlyList<AgentMessage> snapshot = history.Value;
        int revision = snapshot.Count;

        Session session = target.Value;
        string outcome = SessionSupervision.InferOutcome(snapshot);
        IReadOnlyList<string> steeredBy = SessionSupervision.FindSteerAuthors(snapshot);

        var sb = new StringBuilder(2048);
        sb.Append("[session ").Append(session.Id).AppendLine("]");
        sb.Append("title: ").AppendLine(session.Title);
        sb.Append("agent: ").Append(session.Agent).Append(", model: ").Append(session.ProviderId).Append('/').AppendLine(session.Model);
        sb.Append("status: ").Append(session.Status.ToString().ToLowerInvariant())
            .Append(", outcome: ").Append(outcome)
            .Append(", messages: ").Append(revision).Append(", revision: ").Append(revision).AppendLine();
        sb.Append("tokens: ").Append(session.Metadata.TokensInput).Append("↑ ").Append(session.Metadata.TokensOutput).Append("↓, cost: ");
        // #653: a session whose model publishes no rates has a cost FLOOR, not a
        // total, and "$0.0000" here would tell the model the session was free.
        if (session.Metadata.IsCostKnown)
        {
            sb.Append('$').Append(session.Metadata.Cost.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("unknown (model publishes no price)");
        }

        sb.AppendLine();
        if (steeredBy.Count > 0)
        {
            sb.Append("steered by: ").AppendLine(string.Join(", ", steeredBy));
        }

        if (includeTranscript)
        {
            if (revision == 0)
            {
                sb.AppendLine("(empty transcript)");
            }
            else
            {
                sb.AppendLine("--- transcript (tail) ---");
                sb.Append(SessionSupervision.RenderTail(snapshot, limit));
            }
        }

        return ToolResult.Success(sb.ToString());
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="SessionReadTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SessionReadToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "session_read: unknown session {SessionId} (caller {Caller})")]
    public static partial void UnknownSession(ILogger logger, string sessionId, string caller);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "session_read: history unavailable for {SessionId}: {Error}")]
    public static partial void HistoryUnavailable(ILogger logger, string sessionId, string error);
}
