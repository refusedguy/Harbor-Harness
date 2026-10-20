using System.Globalization;
using Harbor.Abstractions.Extensions;

namespace Harbor.ChangeTracking;

/// <summary>
///     One path in the frozen change set (epic #42, slice S3, #377): the
///     diff status plus the path, exactly as the freeze recorded it. An input
///     fact for the checks (S4) and the report (S5) — never a verdict.
/// </summary>
public sealed record FrozenChangeEntry
{
    private FrozenChangeEntry(
        string path,
        string status,
        string? oldPath,
        int? insertions,
        int? deletions,
        bool binary)
    {
        Path = path;
        Status = status;
        OldPath = oldPath;
        Insertions = insertions;
        Deletions = deletions;
        Binary = binary;
    }

    /// <summary>Repo-relative path from the frozen set. For renames/copies this is the new path. Never blank.</summary>
    public string Path { get; }

    /// <summary>
    ///     Single-letter diff status (<c>A</c>, <c>M</c>, <c>D</c>, <c>R</c>,
    ///     <c>T</c>, …). Renames and deletions keep their status — never
    ///     flattened into add+delete. Never blank.
    /// </summary>
    public string Status { get; }

    /// <summary>Previous path for renames and copies; null otherwise.</summary>
    public string? OldPath { get; }

    /// <summary>Inserted lines from numstat; null when <see cref="Binary" />.</summary>
    public int? Insertions { get; }

    /// <summary>Deleted lines from numstat; null when <see cref="Binary" />.</summary>
    public int? Deletions { get; }

    /// <summary>
    ///     True when numstat reports <c>- -</c>: the change is captured in the
    ///     patch with <c>--binary</c> and reported as binary, never rendered
    ///     as a broken text diff.
    /// </summary>
    public bool Binary { get; }

    /// <summary>
    ///     Record one frozen path. Blank path or status is a violated
    ///     precondition (<see cref="ArgumentException" />), not a fact about
    ///     the world — the freeze never produces one.
    /// </summary>
    public static FrozenChangeEntry Create(
        string path,
        string status,
        string? oldPath,
        int? insertions,
        int? deletions,
        bool binary)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A frozen entry must name the path.", nameof(path));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("A frozen entry must carry its diff status.", nameof(status));
        if (oldPath is not null && string.IsNullOrWhiteSpace(oldPath))
            throw new ArgumentException("A frozen old path must not be blank.", nameof(oldPath));
        if (binary)
        {
            if (insertions.HasValue || deletions.HasValue)
                throw new ArgumentException("A binary entry carries no line counts.", nameof(insertions));
        }
        else
        {
            if (!insertions.HasValue || insertions.Value < 0)
                throw new ArgumentException("A text entry must carry a non-negative insertion count.", nameof(insertions));
            if (!deletions.HasValue || deletions.Value < 0)
                throw new ArgumentException("A text entry must carry a non-negative deletion count.", nameof(deletions));
        }
        return new FrozenChangeEntry(path, status, oldPath, insertions, deletions, binary);
    }
}

/// <summary>
///     The frozen change set (epic #42, slice S3, #377): the immutable,
///     reviewable artifact of one run — the patch plus the inventory. Accept
///     (S6), reject (S7) and the report (S5) all read this artifact; none of
///     them re-derives a diff against the moving worktree.
/// </summary>
public sealed record FrozenChangeSet
{
    private FrozenChangeSet(
        string runId,
        string baseRevision,
        string headRevision,
        string patchPath,
        IReadOnlyList<FrozenChangeEntry> entries,
        int ignoredPathCount)
    {
        RunId = runId;
        BaseRevision = baseRevision;
        HeadRevision = headRevision;
        PatchPath = patchPath;
        Entries = entries;
        IgnoredPathCount = ignoredPathCount;
    }

    /// <summary>Minted run id (matches the run directory name).</summary>
    public string RunId { get; }

    /// <summary>Pinned base revision the freeze diffed against.</summary>
    public string BaseRevision { get; }

    /// <summary>Frozen head revision the checks (S4) run against.</summary>
    public string HeadRevision { get; }

    /// <summary>Absolute path of <c>change.patch</c> (<c>runs/&lt;RunId&gt;/change.patch</c>).</summary>
    public string PatchPath { get; }

    /// <summary>Frozen inventory, sorted by path (ordinal).</summary>
    public IReadOnlyList<FrozenChangeEntry> Entries { get; }

    /// <summary>
    ///     <c>.gitignore</c>d paths excluded from the freeze. Recorded, never
    ///     dropped silently — S5 renders it as an explicit gap.
    /// </summary>
    public int IgnoredPathCount { get; }

    /// <summary>
    ///     True when the agent changed nothing: zero entries. A success, not
    ///     an error — and not treated as "verified" (that is S5's job).
    /// </summary>
    public bool IsEmpty => Entries.Count == 0;

