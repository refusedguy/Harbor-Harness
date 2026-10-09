namespace Harbor.Application.Sessions;

/// <summary>
///     Outcome of one <c>harbor run reject</c> decision (epic #42, slice S7,
///     #385). The lifecycle states are untouched by this enum: slice 1 only
///     answers and prints, it never moves the manifest. Later slices
///     (<c>--patch</c>, <c>--worktree</c>, <c>--undo-apply</c>) move
///     <c>Reported -&gt; Rejected</c> through
///     <see cref="RunChangeTransitions" />; they do not reinterpret it.
/// </summary>
public enum RejectOutcome
{
    /// <summary>The out-of-reach inventory was printed; nothing was changed, exit 5.</summary>
    InventoryShown,

    /// <summary>Bad usage (no mode, unknown mode, unknown run, meaningless flag) — exit 2.</summary>
    Refused,

    /// <summary>A mode whose producer slice has not landed yet — exit 4 with the stage named.</summary>
    NotImplemented,
}

/// <summary>
///     One reject decision: its outcome, the process exit code, and the
///     human-readable reason (the three modes, the inventory, or the named
///     missing stage — never a bare refusal).
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ExitCode">Process exit code (5 inventory, 2 bad usage, 4 named-stage failure).</param>
/// <param name="Message">Printed to stdout for the inventory, stderr otherwise.</param>
public sealed record RejectResult(RejectOutcome Outcome, int ExitCode, string Message);

/// <summary>
///     What no local undo can reach (epic #42, slice S7, #385). This is the
///     single source of truth: the S5 report's <c>NotVerified</c> /
///     <c>KnownRisks</c> text (#379) and <c>docs/VERIFIED_CHANGE.md</c> repeat
///     these words, they do not restate them — a second copy would drift.
/// </summary>
public static class RunRejectEffects
{
    /// <summary>
    ///     The full out-of-reach inventory. Fixed at eight entries; a test
    ///     asserts the count and every entry so no later edit can quietly
    ///     narrow what reject admits it cannot undo.
    /// </summary>
    public static readonly IReadOnlyList<string> OutOfReach = new[]
    {
        "commits already pushed",
        "PRs or issues created",
        "network calls",
        "external API writes",
        "database migrations",
        "files written outside the repository",
        "~/.harbor session and log records",
        "anything already consumed by the operator's downstream tooling",
    };

    /// <summary>
    ///     Render the inventory for <paramref name="runId" /> as one block:
    ///     a scope line, the eight entries, and the read-only promise.
    /// </summary>
    public static string RenderInventory(string runId)
    {
        string items = string.Join('\n', OutOfReach.Select(entry => $"  - {entry}"));
        return $"Run '{runId}': no local undo reaches outside the frozen change set. Out of reach:\n" +
            items + "\n" +
            "--all-effects is read-only: nothing was deleted, no isolated copy was removed, the manifest is unchanged.";
    }
}

/// <summary>
///     Reject router (epic #42, slice S7, #385): rejection is three separately
///     invocable operations, never one blurred action. Slice 1 implements the
///     routing and <c>--all-effects</c> only; <c>--patch</c>,
///     <c>--worktree</c> and <c>--undo-apply</c> fail closed naming the slice
///     that owns them, because the frozen change set (S3, #377), the report
///     (S5, #379) and accept (S6, #382) have not landed yet.
/// </summary>
/// <remarks>
///     Read-only in this slice: no step here writes to the operator tree, the
///     isolated copy, or the manifest. The manifest is loaded to prove the run
///     exists (an unknown id is exit 2, like everywhere else) and then left
///     untouched — invoking twice prints twice and changes nothing.
/// </remarks>
public static class RunReject
{
    /// <summary>Drop the run's frozen artifacts; the isolated copy is untouched (later slice).</summary>
    public const string PatchFlag = "--patch";

