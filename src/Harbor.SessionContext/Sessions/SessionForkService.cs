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
///     <para>
///         <b>#670 — the one fork.</b> This used to have a second implementation in the UI
///         framework (<c>SessionFactory.CreateBranchAsync</c>), reachable only because that
///         project cannot reference <c>Harbor.Application</c>. The copy drifted until a UI fork
///         set no <c>ParentSessionId</c>, persisted no title and regenerated every copied
///         message id. The UI framework now reaches this service through the
///         <c>ISessionForker</c> port (<c>Harbor.Ui.Framework.Abstractions/Forking</c>), so this
///         is the only fork left — and anything a caller needs guaranteed has to be guaranteed
///         here. One diagnostic moved IN with the deletion: a failed copy reports how far it
///         got. Since #2226 the prefix is copied in one atomic batch, so the number is always
///         zero on failure (no record landed), but the text survives for callers that keyed
///         on it. It is the only return in this method that rebuilds its error text — every
///         other one re-types the store's <c>Result</c> and leaves the string alone, per
///         <c>ResultConversionBehaviourTests</c>.
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
            return parentRes.ConvertFailure<SessionFork>();

        Result<IReadOnlyList<AgentMessage>> msgsRes = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
        if (msgsRes.IsFailure)
            return msgsRes.ConvertFailure<SessionFork>();

        int count;
        if (upToMessageId is null)
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            count = msgsRes.Value.Count;
            #pragma warning restore CFE0001
        }
        else
        {
            // Linear scan keeps this allocation-free for the "cut at Nth message" case.
            int boundary = -1;
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            IReadOnlyList<AgentMessage> source = msgsRes.Value;
            #pragma warning restore CFE0001
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
            return created.ConvertFailure<SessionFork>();

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
            return stamped.ConvertFailure<SessionFork>();
        }

        // #2226: bracket the copy with a durable claim. The start is written
        // FIRST and the end LAST, so a crash between them leaves an unmatched
        // start — detectable evidence that this child is an incomplete fork.
        ISessionOperationEventStore? markers = store as ISessionOperationEventStore;
        string forkId = Guid.NewGuid().ToString("N");
        if (markers is not null)
        {
            Result claimed = await markers.AppendOperationEventAsync(
                child.Id,
                new SessionOperationEvent
                {
                    Kind = SessionOperationKind.ForkStart,
                    Owner = forkId,
                    BoundaryMessageId = upToMessageId,
                    Copied = count,
                    OccurredAt = DateTimeOffset.UtcNow,
                },
                ct).ConfigureAwait(false);
            if (claimed.IsFailure)
            {
                await store.DeleteAsync(child.Id, CancellationToken.None).ConfigureAwait(false);
                return claimed.ConvertFailure<SessionFork>();
            }
        }

        // #1571: the copy protocol lives on the message (Prototype), not here.
        // #2226: the whole prefix goes in ONE atomic write. A crash no longer
        // leaves a child with a prefix of the history — it is all or nothing.
        var copies = new AgentMessage[count];
        for (int i = 0; i < count; i++)
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return above, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            copies[i] = msgsRes.Value[i].CloneForSession(child.Id);
            #pragma warning restore CFE0001
        }

        Result appended = await store.AppendMessagesBatchAsync(child.Id, copies, ct).ConfigureAwait(false);
        if (appended.IsFailure)
        {
            // No record landed (the batch is atomic), so the child cannot look
            // like a partial fork. Remove the shell and the claim with it.
            await store.DeleteAsync(child.Id, CancellationToken.None).ConfigureAwait(false);
            return appended.ConvertFailure<SessionFork>()
                .MapError(e => $"Failed to copy message history (0 of {count} copied): {e}");
        }

        if (markers is not null)
        {
            Result released = await markers.AppendOperationEventAsync(
                child.Id,
                new SessionOperationEvent
                {
                    Kind = SessionOperationKind.ForkEnd,
                    Owner = forkId,
                    BoundaryMessageId = upToMessageId,
                    Copied = count,
                    OccurredAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None).ConfigureAwait(false);
            if (released.IsFailure)
            {
                // The copy landed, but the claim is still open. That is a
                // DETECTABLE state, not a silent success: surface the failure.
                return released.ConvertFailure<SessionFork>();
            }
        }

        return Result.Success(new SessionFork(stampedChild, count));
    }
}
