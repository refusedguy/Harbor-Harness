using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Sessions;

/// <summary>
///     Moves one session between stores through a portable line-based payload:
///     line 1 is the export envelope (schema version + full <see cref="Session" /> record),
///     every following line is one serialized <see cref="AgentMessage" />.
/// </summary>
/// <remarks>
///     <para>
///         Declared in Domain so apps depend on the capability while implementations live
///         beside their storage codecs (canonical impl: <c>JsonlSessionPorter</c> in
///         <c>Harbor.Storage.Jsonl</c>, reusing the AOT-safe message codec).
///     </para>
///     <para>
///         Import NEVER overwrites an existing session: the porter mints a fresh session
///         id and appends the imported history there (duplicate-import safe by design —
///         running import twice yields two independent copies, never corruption).
///     </para>
/// </remarks>
public interface ISessionPorter
{
    /// <summary>
    ///     Write the full session (metadata header + all messages, chronological order)
    ///     to <paramref name="output" /> as portable text lines.
    /// </summary>
    /// <param name="store">Source of truth to read from.</param>
    /// <param name="sessionId">Session to export.</param>
    /// <param name="output">Destination stream (caller owns lifetime).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure when the session/messages cannot be read.</returns>
    public Task<Result> ExportAsync(
        ISessionStore store, string sessionId, TextWriter output, CancellationToken ct = default);

    /// <summary>
    ///     Read a portable payload produced by <see cref="ExportAsync" /> and materialize it
    ///     as a NEW session in <paramref name="store" /> (fresh id, preserved metadata).
    /// </summary>
    /// <param name="store">Destination store.</param>
    /// <param name="input">Source stream (caller owns lifetime).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly-created session id, or failure with a diagnostic.</returns>
    public Task<Result<string>> ImportAsync(
        ISessionStore store, TextReader input, CancellationToken ct = default);
}
