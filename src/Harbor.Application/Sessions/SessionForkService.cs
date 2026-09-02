using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
namespace Harbor.Application.Sessions;

/// <summary>
///     Outcome of a completed fork: the materialized child session plus how many
///     messages were copied into it.
/// </summary>
/// <param name="Session">The freshly created child session record.</param>
/// <param name="Copied">Number of history messages appended to the child.</param>
public sealed record SessionFork(Session Session, int Copied);

/// <summary>
///     Session fork (v0.8): branch an existing session into a NEW child session,
///     copying the parent's history up to — and including — a boundary message.
///     The parent session is never modified.
/// </summary>
/// <remarks>
///     <para>
///         The child keeps the parent's <c>Directory</c>/<c>Agent</c>/<c>ProviderId</c>/
///         <c>Model</c> binding, but receives a fresh id, zeroed metadata, and a
///         <see cref="Session.ParentSessionId" /> lineage stamp persisted via
///         <see cref="ISessionStore.UpdateAsync" />. Forked messages keep their original
///         ids and <see cref="AgentMessage.CreatedAt" /> ordering and are re-stamped to the
///         child's session id.
///     </para>
///     <para>
///         The prefix semantics mirror <c>DeleteMessagesAfterAsync</c>: revert = rewind the
///         current session AFTER a message; fork = copy history UP TO that same message and
///         continue elsewhere. Every step fail-closes via <c>Result</c>; a failed lineage
///         stamp deletes the just-created shell so no orphan child survives.
///     </para>
/// </remarks>
public sealed class SessionForkService
{
    /// <summary>
    ///     Fork <paramref name="sessionId" /> into a new session holding its history prefix.
    /// </summary>
    /// <param name="store">The store owning both sessions.</param>
    /// <param name="sessionId">Id of the source session to branch from.</param>
    /// <param name="upToMessageId">
    ///     Inclusive last message id to copy. Null copies the full history. An unknown id fails without creating anything.
    /// </param>
    /// <param name="title">Child title; defaults to "Fork of {parent title}".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created child session record and the copy count.</returns>
    public async Task<Result<SessionFork>> ForkAsync(
        ISessionStore store,
        string sessionId,
        string? upToMessageId = null,
        string? title = null,
        CancellationToken ct = default)
    {
        Result<Session> parentRes = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (parentRes.IsFailure)
            return Result.Failure<SessionFork>(parentRes.Error);

        Result<IReadOnlyList<AgentMessage>> msgsRes = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (msgsRes.IsFailure)
            return Result.Failure<SessionFork>(msgsRes.Error);

        int count;
        if (upToMessageId is null)
        {
            count = msgsRes.Value.Count;
        }
        else
        {
            // Linear scan keeps this allocation-free for the "cut at Nth message" case.
            int boundary = -1;
            IReadOnlyList<AgentMessage> source = msgsRes.Value;
            for (int i = 0; i < source.Count; i++)
            {
                if (string.Equals(source[i].Id, upToMessageId, StringComparison.Ordinal))
                {
                    boundary = i;
                    break;
                }
            }

            if (boundary < 0)
                return Result.Failure<SessionFork>(
                    $"Message '{upToMessageId}' not found in session '{sessionId}'.");

            count = boundary + 1;
        }

        Session parent = parentRes.Value;
        Result<Session> created = await store.CreateAsync(
            parent.Directory, parent.Agent, parent.ProviderId, parent.Model, ct).ConfigureAwait(false);
        if (created.IsFailure)
            return Result.Failure<SessionFork>(created.Error);

        Session child = created.Value;

        // Lineage must be durable before any message lands — otherwise a crash between
        // Create and Update leaves a sibling with no visible branch relationship. The
        // requested/defaulted title rides along on the same write.
        Session stampedChild = child with
        {
            ParentSessionId = sessionId,
            Title = title ?? $"Fork of {parent.Title}",
        };
        Result stamped = await store.UpdateAsync(stampedChild, ct).ConfigureAwait(false);
        if (stamped.IsFailure)
        {
            await store.DeleteAsync(child.Id, CancellationToken.None).ConfigureAwait(false);
            return Result.Failure<SessionFork>(stamped.Error);
        }

        for (int i = 0; i < count; i++)
        {
            AgentMessage copy = msgsRes.Value[i] with { SessionId = child.Id };
            Result appended = await store.AppendMessageAsync(child.Id, copy, ct).ConfigureAwait(false);
            if (appended.IsFailure)
                return Result.Failure<SessionFork>(appended.Error);
        }

        return Result.Success(new SessionFork(stampedChild, count));
    }
}
