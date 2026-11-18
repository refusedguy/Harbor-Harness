using CSharpFunctionalExtensions;

namespace Harbor.ChangeTracking;

/// <summary>
///     Minimal verification-report generator for a single path (epic #42,
///     slice S5, #379). It turns one line of a diff tool's
///     <c>--name-status</c> inventory plus the recorded checks for that run
///     into the same <see cref="ChangeReport" /> the full workflow produces.
/// </summary>
/// <remarks>
///     <para>
///         This is an adapter, not a second derivation. All five sections —
///         <c>Changed</c>, <c>Verified</c>, <c>NotVerified</c>, <c>Limits</c>,
///         and <c>KnownRisks</c> — come from <see cref="ChangeReport.Create" />,
///         so a report built here obeys the same "derived from recorded
///         artifacts only" rule as the existing workflow. Nothing in
///         <see cref="RunReporter" />, <see cref="ChangeReport" />, or the
///         <c>harbor run report</c> verb is changed by this type.
///     </para>
///     <para>
///         The single path is supplied by the caller as a parsed
///         <see cref="ChangedPathEntry" />. <see cref="ParseDiffLine" />
///         parses the common <c>STATUS\tPATH</c> form (and the two-path
///         rename/copy form) into one, mirroring the freezer's convention
///         that a rename is recorded under its destination path.
///     </para>
/// </remarks>
public static class SinglePathReportGenerator
{
    /// <summary>Section name for the changed path.</summary>
    public const string ChangedSection = "Changed";

    /// <summary>Section name for checks that passed on the frozen head.</summary>
    public const string VerifiedSection = "Verified";

    /// <summary>Section name for derived gaps and external side effects.</summary>
    public const string NotVerifiedSection = "NotVerified";

    /// <summary>Section name for the recorded run limits.</summary>
    public const string LimitsSection = "Limits";

    /// <summary>Section name for derived known risks.</summary>
    public const string RisksSection = "KnownRisks";

    /// <summary>
    ///     Parse one <c>--name-status</c> line into a single changed path.
    ///     The status is the leading letter (so a score such as <c>R100</c>
    ///     becomes <c>R</c>); a rename or copy is recorded under its
    ///     destination path, matching the freezer. Blank input, a missing
    ///     status, or a missing path is a failure, never an empty entry.
    /// </summary>
    /// <param name="line">
    ///     A tab-separated line, for example <c>M\tsrc/Foo.cs</c> or
    ///     <c>R100\told/Foo.cs\tnew/Foo.cs</c>.
    /// </param>
    public static Result<ChangedPathEntry> ParseDiffLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return Result.Failure<ChangedPathEntry>("A diff name-status line must not be blank.");

        string[] parts = line!.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return Result.Failure<ChangedPathEntry>(
                "A diff name-status line must carry a status and a path.");

        string status = parts[0].Trim();
        if (status.Length == 0)
            return Result.Failure<ChangedPathEntry>("A diff name-status line must carry a status.");

        char kind = status[0];
        bool rename = kind is 'R' or 'C';
        string path = rename && parts.Length >= 3 ? parts[2] : parts[1];
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<ChangedPathEntry>("A diff name-status line must carry a path.");

        return Result.Success(ChangedPathEntry.Create(path.Trim(), kind.ToString()));
    }

    /// <summary>
    ///     Build the five-section report for one path from recorded facts.
    ///     Every section is derived by <see cref="ChangeReport.Create" />;
    ///     callers cannot author a <c>Verified</c> or <c>NotVerified</c>
    ///     entry here.
    /// </summary>
    /// <param name="runId">Minted run id.</param>
    /// <param name="baseRevision">Pinned base revision.</param>
    /// <param name="headRevision">Frozen head revision the checks ran against.</param>
    /// <param name="path">The single changed path.</param>
    /// <param name="checks">Recorded check results for the run.</param>
    /// <param name="ignoredPathCount">Git-ignored paths excluded from the freeze.</param>
    /// <param name="timeoutSeconds">Wall-clock budget from the contract limits.</param>
    /// <param name="maxSteps">Max agent steps from the contract limits.</param>
    /// <param name="worktreeDirty">Whether the worktree holds unfrozen state.</param>
    public static ChangeReport Generate(
        string runId,
        string baseRevision,
        string headRevision,
        ChangedPathEntry path,
        RecordedCheck[] checks,
        int ignoredPathCount,
        int timeoutSeconds,
        int maxSteps,
        bool worktreeDirty)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(checks);
        return ChangeReport.Create(
            runId, baseRevision, headRevision,
            [path], checks, ignoredPathCount, timeoutSeconds, maxSteps, worktreeDirty);
    }
}
