namespace Harbor.ChangeTracking;

/// <summary>
///     The region of the worktree a diff review was performed against
///     (issue #2672, G03).
/// </summary>
/// <remarks>
///     Mirrors the <c>unstaged</c>/<c>staged</c>/<c>branch</c> scope of the
///     source diff-review schema. The scope is part of a review's identity:
///     having reviewed the staged diff says nothing about the unstaged one,
///     so the two must never share a review flag.
/// </remarks>
public enum DiffReviewScope
{
    Unstaged,
    Staged,
    Branch
}

/// <summary>
///     The per-file record of what was reviewed in a diff (issue #2672, G03).
/// </summary>
/// <remarks>
///     <para>
///         A review is a fact about one exact diff: the file, the region
///         (scope), and the identity of the bytes that were looked at. Keeping
///         only a "reviewed" timestamp is not enough — the file can change
///         immediately afterwards, and an old flag would then hide new
///         content. <see cref="ReviewDiffIdentity" /> is the identity of the
///         diff that was reviewed. A caller compares it with the identity of
///         the diff it is about to show through
///         <see cref="IsStale(DiffReviewScope, string?)" />.
///     </para>
///     <para>
///         <see cref="LastSeenDiffIdentity" /> is the identity of the diff the
///         viewer last showed. Recording it does not mark anything reviewed;
///         it only makes a stale review detectable even when the caller cannot
///         recompute the identity itself. Seeing a diff is not reviewing it.
///     </para>
/// </remarks>
public sealed record DiffReviewState
{
    /// <summary>Schema version of the review-state shape.</summary>
    public const int CurrentSchemaVersion = 1;

    private DiffReviewState(
        int schemaVersion,
        string filePath,
        string? oldPath,
        DiffReviewScope scope,
        string? reviewDiffIdentity,
        DateTimeOffset? reviewedAtUtc,
        string? lastSeenDiffIdentity,
        DateTimeOffset? lastOpenedAtUtc)
    {
        SchemaVersion = schemaVersion;
        FilePath = filePath;
        OldPath = oldPath;
        Scope = scope;
        ReviewDiffIdentity = reviewDiffIdentity;
        ReviewedAtUtc = reviewedAtUtc;
        LastSeenDiffIdentity = lastSeenDiffIdentity;
        LastOpenedAtUtc = lastOpenedAtUtc;
    }

    /// <summary>Schema version carried by this state.</summary>
    public int SchemaVersion { get; }

    /// <summary>Repo-relative path of the reviewed file. Never blank.</summary>
    public string FilePath { get; }

    /// <summary>Original path when the file was renamed, otherwise null.</summary>
    public string? OldPath { get; }

    /// <summary>Which diff region the review covered.</summary>
    public DiffReviewScope Scope { get; }

    /// <summary>Identity of the exact diff that was reviewed, when reviewed.</summary>
    public string? ReviewDiffIdentity { get; }

    /// <summary>When the review was recorded, in UTC.</summary>
    public DateTimeOffset? ReviewedAtUtc { get; }

    /// <summary>Identity of the diff the viewer last showed, when known.</summary>
    public string? LastSeenDiffIdentity { get; }

    /// <summary>When the viewer last showed that diff, in UTC.</summary>
    public DateTimeOffset? LastOpenedAtUtc { get; }

    /// <summary>
    ///     True when a review of an exact diff has been recorded under the
    ///     current schema. A half-written state (identity without a time, or
    ///     the reverse) can never be reviewed.
    /// </summary>
    public bool IsReviewed =>
        SchemaVersion == CurrentSchemaVersion &&
        ReviewedAtUtc.HasValue &&
        !string.IsNullOrWhiteSpace(ReviewDiffIdentity);