    internal static FrozenChangeSet Create(
        string runId,
        string baseRevision,
        string headRevision,
        string patchPath,
        IReadOnlyList<FrozenChangeEntry> entries,
        int ignoredPathCount)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("A frozen set must name its run.", nameof(runId));
        if (string.IsNullOrWhiteSpace(baseRevision))
            throw new ArgumentException("A frozen set must name its base revision.", nameof(baseRevision));
        if (string.IsNullOrWhiteSpace(headRevision))
            throw new ArgumentException("A frozen set must name its frozen head.", nameof(headRevision));
        if (string.IsNullOrWhiteSpace(patchPath))
            throw new ArgumentException("A frozen set must name its patch path.", nameof(patchPath));
        Ensure.NotNull(entries);
        if (ignoredPathCount < 0)
            throw new ArgumentException("The ignored-path count cannot be negative.", nameof(ignoredPathCount));
        var sorted = new List<FrozenChangeEntry>(entries);
        sorted.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
        return new FrozenChangeSet(runId, baseRevision, headRevision, patchPath, sorted, ignoredPathCount);
    }
}

/// <summary>
///     Change-set freezer (epic #42, stage S3): captures the agent's work in
///     the isolated copy as an immutable artifact — <c>change.patch</c> plus
///     <c>changeset.json</c> — and moves the manifest <c>Isolated</c> to
///     <c>Changed</c> through <see cref="RunChangeTransitions" />.
/// </summary>
/// <remarks>
///     <para>
///         Freeze is <c>git add -A</c> (untracked files created by the run are
///         included — that is the point) plus one <c>git commit
///         --no-verify</c> under the pinned Harbor identity (never the
///         operator's <c>~/.gitconfig</c>), then <c>git diff --binary</c> to
///         the run directory. The manifest moves only after both the patch
///         and <c>changeset.json</c> are on disk.
///     </para>
///     <para>
///         Fail-closed throughout: over-limit runs (patch byte cap, default 8
///         MiB; entry-count cap, default 2000) are <c>Result.Failure</c>s
///         naming the cap — the patch is never silently truncated. The index
///         is reset (mixed, worktree untouched) so a retry starts clean.
///     </para>
///     <para>
///         Idempotent: freezing a run past <c>Isolated</c> returns the
///         existing artifact and creates no second commit. An empty change
///         set commits nothing (<c>HeadRevision</c> equals
///         <c>BaseRevision</c>) and succeeds with zero entries.
///     </para>
/// </remarks>
public static class ChangeSetFreezer
{
    /// <summary>Pinned freeze author name: the commit never inherits the operator's git identity.</summary>
    public const string FreezeAuthorName = "Harbor Run";

    /// <summary>Pinned freeze author email: the commit never inherits the operator's git identity.</summary>
    public const string FreezeAuthorEmail = "harbor@local";

    /// <summary>Default patch byte cap: over-limit freezes fail naming this cap, never truncate.</summary>
    public const int DefaultPatchByteCapBytes = 8 * 1024 * 1024;

    /// <summary>Default entry-count cap: over-limit freezes fail naming this cap.</summary>
    public const int DefaultMaxEntries = 2000;

    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DiffTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Freeze <paramref name="workspace" /> with the default caps
    ///     (<see cref="DefaultPatchByteCapBytes" />, <see cref="DefaultMaxEntries" />).
    /// </summary>
    public static Task<Result<FrozenChangeSet>> FreezeAsync(
        IsolatedWorkspace? workspace,
        CancellationToken ct = default) =>
        FreezeAsync(workspace, DefaultPatchByteCapBytes, DefaultMaxEntries, ct);

