using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CLI face of <c>harbor sessions merge-back</c>: validates the fork, names
///     the lineage edge and delegates the 3way delta merge to
///     <see cref="SessionMergeBackService" />. The service stays store-generic;
///     the runner is the place where a CLI request becomes a
///     <see cref="ThreadRelation" /> (issue #3407).
/// </summary>
public sealed class SessionMergeBackRunner
{
    private readonly ISessionStore _store;
    private readonly SessionMergeBackService _mergeBack;

    /// <summary>Construct over any backend; the merge-back service is DI-owned.</summary>
    public SessionMergeBackRunner(ISessionStore store, SessionMergeBackService mergeBack)
    {
        _store = Ensure.NotNull(store);
        _mergeBack = Ensure.NotNull(mergeBack);
    }

    /// <summary>
    ///     Merge the fork's delta back into the parent it was taken from.
    /// </summary>
    /// <param name="forkSessionId">Fork session carrying the delta.</param>
    /// <param name="fromMessageId">Boundary message id (inclusive base).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The lineage edge, applied ids and any conflicts. Conflicts are
    /// a normal outcome: the service writes nothing to the parent then.</returns>
    public async Task<Result<MergeBackOutcome>> MergeBackAsync(
        string forkSessionId,
        string fromMessageId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(forkSessionId))
            return Result.Failure<MergeBackOutcome>("Fork session id must not be empty.");
        if (string.IsNullOrWhiteSpace(fromMessageId))
            return Result.Failure<MergeBackOutcome>("Boundary message id must not be empty.");

        Result<Session> source = await _store.GetAsync(forkSessionId, ct).ConfigureAwait(false);
        return await source
            .Bind(session => MergeNamedAsync(session, fromMessageId, ct))
            .ConfigureAwait(false);
    }

    private async Task<Result<MergeBackOutcome>> MergeNamedAsync(
        Session source,
        string fromMessageId,
        CancellationToken ct)
    {
        string? parentId = source.ParentSessionId;
        if (parentId is null)
            return Result.Failure<MergeBackOutcome>(
                $"Session '{source.Id}' is not a fork; it has no parent to merge into.");

        var relation = new ThreadRelation(ThreadRelationKind.Fork, source.Id, fromMessageId, parentId);
        return await _mergeBack
            .MergeBackAsync(_store, relation.SourceSessionId, relation.SourcePoint, ct: ct)
            .Map(merged => new MergeBackOutcome(
                relation, merged.AppliedCount, merged.AppliedMessageIds, merged.Conflicts))
            .ConfigureAwait(false);
    }

    /// <summary>What the CLI reports after a merge-back attempt.</summary>
    /// <param name="Relation">The lineage edge the delta travelled along.</param>
    /// <param name="AppliedCount">Messages appended to the parent.</param>
    /// <param name="AppliedMessageIds">Ids of the appended messages, in order.</param>
    /// <param name="Conflicts">Unresolved versions; non-empty means nothing was written.</param>
    public sealed record MergeBackOutcome(
        ThreadRelation Relation,
        int AppliedCount,
        IReadOnlyList<string> AppliedMessageIds,
        IReadOnlyList<MergeBackConflict> Conflicts)
    {
        /// <summary>True when the merge was blocked and the parent was left untouched.</summary>
        public bool HasConflicts => Conflicts.Count > 0;
    }
}
