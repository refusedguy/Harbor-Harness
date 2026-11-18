using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Sessions;

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for the session orchestration
///     failure paths. Internal so the public API freeze stays untouched.
/// </summary>
/// <remarks>
///     Every method closes a branch that used to return an error result or
///     <see langword="false"/> without leaving a trace in the log. Errors are
///     expected-failure results from collaborators, so the text carries the
///     session id and the collaborator's error, never message content or keys.
/// </remarks>
internal static partial class SessionLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "EnsureDefaultSession: session creation failed: {Error}")]
    public static partial void DefaultCreateFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "EnsureDefaultSession: session {SessionId} could not be opened")]
    public static partial void DefaultOpenFailed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "NewSession: creation failed: {Error}")]
    public static partial void NewCreateFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "NewSession: session {SessionId} was created but could not be opened")]
    public static partial void NewOpenFailed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "BranchActive: no active session to branch")]
    public static partial void BranchNoActive(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "BranchActive: branch creation failed: {Error}")]
    public static partial void BranchCreateFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "BranchActive: branch {SessionId} could not be opened")]
    public static partial void BranchOpenFailed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "OpenSession: switch to {SessionId} failed")]
    public static partial void SwitchFailed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Session {SessionId}: message history load failed, hydrating empty: {Error}")]
    public static partial void ReplayFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "DeleteSession {SessionId}: fallback list failed: {Error}")]
    public static partial void DeleteFallbackListFailed(ILogger logger, string sessionId, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "DeleteSession: fallback open of {SessionId} failed")]
    public static partial void DeleteFallbackOpenFailed(ILogger logger, string sessionId);
}
