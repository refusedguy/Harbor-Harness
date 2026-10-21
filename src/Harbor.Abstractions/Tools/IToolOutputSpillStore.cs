namespace Harbor.Abstractions.Tools;

/// <summary>
///     Where an oversized tool output went, and how big it is (issue #1748).
/// </summary>
/// <param name="Path">
///     The absolute path of the spill file. It is the LOCATOR the model receives:
///     a pointer into Harbor's private spill area, never a path the model chose.
/// </param>
/// <param name="SizeBytes">The size of the spill file on disk, in bytes.</param>
/// <param name="CharCount">The size of the spilled text, in characters.</param>
/// <remarks>
///     <para>
///         The pointer is a VALUE, not a handle: it names a file that outlives
///         the process, so a model that received one can read the output back
///         after a restart. Nothing in it is derived from model input — the
///         path is composed by the store from the session id and the tool call
///         id, and both are sanitised before they touch the filesystem.
///     </para>
/// </remarks>
public sealed record ToolOutputSpill(string Path, long SizeBytes, int CharCount);

/// <summary>
///     The spill area for tool outputs that do not fit the model's context
///     (issue #1748).
/// </summary>
/// <remarks>
///     <para>
///         <b>What this is for.</b> A tool result larger than the inline budget
///         used to be cut at the context boundary, and the marker told the model
///         the tail was "retained in session log" — a place the model has no
///         path to and no pointer to. The information was simply gone from the
///         conversation. This contract is the other shape: the full output goes
///         to a file in Harbor's PRIVATE directory, and the model receives a
///         preview plus a locator it can read back.
///     </para>
///     <para>
///         <b>The write is synchronous on purpose.</b> <see cref="Spill" /> is
///         called once per tool call, from the request-assembly path of the agent
///         loop — a background thread, not a UI thread — and the payload is
///         bounded by the tool's own output cap. The read, by contrast, happens
///         inside a tool call and is asynchronous, like every other file read in
///         the tool layer.
///     </para>
///     <para>
///         <b>Failures are values.</b> A spill that cannot be written (a full
///         disk, a permission denial) comes back as a failed
///         <see cref="Result{TValue}" /> so the caller can keep the ORIGINAL
///         output instead of shipping a silently truncated one. That is the
///         whole point of the contract: an overfull context is recoverable, a
///         silently cut one is not.
///     </para>
/// </remarks>
public interface IToolOutputSpillStore
{
    /// <summary>
    ///     The root directory of the spill area. It lives under Harbor's home
    ///     directory, never in the user's working tree.
    /// </summary>
    string Root { get; }

    /// <summary>
    ///     Persist a tool output that does not fit the inline budget.
    /// </summary>
    /// <param name="sessionId">The session the tool call belongs to.</param>
    /// <param name="toolCallId">The tool call the output belongs to.</param>
    /// <param name="content">The full tool output.</param>
    /// <returns>
    ///     The pointer to the stored output, or a failure when it could not be
    ///     written. The call is idempotent: the same tool call always maps to
    ///     the same file, and a second call returns the existing pointer
    ///     without rewriting the bytes.
    /// </returns>
    Result<ToolOutputSpill> Spill(string sessionId, string toolCallId, string content);

    /// <summary>
    ///     Read a spilled output back.
    /// </summary>
    /// <param name="sessionId">The session the read belongs to.</param>
    /// <param name="path">The path from the pointer the model received.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The stored text, or a failure when the path is not a spill file of
    ///     this session or the file is gone.
    /// </returns>
    Task<Result<string>> ReadAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Remove the session directories whose last write is older than the
    ///     store's time-to-live.
    /// </summary>
    /// <returns>The number of removed directories, or a failure.</returns>
    Result<int> Sweep();
}

/// <summary>
///     The defaults of the tool-output spill policy (issue #1748).
/// </summary>
public static class ToolOutputSpillPolicy
{
    /// <summary>
    ///     The default inline budget: a tool result longer than this many
    ///     characters is spilled to a file and reaches the model as a preview
    ///     plus a locator.
    /// </summary>
    /// <remarks>
    ///     The value is the one the context boundary already used before the
    ///     spill existed, so the default install spends the same context budget
    ///     as before — it only stops losing the tail.
    /// </remarks>
    public const int DefaultMaxInlineChars = 2000;

    /// <summary>
    ///     The default time-to-live of a session's spill directory, in hours.
    /// </summary>
    /// <remarks>
    ///     The spill area is a scratch pad, not an archive: the full output
    ///     also lives in the session log, and the file exists so the model can
    ///     re-read it DURING the run.
    /// </remarks>
    public const int DefaultTtlHours = 24;
}