    /// <summary>
    ///     Create the state for one file. A review identity and its timestamp
    ///     must be supplied together, so the record cannot describe a review
    ///     that never happened.
    /// </summary>
    /// <param name="filePath">Repo-relative path of the file. Must not be blank.</param>
    /// <param name="scope">Which diff region this state tracks.</param>
    /// <param name="oldPath">Original path when the file was renamed.</param>
    /// <param name="lastSeenDiffIdentity">Identity of the diff last shown, when known.</param>
    /// <param name="lastOpenedAtUtc">When that diff was last shown.</param>
    /// <param name="reviewDiffIdentity">Identity of the reviewed diff, when reviewed.</param>
    /// <param name="reviewedAtUtc">When the review was recorded.</param>
    /// <param name="schemaVersion">Schema version of a state being replayed.</param>
    /// <returns>The new review state.</returns>
    public static DiffReviewState Create(
        string filePath,
        DiffReviewScope scope = DiffReviewScope.Unstaged,
        string? oldPath = null,
        string? lastSeenDiffIdentity = null,
        DateTimeOffset? lastOpenedAtUtc = null,
        string? reviewDiffIdentity = null,
        DateTimeOffset? reviewedAtUtc = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A review must name a file.", nameof(filePath));
        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope));

        // The two halves of a review are one fact. Reject a state that claims
        // only one of them at the boundary rather than carrying a half-truth.
        if (reviewDiffIdentity is not null && !reviewedAtUtc.HasValue)
            throw new ArgumentException("A reviewed diff needs a review time.", nameof(reviewedAtUtc));
        if (reviewedAtUtc.HasValue && string.IsNullOrWhiteSpace(reviewDiffIdentity))
            throw new ArgumentException("A review time needs the identity of the reviewed diff.", nameof(reviewDiffIdentity));

        return new DiffReviewState(
            schemaVersion,
            filePath,
            Normalize(oldPath),
            scope,
            Normalize(reviewDiffIdentity),
            reviewedAtUtc,
            Normalize(lastSeenDiffIdentity),
            lastOpenedAtUtc);
    }

    /// <summary>
    ///     Parse a <c>--scope</c> argument. Matching is case-insensitive and
    ///     an unknown value is rejected rather than silently treated as one
    ///     of the known scopes.
    /// </summary>
    public static DiffReviewScope ParseScope(string? value)
    {
        string scope = value?.Trim() ?? string.Empty;
        if (string.Equals(scope, "unstaged", StringComparison.OrdinalIgnoreCase))
            return DiffReviewScope.Unstaged;
        if (string.Equals(scope, "staged", StringComparison.OrdinalIgnoreCase))
            return DiffReviewScope.Staged;
        if (string.Equals(scope, "branch", StringComparison.OrdinalIgnoreCase))
            return DiffReviewScope.Branch;
        throw new ArgumentException($"Unknown diff review scope: {value ?? "<null>"}.", nameof(value));
    }

    /// <summary>
    ///     Whether the recorded review no longer covers the given diff.
    ///     A missing review, a schema mismatch, a scope change, an unknown
    ///     current identity, or any change in the diff identity all make the
    ///     review stale.
    /// </summary>
    /// <param name="scope">Scope of the diff being checked.</param>
    /// <param name="currentDiffIdentity">Identity of the diff about to be shown.</param>
    /// <returns><c>true</c> when the diff must be reviewed again.</returns>
    public bool IsStale(DiffReviewScope scope, string? currentDiffIdentity)
    {
        if (!IsReviewed || Scope != scope || string.IsNullOrWhiteSpace(currentDiffIdentity))
            return true;

        return !string.Equals(ReviewDiffIdentity, currentDiffIdentity, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <see cref="IsStale(DiffReviewScope, string?)" /> for the same scope
    ///     this state was recorded with.
    /// </summary>
    public bool IsStale(string? currentDiffIdentity) => IsStale(Scope, currentDiffIdentity);

    /// <summary>
    ///     Return the state after the exact diff <paramref name="diffIdentity" />
    ///     has been reviewed. The original is untouched: an acknowledgement is
    ///     a new fact, not a mutation of the state that was inspected.
    /// </summary>
    /// <param name="diffIdentity">Identity of the diff that was reviewed.</param>
    /// <param name="reviewedAtUtc">Review time; defaults to now in UTC.</param>
    /// <returns>The reviewed state.</returns>
    public DiffReviewState MarkReviewed(string diffIdentity, DateTimeOffset? reviewedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(diffIdentity))
            throw new ArgumentException("A review must identify the diff it covered.", nameof(diffIdentity));

        return new DiffReviewState(
            CurrentSchemaVersion,
            FilePath,
            OldPath,
            Scope,
            diffIdentity,
            reviewedAtUtc ?? DateTimeOffset.UtcNow,
            diffIdentity,
            LastOpenedAtUtc);
    }

    /// <summary>
    ///     Return the state after the viewer last showed
    ///     <paramref name="diffIdentity" />. This does NOT mark the diff
    ///     reviewed; it only records what was on screen, so a stale review can
    ///     be detected later.
    /// </summary>
    /// <param name="diffIdentity">Identity of the diff that was shown.</param>
    /// <param name="lastOpenedAtUtc">Show time; defaults to now in UTC.</param>
    /// <returns>The state with the new last-seen identity.</returns>
    public DiffReviewState RecordSeen(string diffIdentity, DateTimeOffset? lastOpenedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(diffIdentity))
            throw new ArgumentException("A shown diff must have an identity.", nameof(diffIdentity));

        return new DiffReviewState(
            SchemaVersion,
            FilePath,
            OldPath,
            Scope,
            ReviewDiffIdentity,
            ReviewedAtUtc,
            diffIdentity,
            lastOpenedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
