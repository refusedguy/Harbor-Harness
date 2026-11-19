namespace Harbor.Application.Sessions;

/// <summary>
///     Kind of lineage edge recorded between two sessions (issue #2660).
/// </summary>
public enum ThreadRelationKind
{
    /// <summary>
    ///     The source session was forked from the target at
    ///     <see cref="ThreadRelation.SourcePoint" />; its delta can be merged back.
    /// </summary>
    Fork,
}

/// <summary>
///     A named lineage edge between a fork and the session it was taken from
///     (issue #2660: <c>ThreadRelation { kind, sourceSessionId, sourcePoint,
///     targetSessionId }</c>).
/// </summary>
/// <remarks>
///     <para>
///         The direction follows merge-back: <see cref="SourceSessionId" /> is the
///         fork that carries the delta, <see cref="TargetSessionId" /> is the parent
///         that receives it. The parent link is already durable on
///         <c>Session.ParentSessionId</c>; this record names the edge and the
///         boundary without adding a new store row.
///     </para>
///     <para>
///         <see cref="SourcePoint" /> is the message id at which the fork was
///         taken. It is the 3way base boundary: the fork's history up to and
///         including that id is the common ancestor of the two branches.
///     </para>
/// </remarks>
/// <param name="Kind">How the two sessions are related.</param>
/// <param name="SourceSessionId">Fork session carrying the delta to merge back.</param>
/// <param name="SourcePoint">Boundary message id the fork was taken at.</param>
/// <param name="TargetSessionId">Parent session the delta merges into.</param>
public sealed record ThreadRelation(
    ThreadRelationKind Kind,
    string SourceSessionId,
    string SourcePoint,
    string TargetSessionId);