    /// <summary>Remove the isolated working copy (later slice).</summary>
    public const string WorktreeFlag = "--worktree";

    /// <summary>Print the out-of-reach inventory; read-only (this slice).</summary>
    public const string AllEffectsFlag = "--all-effects";

    /// <summary>Reverse the frozen set after accept (later slice).</summary>
    public const string UndoApplyFlag = "--undo-apply";

    /// <summary>
    ///     Decide one <c>harbor run reject</c> invocation. A missing mode flag
    ///     prints the three modes and exits 2 — there is no default.
    /// </summary>
    /// <param name="runIdValue">Run id (validated like everywhere else: no path separators).</param>
    /// <param name="modeFlag">Exactly one of the four mode flags, or null when the caller passed none.</param>
    /// <param name="force">Caller passed <c>--force</c>; meaningful only to <c>--worktree</c>.</param>
    public static RejectResult Decide(string? runIdValue, string? modeFlag, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(modeFlag))
            return new RejectResult(RejectOutcome.Refused, 2, Usage());
        if (!IsKnownMode(modeFlag))
            return new RejectResult(
                RejectOutcome.Refused, 2,
                $"Unknown reject mode '{modeFlag}'. Use --patch, --worktree, --all-effects, or --undo-apply.\n" + Usage());
        if (force && !modeFlag.Equals(WorktreeFlag, StringComparison.Ordinal))
            return new RejectResult(
                RejectOutcome.Refused, 2,
                $"Reject {modeFlag} does not take --force: only --worktree does, to include paths outside the frozen set.");

        Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(runIdValue);
        if (loaded.IsFailure)
            return new RejectResult(RejectOutcome.Refused, 2, loaded.Error);
        RunManifest manifest = loaded.Value;

        if (modeFlag.Equals(AllEffectsFlag, StringComparison.Ordinal))
            return new RejectResult(
                RejectOutcome.InventoryShown, 5, RunRejectEffects.RenderInventory(manifest.RunId));

        return new RejectResult(RejectOutcome.NotImplemented, 4, PendingMessage(manifest, modeFlag));
    }

    private static bool IsKnownMode(string modeFlag) =>
        modeFlag.Equals(PatchFlag, StringComparison.Ordinal) ||
        modeFlag.Equals(WorktreeFlag, StringComparison.Ordinal) ||
        modeFlag.Equals(AllEffectsFlag, StringComparison.Ordinal) ||
        modeFlag.Equals(UndoApplyFlag, StringComparison.Ordinal);

    private static string Usage() =>
        "Usage: harbor run reject <RunId> (--patch | --worktree | --all-effects | --undo-apply)\n" +
        "  --patch       drop the run's frozen artifacts; the isolated copy is untouched.\n" +
        "  --worktree    remove the isolated working copy (--force includes unaccounted paths).\n" +
        "  --all-effects print what no local undo can reach; read-only.\n" +
        "  --undo-apply  reverse the frozen set after accept; refuses on operator edits.\n" +
        "Reject never touches the operator tree by default.";

    private static string PendingMessage(RunManifest manifest, string modeFlag)
    {
        if (modeFlag.Equals(PatchFlag, StringComparison.Ordinal))
            return $"Reject --patch (S7, #385) is not implemented in this slice: " +
                $"the frozen change set (S3, #377) and the report (S5, #379) have not landed, " +
                $"so run '{manifest.RunId}' has no rejectable artifacts yet. Nothing was deleted.";
        if (modeFlag.Equals(WorktreeFlag, StringComparison.Ordinal))
            return $"Reject --worktree (S7, #385) is not implemented in this slice: " +
                $"the isolated copy at '{manifest.WorktreePath}' was left untouched. Nothing was removed.";
        return $"Reject --undo-apply (S7, #385) is not implemented in this slice: " +
            "it reverses the frozen set only after accept (S6, #382), " +
            "and the frozen set (S3, #377) has not landed. Nothing was reversed.";
    }
}
