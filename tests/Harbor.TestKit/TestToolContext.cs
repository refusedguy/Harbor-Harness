using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

/// <summary>
///     The ONE place a <see cref="ToolContext" /> is built for tests (#1775).
/// </summary>
/// <remarks>
///     <para>
///         The 8-argument <see cref="ToolContext" /> literal used to be copy-pasted
///         into every test file that exercised a tool — 24 copies in 19 files at
///         the time of this change. Every copy was a hand-maintained contract: a
///         change to the record's signature broke 24 places, and a copy that
///         drifted (a different session id, a missing working directory) was
///         invisible until a test failed for the wrong reason.
///     </para>
///     <para>
///         The defaults reproduce the canonical copy exactly:
///         <c>"test-session"</c> / <c>"test-message"</c> / <c>"test-call"</c> /
///         <c>"code"</c>, no abort token, no messages, a progress callback that
///         drops updates, and a permission callback that ALLOWS WITHOUT
///         PERSISTING. Every argument that ever varied between the copies is a
///         parameter, so no copy loses information by moving here.
///     </para>
/// </remarks>
public static class TestToolContext
{
    private static readonly Func<ToolProgressUpdate, CancellationToken, Task> NoProgress =
        (_, _) => Task.CompletedTask;

    private static readonly Func<PermissionRequest, CancellationToken, Task<PermissionResponse>> Allow =
        (_, _) => Task.FromResult(AllowWithoutPersist());

    /// <summary>
    ///     The permission answer the canonical copy used: allow the call, do not
    ///     persist the decision.
    /// </summary>
    public static PermissionResponse AllowWithoutPersist() => new(PermissionAction.Allow, false);

    /// <summary>
    ///     Builds a <see cref="ToolContext" /> for a test.
    /// </summary>
    /// <param name="sessionId">Session the tool call belongs to.</param>
    /// <param name="messageId">Message the tool call belongs to.</param>
    /// <param name="callId">Tool-call id; <c>null</c> when the caller has none.</param>
    /// <param name="agent">Agent name the call runs under.</param>
    /// <param name="abort">Token that aborts the tool mid-execution.</param>
    /// <param name="messages">Conversation snapshot handed to the tool.</param>
    /// <param name="reportProgress">
    ///     Progress callback; <c>null</c> means "drop every update", which is
    ///     what every copy did.
    /// </param>
    /// <param name="ask">
    ///     Permission callback; <c>null</c> means "allow without persisting",
    ///     which is what every copy did.
    /// </param>
    /// <param name="workingDirectory">
    ///     Directory the tool resolves relative paths against; <c>null</c> falls
    ///     back to the process directory.
    /// </param>
    public static ToolContext Create(
        string sessionId = "test-session",
        string messageId = "test-message",
        string? callId = "test-call",
        string agent = "code",
        CancellationToken abort = default,
        IReadOnlyList<AgentMessage>? messages = null,
        Func<ToolProgressUpdate, CancellationToken, Task>? reportProgress = null,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? ask = null,
        string? workingDirectory = null)
        => new(
            sessionId,
            messageId,
            callId,
            agent,
            abort,
            messages ?? Array.Empty<AgentMessage>(),
            reportProgress ?? NoProgress,
            ask ?? Allow,
            workingDirectory);
}