    /// <summary>
    ///     Freeze <paramref name="workspace" />: stage everything, commit
    ///     under the pinned identity, write <c>change.patch</c> and
    ///     <c>changeset.json</c>, move the manifest to <c>Changed</c>.
    /// </summary>
    /// <param name="workspace">Materialized isolated workspace (S2); its path is the only tree touched.</param>
    /// <param name="patchByteCapBytes">Patch byte cap; must be positive. Over-limit is a failure naming the cap.</param>
    /// <param name="maxEntries">Entry-count cap; must be positive. Over-limit is a failure naming the cap.</param>
    /// <param name="ct">Caller cancellation.</param>
    public static async Task<Result<FrozenChangeSet>> FreezeAsync(
        IsolatedWorkspace? workspace,
        int patchByteCapBytes,
        int maxEntries,
        CancellationToken ct = default)
    {
        if (workspace is null)
            return Result.Failure<FrozenChangeSet>("Isolated workspace must not be null.");
        if (patchByteCapBytes <= 0)
            return Result.Failure<FrozenChangeSet>(
                $"Patch byte cap must be positive (got {patchByteCapBytes.ToString(CultureInfo.InvariantCulture)}).");
        if (maxEntries <= 0)
            return Result.Failure<FrozenChangeSet>(
                $"Entry-count cap must be positive (got {maxEntries.ToString(CultureInfo.InvariantCulture)}).");

        Result<RunManifest> loaded = WorkspaceMaterializer.TryLoad(workspace.RunId.Value);
        if (loaded.IsFailure)
            return loaded.ConvertFailure<FrozenChangeSet>();
        RunManifest manifest = loaded.Value;

        if (!manifest.WorktreePath.Equals(workspace.Path, StringComparison.Ordinal))
            return Result.Failure<FrozenChangeSet>(
                $"Freeze refused: workspace path '{workspace.Path}' does not match " +
                $"the manifest worktree '{manifest.WorktreePath}' for run '{manifest.RunId}'.");
        if (!manifest.BaseRevision.Equals(workspace.BaseRevision, StringComparison.Ordinal))
            return Result.Failure<FrozenChangeSet>(
                $"Freeze refused: workspace base {workspace.BaseRevision} does not match " +
                $"the manifest base {manifest.BaseRevision} for run '{manifest.RunId}'.");
        if (!Directory.Exists(workspace.Path))
            return Result.Failure<FrozenChangeSet>(
                $"Freeze refused: worktree at '{workspace.Path}' does not exist — " +
                "the freeze captures the isolated copy, never the operator's tree.");

        if (manifest.State != RunState.Isolated)
            return await ReadExistingAsync(manifest, ct).ConfigureAwait(false);

        return await FreezeIsolatedAsync(manifest, workspace, patchByteCapBytes, maxEntries, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Load the recorded frozen change set for <paramref name="runIdValue" />
    ///     (epic #42, slice S5 remainder, #379): <c>changeset.json</c> plus the
    ///     <c>change.patch</c> path. No worktree access, no head comparison —
    ///     the report (S5) renders the recorded artifact, including the case
    ///     where the worktree holds post-freeze state (that dirt is a
    ///     <c>KnownRisks</c> entry, not a reason to refuse the read). A missing
    ///     patch or inventory is a <c>Result.Failure</c> naming the freeze
    ///     stage, never an empty set.
    /// </summary>
    /// <param name="runIdValue">Run id (validated like everywhere else: no path separators).</param>
    public static Result<FrozenChangeSet> TryLoad(string? runIdValue) =>
        ValidateRunId(runIdValue).Bind(runId =>
        {
            string runDir = WorkspacePaths.Default.RunDir(runId);
            string patchPath = Path.Combine(runDir, "change.patch");
            string changesetPath = Path.Combine(runDir, "changeset.json");
            if (!File.Exists(patchPath) || !File.Exists(changesetPath))
                return Result.Failure<FrozenChangeSet>(
                    $"Run '{runId}' has no frozen change set ('{patchPath}', '{changesetPath}'): " +
                    "stage 'freeze' (S3, #377) has not produced it — refusing to invent a change set.");
            return ReadChangesetJson(changesetPath, patchPath);
        });

    private static async Task<Result<FrozenChangeSet>> FreezeIsolatedAsync(
        RunManifest manifest,
        IsolatedWorkspace workspace,
        int patchByteCapBytes,
        int maxEntries,
        CancellationToken ct)
    {
        string worktree = workspace.Path;
        string baseRevision = manifest.BaseRevision;
        string runDir = WorkspacePaths.Default.RunDir(manifest.RunId);
        string patchPath = Path.Combine(runDir, "change.patch");
        string changesetPath = Path.Combine(runDir, "changeset.json");

        Result<int> ignored;
        try
        {
            ignored = await CountIgnoredAsync(worktree, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<FrozenChangeSet>($"Freeze of run '{manifest.RunId}' was cancelled.");
        }
        if (ignored.IsFailure)
            return ignored.ConvertFailure<FrozenChangeSet>();

        Result<StagedDiff> staged;
        try
        {
            staged = await StageAndDiffAsync(worktree, baseRevision, manifest.RunId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<FrozenChangeSet>($"Freeze of run '{manifest.RunId}' was cancelled.");
        }
        if (staged.IsFailure)
            return staged.ConvertFailure<FrozenChangeSet>();
        // Hoisted to the guard level: CFE0001 tracks the IsFailure early
        // return only at this nesting — a .Value read inside a branch below
        // is reported.
        List<FrozenChangeEntry> stagedEntries = staged.Value.Entries;
        byte[] stagedPatch = staged.Value.Patch;

        if (stagedEntries.Count > maxEntries)
        {
            await ResetIndexAsync(worktree).ConfigureAwait(false);
            return Result.Failure<FrozenChangeSet>(
                $"Freeze of run '{manifest.RunId}' exceeds the entry-count cap " +
                $"({stagedEntries.Count.ToString(CultureInfo.InvariantCulture)} paths, cap {maxEntries.ToString(CultureInfo.InvariantCulture)} entries): " +
                "refusing to freeze — narrow the change set and retry.");
        }
        if (stagedPatch.Length > patchByteCapBytes)
        {
            await ResetIndexAsync(worktree).ConfigureAwait(false);
            return Result.Failure<FrozenChangeSet>(
                $"Freeze of run '{manifest.RunId}' exceeds the patch byte cap " +
                $"({stagedPatch.Length.ToString(CultureInfo.InvariantCulture)} bytes, cap {patchByteCapBytes.ToString(CultureInfo.InvariantCulture)} bytes): " +
                "the patch is never silently truncated — narrow the change set and retry.");
        }

        Result<string> head;
        try
        {
            head = await ResolveHeadAsync(worktree, baseRevision, manifest.RunId, stagedEntries.Count, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<FrozenChangeSet>($"Freeze of run '{manifest.RunId}' was cancelled.");
        }
        if (head.IsFailure)
            return head.ConvertFailure<FrozenChangeSet>();
        string headRevision = head.Value;

        // The frozen diff is authoritative: re-derive it commit-to-commit so
        // the artifact on disk is exactly what the head holds.
        Result<byte[]> frozenPatch;
        Result<string> status;
        try
        {
            frozenPatch = await DiffPatchAsync(worktree, baseRevision, headRevision, ct).ConfigureAwait(false);
            status = await PorcelainAsync(worktree, ct, ignored: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<FrozenChangeSet>($"Freeze of run '{manifest.RunId}' was cancelled.");
        }
        if (frozenPatch.IsFailure)
            return frozenPatch.ConvertFailure<FrozenChangeSet>();
        if (status.IsFailure)
            return status.ConvertFailure<FrozenChangeSet>();
        // Hoisted to the guard level (see the staged-diff hoist above).
        byte[] frozenBytes = frozenPatch.Value;
        string statusText = status.Value;
        if (!BytesEqual(frozenBytes, stagedPatch))
            return Result.Failure<FrozenChangeSet>(
                $"Freeze of run '{manifest.RunId}' failed at stage 'verify': " +
                "the committed diff does not match the staged diff — refusing to record a half-frozen set.");
        if (statusText.Length > 0)
            return Result.Failure<FrozenChangeSet>(
                $"Freeze of run '{manifest.RunId}' failed at stage 'verify': " +
                $"the worktree is not clean after the freeze ({FirstLine(statusText)}) — refusing to record a half-frozen set.");

        var set = FrozenChangeSet.Create(
            manifest.RunId, baseRevision, headRevision, patchPath, stagedEntries, ignored.Value);

        Result<string> writtenPatch = WriteBytesAtomic(patchPath, frozenBytes, manifest.RunId);
        if (writtenPatch.IsFailure)
            return writtenPatch.ConvertFailure<FrozenChangeSet>();
        Result<string> writtenJson = WriteChangesetJson(changesetPath, set);
        if (writtenJson.IsFailure)
            return writtenJson.ConvertFailure<FrozenChangeSet>();

        // The manifest moves only after both artifacts are on disk, through
        // the single state-writer (S6, #382) — never a reinterpreted state.
        Result<RunManifest> moved = WorkspaceMaterializer.TryAdvanceState(manifest.RunId, RunState.Changed);
        if (moved.IsFailure)
            return moved.ConvertFailure<FrozenChangeSet>();
        return Result.Success(set);
    }

    private sealed record StagedDiff(List<FrozenChangeEntry> Entries, byte[] Patch);

    /// <summary>
    ///     Stage everything (<c>add -A</c>: untracked files created by the
    ///     run are included) and diff the worktree against
    ///     <paramref name="baseRevision" />: inventory plus the would-be
    ///     patch, before any commit exists.
    /// </summary>
    private static async Task<Result<StagedDiff>> StageAndDiffAsync(
        string worktree, string baseRevision, string runId, CancellationToken ct)
    {
        Result<WorkspaceMaterializer.GitOutput> added =
            await WorkspaceMaterializer.RunGitAsync(
                worktree, ["add", "-A"], StageTimeout, ct).ConfigureAwait(false);
        if (added.IsFailure)
            return added.ConvertFailure<StagedDiff>();
        if (added.Value.ExitCode != 0)
            return Result.Failure<StagedDiff>(
                $"Freeze of run '{runId}' failed at stage 'add': {WorkspaceMaterializer.Clip(added.Value.Stderr)}");

        Result<IReadOnlyList<NameStatus>> names =
            await DiffNameStatusAsync(worktree, baseRevision, ct).ConfigureAwait(false);
        if (names.IsFailure)
            return names.ConvertFailure<StagedDiff>();
        Result<IReadOnlyList<NumStat>> counts =
            await DiffNumstatAsync(worktree, baseRevision, ct).ConfigureAwait(false);
        if (counts.IsFailure)
            return counts.ConvertFailure<StagedDiff>();
        Result<byte[]> patch =
            await DiffPatchAsync(worktree, baseRevision, null, ct).ConfigureAwait(false);
        if (patch.IsFailure)
            return patch.ConvertFailure<StagedDiff>();

        Result<List<FrozenChangeEntry>> joined = JoinEntries(names.Value, counts.Value);
        if (joined.IsFailure)
        {
            await ResetIndexAsync(worktree).ConfigureAwait(false);
            return joined.ConvertFailure<StagedDiff>();
        }
        return Result.Success(new StagedDiff(joined.Value, patch.Value));
    }

    /// <summary>
    ///     Resolve the frozen head: an empty change set commits nothing (the
    ///     head is whatever HEAD already says); otherwise commit under the
    ///     pinned identity — unless a previous freeze already committed, in
    ///     which case adopt the head and never stack a second commit.
    /// </summary>
    private static async Task<Result<string>> ResolveHeadAsync(
        string worktree, string baseRevision, string runId, int entryCount, CancellationToken ct)
    {
        // #1558: the guard + `.Value` pair is one `Bind` link now. The head
        // read still happens FIRST and its error still travels unchanged —
        // `Bind` re-types a failure to the chain's result type, which is what
        // the old `return head;` guard did — and the branch below is the
        // same branch it was.
        return await (await RevParseHeadAsync(worktree, ct).ConfigureAwait(false))
            .Bind(async current => entryCount == 0 || !current.Equals(baseRevision, StringComparison.Ordinal)
                ? Result.Success(current)
                : await CommitFrozenAsync(worktree, runId, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Idempotent re-freeze: a run past <c>Isolated</c> returns its
    ///     recorded artifact. The worktree head must still equal the recorded
    ///     head — a mismatch is a failure, never a silent re-freeze.
    /// </summary>
    private static async Task<Result<FrozenChangeSet>> ReadExistingAsync(
        RunManifest manifest, CancellationToken ct)
    {
        string runDir = WorkspacePaths.Default.RunDir(manifest.RunId);
        string patchPath = Path.Combine(runDir, "change.patch");
        string changesetPath = Path.Combine(runDir, "changeset.json");
        if (!File.Exists(patchPath) || !File.Exists(changesetPath))
            return Result.Failure<FrozenChangeSet>(
                $"Run '{manifest.RunId}' is {manifest.State} but its frozen artifacts are missing " +
                $"('{patchPath}', '{changesetPath}') — cannot re-freeze a run that never froze.");

        Result<string> head;
        try
        {
            head = await RevParseHeadAsync(manifest.WorktreePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<FrozenChangeSet>($"Freeze of run '{manifest.RunId}' was cancelled.");
        }

        // #1558: the head read and the recorded-set read are one rail. Both
        // links short-circuit on failure and hand the SAME error on — the
        // old guards re-typed it with `ConvertFailure<FrozenChangeSet>()`
        // and returned the recorded result verbatim — so the messages and
        // the result type are unchanged.
        return head
            .Bind(currentHead => ReadChangesetJson(changesetPath, patchPath)
                .Bind(existing => existing.HeadRevision.Equals(currentHead, StringComparison.Ordinal)
                    ? Result.Success(existing)
                    : Result.Failure<FrozenChangeSet>(
                        $"Run '{manifest.RunId}' is {manifest.State} at recorded head {existing.HeadRevision} " +
                        $"but the worktree is at {currentHead} — refusing to present a stale frozen set.")));
    }

    private sealed record NameStatus(string Status, string Path, string? OldPath);

    private sealed record NumStat(string Path, string? OldPath, int? Insertions, int? Deletions, bool Binary);

    private static Result<List<FrozenChangeEntry>> JoinEntries(
        IReadOnlyList<NameStatus> names, IReadOnlyList<NumStat> counts)
    {
        var byNew = new Dictionary<string, NumStat>(StringComparer.Ordinal);
        var byPair = new Dictionary<string, NumStat>(StringComparer.Ordinal);
        for (int i = 0; i < counts.Count; i++)
        {
            NumStat c = counts[i];
            if (c.OldPath is null)
                byNew[c.Path] = c;
            else
                byPair[c.OldPath + "\0" + c.Path] = c;
        }

        var entries = new List<FrozenChangeEntry>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            NameStatus n = names[i];
            NumStat? c;
            if (n.OldPath is null)
            {
                if (!byNew.TryGetValue(n.Path, out c))
                    return Result.Failure<List<FrozenChangeEntry>>(
                        $"Freeze failed at stage 'inventory': no numstat for '{n.Path}' — refusing to record a partial set.");
            }
            else
            {
                if (!byPair.TryGetValue(n.OldPath + "\0" + n.Path, out c))
                    return Result.Failure<List<FrozenChangeEntry>>(
                        $"Freeze failed at stage 'inventory': no numstat for rename '{n.OldPath} -> {n.Path}' — refusing to record a partial set.");
            }
            entries.Add(FrozenChangeEntry.Create(n.Path, n.Status, n.OldPath, c.Insertions, c.Deletions, c.Binary));
        }
        return Result.Success(entries);
    }

    private static async Task<Result<int>> CountIgnoredAsync(string worktree, CancellationToken ct)
    {
        return (await PorcelainAsync(worktree, ct, ignored: true).ConfigureAwait(false))
            .Map(CountIgnoredLines);
    }

    private static int CountIgnoredLines(string porcelain)
    {
        int ignored = 0;
        ReadOnlySpan<char> rest = porcelain.AsSpan();
        while (true)
        {
            int nl = rest.IndexOf('\n');
            ReadOnlySpan<char> line = nl < 0 ? rest : rest.Slice(0, nl);
            if (line.StartsWith("!!", StringComparison.Ordinal))
                ignored++;
            if (nl < 0)
                break;
            rest = rest.Slice(nl + 1);
        }
        return ignored;
    }

    private static async Task<Result<string>> PorcelainAsync(string worktree, CancellationToken ct, bool ignored)
    {
        string[] argv = ignored
            ? ["status", "--porcelain=v1", "--ignored"]
            : ["status", "--porcelain=v1"];
        return (await WorkspaceMaterializer.RunGitAsync(worktree, argv, ProbeTimeout, ct).ConfigureAwait(false))
            .Bind(o => o.ExitCode != 0
                ? Result.Failure<string>(
                    $"Freeze failed at stage 'status': {WorkspaceMaterializer.Clip(o.Stderr)}")
                : Result.Success(o.Stdout));
    }

    private static async Task<Result<IReadOnlyList<NameStatus>>> DiffNameStatusAsync(
        string worktree, string baseRevision, CancellationToken ct)
    {
        return (await WorkspaceMaterializer.RunGitAsync(
                worktree,
                ["diff", "--name-status", "-M", "-z", baseRevision],
                DiffTimeout, ct).ConfigureAwait(false))
            .Bind(o => o.ExitCode != 0
                ? Result.Failure<IReadOnlyList<NameStatus>>(
                    $"Freeze failed at stage 'inventory': {WorkspaceMaterializer.Clip(o.Stderr)}")
                : ParseNameStatus(o.Stdout));
    }

    private static Result<IReadOnlyList<NameStatus>> ParseNameStatus(string stdout)
    {
        var entries = new List<NameStatus>();
        ReadOnlySpan<char> rest = stdout.AsSpan();
        while (TryReadNulToken(ref rest, out ReadOnlySpan<char> token))
        {
            if (token.IsEmpty)
                continue;
            char kind = token[0];
            bool rename = kind is 'R' or 'C';
            if (rename)
            {
                if (!TryReadNulToken(ref rest, out ReadOnlySpan<char> oldPath)
                    || !TryReadNulToken(ref rest, out ReadOnlySpan<char> newPath))
                    return Result.Failure<IReadOnlyList<NameStatus>>(
                        "Freeze failed at stage 'inventory': truncated name-status — refusing to record a partial set.");
                entries.Add(new NameStatus(kind.ToString(), newPath.ToString(), oldPath.ToString()));
            }
            else
            {
                if (!TryReadNulToken(ref rest, out ReadOnlySpan<char> path))
                    return Result.Failure<IReadOnlyList<NameStatus>>(
                        "Freeze failed at stage 'inventory': truncated name-status — refusing to record a partial set.");
                entries.Add(new NameStatus(kind.ToString(), path.ToString(), null));
            }
        }
        return Result.Success<IReadOnlyList<NameStatus>>(entries);
    }

    /// <summary>
    ///     Read one raw <c>-z</c> token (NUL-separated), preserving empty
    ///     segments exactly like <c>string.Split('\0')</c> so the
    ///     truncated-output checks keep their semantics without the array.
    /// </summary>
    private static bool TryReadNulToken(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> token)
    {
        if (rest.IsEmpty)
        {
            token = default;
            return false;
        }
        int nul = rest.IndexOf('\0');
        if (nul < 0)
        {
            token = rest;
            rest = ReadOnlySpan<char>.Empty;
            return true;
        }
        token = rest.Slice(0, nul);
        rest = rest.Slice(nul + 1);
        return true;
    }

    private static async Task<Result<IReadOnlyList<NumStat>>> DiffNumstatAsync(
        string worktree, string baseRevision, CancellationToken ct)
    {
        return (await WorkspaceMaterializer.RunGitAsync(
                worktree,
                ["diff", "--numstat", "-M", "-z", baseRevision],
                DiffTimeout, ct).ConfigureAwait(false))
            .Bind(o => o.ExitCode != 0
                ? Result.Failure<IReadOnlyList<NumStat>>(
                    $"Freeze failed at stage 'inventory': {WorkspaceMaterializer.Clip(o.Stderr)}")
                : ParseNumstat(o.Stdout));
    }

    private static Result<IReadOnlyList<NumStat>> ParseNumstat(string stdout)
    {
        var entries = new List<NumStat>();
        ReadOnlySpan<char> rest = stdout.AsSpan();
        while (TryReadNulToken(ref rest, out ReadOnlySpan<char> token))
        {
            if (token.IsEmpty)
                continue;
            int tab = token.IndexOf('\t');
            if (tab < 0)
                return Result.Failure<IReadOnlyList<NumStat>>(
                    "Freeze failed at stage 'inventory': malformed numstat — refusing to record a partial set.");
            int rel = token.Slice(tab + 1).IndexOf('\t');
            if (rel < 0)
                return Result.Failure<IReadOnlyList<NumStat>>(
                    "Freeze failed at stage 'inventory': malformed numstat — refusing to record a partial set.");
            int tab2 = tab + 1 + rel;
            ReadOnlySpan<char> added = token.Slice(0, tab);
            ReadOnlySpan<char> deleted = token.Slice(tab + 1, tab2 - tab - 1);
            ReadOnlySpan<char> remainder = token.Slice(tab2 + 1);
            bool binary = added.Length == 1 && added[0] == '-'
                && deleted.Length == 1 && deleted[0] == '-';
            if (remainder.IsEmpty)
            {
                // Rename/copy with -z: the two paths arrive as the next tokens.
                if (!TryReadNulToken(ref rest, out ReadOnlySpan<char> oldPath)
                    || !TryReadNulToken(ref rest, out ReadOnlySpan<char> newPath))
                    return Result.Failure<IReadOnlyList<NumStat>>(
                        "Freeze failed at stage 'inventory': truncated numstat — refusing to record a partial set.");
                entries.Add(MakeNumStat(newPath.ToString(), oldPath.ToString(), added, deleted, binary));
            }
            else
            {
                entries.Add(MakeNumStat(remainder.ToString(), null, added, deleted, binary));
            }
        }
        return Result.Success<IReadOnlyList<NumStat>>(entries);
    }

    private static NumStat MakeNumStat(string path, string? oldPath, ReadOnlySpan<char> added, ReadOnlySpan<char> deleted, bool binary)
    {
        if (binary)
            return new NumStat(path, oldPath, null, null, true);
        int insertions = int.TryParse(added, NumberStyles.None, CultureInfo.InvariantCulture, out int a) ? a : 0;
        int deletions = int.TryParse(deleted, NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? d : 0;
        return new NumStat(path, oldPath, insertions, deletions, false);
    }

    private static async Task<Result<byte[]>> DiffPatchAsync(
        string worktree, string baseRevision, string? headRevision, CancellationToken ct)
    {
        string[] argv = headRevision is null
            ? ["diff", "--binary", baseRevision]
            : ["diff", "--binary", baseRevision, headRevision];
        // Raw bytes would be ideal, but the S2 git helper decodes stdout as
        // text. Patches are UTF-8 by construction for text hunks; binary
        // hunks travel as ASCII base85 (`GIT binary patch`), so a
        // UTF-8 round-trip is lossless for every patch git emits here.
        return (await WorkspaceMaterializer.RunGitAsync(worktree, argv, DiffTimeout, ct).ConfigureAwait(false))
            .Bind(o => o.ExitCode != 0
                ? Result.Failure<byte[]>(
                    $"Freeze failed at stage 'diff': {WorkspaceMaterializer.Clip(o.Stderr)}")
                : Result.Success(System.Text.Encoding.UTF8.GetBytes(o.Stdout)));
    }

    private static async Task<Result<string>> RevParseHeadAsync(string worktree, CancellationToken ct)
    {
        return (await WorkspaceMaterializer.RunGitAsync(worktree, ["rev-parse", "HEAD"], ProbeTimeout, ct).ConfigureAwait(false))
            .Bind(o => o.ExitCode != 0
                ? Result.Failure<string>(
                    $"Freeze failed at stage 'head': {WorkspaceMaterializer.Clip(o.Stderr)}")
                : Result.Success(o.Stdout.Trim()))
            .Ensure(head => head.Length > 0, "Freeze failed at stage 'head': git reports an empty HEAD.");
    }

    private static async Task<Result<string>> CommitFrozenAsync(
        string worktree, string runId, CancellationToken ct)
    {
        string message = $"harbor: frozen change set for run {runId}";
        string[] argv =
        [
            "-c", $"user.name={FreezeAuthorName}",
            "-c", $"user.email={FreezeAuthorEmail}",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "-m", message,
        ];
        return await (await WorkspaceMaterializer.RunGitAsync(worktree, argv, CommitTimeout, ct).ConfigureAwait(false))
            .Bind(o => o.ExitCode != 0
                ? Result.Failure<WorkspaceMaterializer.GitOutput>(
                    $"Freeze of run '{runId}' failed at stage 'commit': {WorkspaceMaterializer.Clip(o.Stderr)}")
                : Result.Success(o))
            .Bind(_ => RevParseHeadAsync(worktree, ct)).ConfigureAwait(false);
    }

    private static async Task ResetIndexAsync(string worktree)
    {
        try
        {
            await WorkspaceMaterializer.RunGitAsync(
                worktree, ["reset", "-q"], ProbeTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static string FirstLine(string text)
    {
        int at = text.IndexOf('\n');
        string first = at < 0 ? text : text.Substring(0, at);
        first = first.Trim();
        return first.Length <= 120 ? first : first.Substring(0, 120) + "…";
    }

    private static Result<string> WriteBytesAtomic(string path, byte[] bytes, string runId)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Result.Failure<string>($"Cannot create run directory '{dir}': {ex.Message}");
            }
        }
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            return Result.Failure<string>($"Cannot write frozen patch for run '{runId}': {ex.Message}");
        }
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            DeleteQuiet(tmp);
            return Result.Failure<string>($"Cannot write frozen patch for run '{runId}': {ex.Message}");
        }
        return Result.Success(path);
    }

    private static Result<string> WriteChangesetJson(string path, FrozenChangeSet set)
    {
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("runId", set.RunId);
                writer.WriteString("baseRevision", set.BaseRevision);
                writer.WriteString("headRevision", set.HeadRevision);
                writer.WriteBoolean("isEmpty", set.IsEmpty);
                writer.WriteNumber("ignoredPathCount", set.IgnoredPathCount);
                writer.WriteStartArray("entries");
                for (int i = 0; i < set.Entries.Count; i++)
                {
                    FrozenChangeEntry e = set.Entries[i];
                    writer.WriteStartObject();
                    writer.WriteString("path", e.Path);
                    writer.WriteString("status", e.Status);
                    if (e.OldPath is null)
                        writer.WriteNull("oldPath");
                    else
                        writer.WriteString("oldPath", e.OldPath);
                    if (e.Insertions is null)
                        writer.WriteNull("insertions");
                    else
                        writer.WriteNumber("insertions", e.Insertions.Value);
                    if (e.Deletions is null)
                        writer.WriteNull("deletions");
                    else
                        writer.WriteNumber("deletions", e.Deletions.Value);
                    writer.WriteBoolean("binary", e.Binary);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Result.Failure<string>($"Cannot serialize frozen change set for run '{set.RunId}': {ex.Message}");
        }
        return WriteBytesAtomic(path, bytes, set.RunId);
    }

    private static Result<FrozenChangeSet> ReadChangesetJson(string changesetPath, string patchPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(changesetPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return Result.Failure<FrozenChangeSet>($"Cannot read frozen change set at '{changesetPath}': {ex.Message}");
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' is not valid JSON: {ex.Message}");
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' is not a JSON object.");
            string? runId = GetString(root, "runId");
            string? baseRevision = GetString(root, "baseRevision");
            string? headRevision = GetString(root, "headRevision");
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(baseRevision) || string.IsNullOrWhiteSpace(headRevision))
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' is missing required fields.");
            if (!root.TryGetProperty("ignoredPathCount", out JsonElement ignoredEl)
                || ignoredEl.ValueKind != JsonValueKind.Number
                || !ignoredEl.TryGetInt32(out int ignoredPathCount)
                || ignoredPathCount < 0)
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' has an invalid 'ignoredPathCount'.");
            if (!root.TryGetProperty("entries", out JsonElement entriesEl)
                || entriesEl.ValueKind != JsonValueKind.Array)
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' has an invalid 'entries' array.");
            var entries = new List<FrozenChangeEntry>(entriesEl.GetArrayLength());
            foreach (JsonElement el in entriesEl.EnumerateArray())
            {
                Result<FrozenChangeEntry> entry = ReadEntry(el);
                if (entry.IsFailure)
                    return entry.ConvertFailure<FrozenChangeSet>()
                        .MapError(e => $"Frozen change set at '{changesetPath}' has an invalid entry: {e}");
                // Hoisted to the guard level (see FreezeIsolatedAsync).
                FrozenChangeEntry parsed = entry.Value;
                entries.Add(parsed);
            }
            return Result.Success(FrozenChangeSet.Create(
                runId, baseRevision, headRevision, patchPath, entries, ignoredPathCount));
        }
    }

    private static Result<FrozenChangeEntry> ReadEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return Result.Failure<FrozenChangeEntry>("entry is not a JSON object.");
        string? path = GetString(el, "path");
        string? status = GetString(el, "status");
        string? oldPath = GetString(el, "oldPath");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(status))
            return Result.Failure<FrozenChangeEntry>("entry is missing 'path' or 'status'.");
        int? insertions = GetNullableInt(el, "insertions");
        int? deletions = GetNullableInt(el, "deletions");
        bool binary = el.TryGetProperty("binary", out JsonElement binEl)
            && binEl.ValueKind == JsonValueKind.True;
        try
        {
            return Result.Success(FrozenChangeEntry.Create(path, status, oldPath, insertions, deletions, binary));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<FrozenChangeEntry>(ex.Message);
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null,
        };
    }

    private static int? GetNullableInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n) && n >= 0)
            return n;
        return null;
    }

    private static Result<string> ValidateRunId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<string>("Run ID cannot be empty.");
        if (value != Path.GetFileName(value) || value is "." or "..")
            return Result.Failure<string>($"Invalid run id '{value}': path separators are not allowed.");
        return Result.Success(value);
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _ = ex;
        }
    }
}
