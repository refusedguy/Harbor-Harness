using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Forking;

/// <summary>
///     A completed fork: the materialized child session plus how many history
///     messages were copied into it.
/// </summary>
/// <param name="Session">The freshly created child session record.</param>
/// <param name="Copied">Number of history messages appended to the child.</param>
public sealed record SessionForked(Session Session, int Copied);

/// <summary>
///     Branch an existing session into a NEW child session, copying the parent's
///     history up to — and including — a boundary message. The parent is never
///     modified.
/// </summary>
/// <remarks>
///     <para>
///         #670: this is the narrow port over <c>Harbor.Application.Sessions.SessionForkService</c>,
///         declared here for the same reason <c>ICommonConfigModelRefReader</c> was (#453,
///         ADR-009): the UI framework has to fork sessions, and it cannot reference
///         <c>Harbor.Application</c> to ask. The layer matrix draws
///         <c>Presentation → Application</c> as a violation
///         (<c>FullLayerMatrixTests.Matrix_AllowedEntries_RespectLayerRules</c>), and the one
///         existing exception for that edge (<c>Harbor.Desktop.Abstractions</c>) is a named debt
///         with a stated fix — not a precedent. A port in Domain that a composition root adapts
///         is the direction the matrix already permits.
///     </para>
///     <para>
///         It used to be the other way round. <c>SessionFactory.CreateBranchAsync</c> hand-wrote
///         a second fork, and the two drifted until a UI fork was unrecognisable as a fork: no
///         <see cref="Session.ParentSessionId" />, a title applied but never persisted, and copied
///         message ids regenerated. A port cannot drift that way — there is only one fork left.
///     </para>
///     <para>
///         <b>What this port is not:</b> an error-message contract. Implementations return the
///         underlying store failure verbatim; the calling Presentation layer adds the context it
///         owns (which session, how far the copy got).
///     </para>
/// </remarks>
public interface ISessionForker
{
    /// <summary>
    ///     Fork <paramref name="sessionId" /> into a new session holding its history prefix.
    /// </summary>
    /// <param name="sessionId">Id of the source session to branch from.</param>
    /// <param name="upToMessageId">
    ///     Inclusive last message id to copy. Null copies the full history. An unknown id fails
    ///     without creating anything.
    /// </param>
    /// <param name="title">Child title; defaults to "Fork of {parent title}".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created child session and the copy count.</returns>
    Task<Result<SessionForked>> ForkAsync(
        string sessionId,
        string? upToMessageId = null,
        string? title = null,
        CancellationToken ct = default);
}