using Harbor.Abstractions.Models;

namespace Harbor.Application.Sessions;

/// <summary>
///     One message the 3way merge could not resolve: the parent and the fork
///     both changed the same ancestor, so the fork's version is not applied.
/// </summary>
/// <param name="MessageId">Id of the conflicting message.</param>
/// <param name="BaseText">Text at the fork point (the common ancestor).</param>
/// <param name="TargetText">Text currently in the parent session.</param>
/// <param name="SourceText">Text in the fork session.</param>
public sealed record MergeBackConflict(
    string MessageId,
    string BaseText,
    string TargetText,
    string SourceText);

/// <summary>
///     Result of a delta merge-back (issue #2660): the lineage edge, the names
///     of the applied messages, and any conflicts that blocked the merge.
/// </summary>
/// <remarks>
///     Conflicts are a normal outcome, not a failure rail: the merge is atomic,
///     so a non-empty <see cref="Conflicts" /> means nothing was written to the
///     target. The caller decides how to present the three versions.
/// </remarks>
/// <param name="Relation">The lineage edge that was merged.</param>
/// <param name="Strategy">Strategy used; <c>3way</c> today.</param>
/// <param name="AppliedCount">Number of fork messages appended to the target.</param>
/// <param name="AppliedMessageIds">Ids of the appended messages, in append order.</param>
/// <param name="Conflicts">Unresolved message versions; empty on a clean merge.</param>
public sealed record SessionMergeBack(
    ThreadRelation Relation,
    string Strategy,
    int AppliedCount,
    IReadOnlyList<string> AppliedMessageIds,
    IReadOnlyList<MergeBackConflict> Conflicts)
{
    /// <summary>True when at least one message could not be merged.</summary>
    public bool HasConflicts => Conflicts.Count > 0;
}
