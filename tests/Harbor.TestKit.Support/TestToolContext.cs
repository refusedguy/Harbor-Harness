using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;

namespace Harbor.TestKit;

/// <summary>
///     The ONE place a <see cref="ToolContext" /> is built for tests (#1775).
/// </summary>
/// <remarks>
///     <para>
///         The 8-argument <see cref="ToolContext" /> literal used to be
///         copy-pasted into every test file that exercised a tool. Measured on
///         the tree this factory replaced, there were <b>39 such constructions
///         in 34 files</b> — 352 lines. Every copy was a hand-maintained
///         contract: a change to the record's signature broke all of them at
///         once, and a copy that drifted (a different session id, a missing
///         working directory) was invisible until a test failed for the wrong
///         reason.
///     </para>
///     <para>
///         The defaults reproduce the canonical copy exactly:
///         <c>"test-session"</c> / <c>"test-message"</c> / <c>"test-call"</c> /
///         <c>"code"</c>, no abort token, no messages, a progress callback that
///         drops updates, and a permission callback that ALLOWS WITHOUT
///         PERSISTING. Every argument that ever varied between the copies is a
///         parameter, so no copy loses information by moving here.
///     </para>
///     <para>
///         WHY THIS FILE IS IN THE SUPPORT PART, NOT IN THE CORE
///         ------------------------------------------------------
///         A <see cref="ToolContext" /> needs <c>Harbor.Abstractions</c> (the
///         record itself and <see cref="ToolProgressUpdate" />) and
///         <c>Harbor.Abstractions.Contracts</c> (<see cref="AgentMessage" />,
///         <see cref="PermissionRequest" />, <see cref="PermissionResponse" />,
///         <see cref="PermissionAction" />). Both are contract assemblies every
///         test project already has, and both are already in this part's
///         closure — so the factory here adds no product assembly to anybody.
///     </para>
///     <para>
///         That is what the two projects that still build a
///         <see cref="ToolContext" /> by hand and do NOT reference the core kit
///         need. The core kit is a superset of this part, so a factory left in
///         the core would have cost each of them a reference the part does not
///         need, and the core's own closure on top. The third hand-built site
///         lives in a project that references BOTH parts, so it reaches the
///         factory either way. The move is source-compatible for the nineteen
///         files that were already migrated: the core references this part,
///         the namespace is <c>Harbor.TestKit</c> in both, and the type name
///         did not change, so <c>using Harbor.TestKit;</c> still finds it.
///     </para>
///     <para>
///         The tree now holds one construction of a <see cref="ToolContext" />
///         outside this file, and it is the one in
///         <c>tests/Harbor.TestKit.Tests/TestToolContextTests.cs</c> — the test
///         that holds this factory against the literal it replaced. The
///         measured chain is <b>39 (34 files, 352 lines) -&gt; 9 (9 files, 81
///         lines) -&gt; 1</b>, and the permission answer the copies spelled out
///         went <b>59 -&gt; 7</b> <c>new PermissionResponse(PermissionAction.X,
///         false)</c> literals.
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
    ///     The other answer the tree spells out: deny the call, do not persist
    ///     the decision.
    /// </summary>
    /// <remarks>
    ///     The plugin suites use it to give a sandboxed tool a context whose
    ///     permission callback refuses the call. The persist flag is
    ///     <see langword="false" /> because none of those tests writes a
    ///     decision back to a ruleset — the answer is a fixture, not a policy.
    /// </remarks>
    public static PermissionResponse DenyWithoutPersist() => new(PermissionAction.Deny, false);

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
