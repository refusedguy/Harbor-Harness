using System.Globalization;
using System.Text;

namespace Harbor.Application.Sessions;

/// <summary>
///     One path in the frozen change set (epic #42, slice S5, #379): the
///     porcelain status plus the path, exactly as the freeze recorded it.
///     An input fact, not a verdict — ordering and derivation live in
///     <see cref="ChangeReport" />.
/// </summary>
public sealed record ChangedPathEntry
{
    private ChangedPathEntry(string path, string status)
    {
        Path = path;
        Status = status;
    }

    /// <summary>Repo-relative path from the frozen set. Never blank.</summary>
    public string Path { get; }

    /// <summary>Porcelain status (<c>A</c>, <c>M</c>, <c>D</c>, <c>R</c>, …). Never blank.</summary>
    public string Status { get; }

    /// <summary>
    ///     Record one frozen path. Blank path or status is a violated
    ///     precondition (<see cref="ArgumentException" />), not a fact about
    ///     the world — the freeze never produces one.
    /// </summary>
    public static ChangedPathEntry Create(string path, string status)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A changed path must name the path.", nameof(path));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("A changed path must carry its porcelain status.", nameof(status));
        return new ChangedPathEntry(path, status);
    }
}

/// <summary>
///     One check the run attempted (epic #42, slice S5, #379): what was run
///     and what came back. An input fact — whether it counts as evidence is
///     derived by <see cref="ChangeReport" />, never stored here.
/// </summary>
public sealed record RecordedCheck
{
    private RecordedCheck(
        string name,
        string command,
        int? exitCode,
        string? revision,
        IReadOnlyList<string> envKeys,
        string? missReason,
        bool timedOut)
    {
        Name = name;
        Command = command;
        ExitCode = exitCode;
        Revision = revision;
        EnvKeys = envKeys;
        MissReason = missReason;
        TimedOut = timedOut;
    }

    /// <summary>Check name from the operator's declaration. Never blank.</summary>
    public string Name { get; }

    /// <summary>The exact command line that was executed. Never blank.</summary>
    public string Command { get; }

    /// <summary>Process exit code, or <c>null</c> when the check never ran.</summary>
    public int? ExitCode { get; }

    /// <summary>
    ///     Revision the check ran against, or <c>null</c> when none was
    ///     recorded. A check with no recorded revision can never appear under
    ///     <c>Verified</c>.
    /// </summary>
    public string? Revision { get; }

    /// <summary>Effective environment keys, sorted. Never null.</summary>
    public IReadOnlyList<string> EnvKeys { get; }

    /// <summary>
    ///     Why the check produced no result (<c>null</c> when it ran).
    ///     Non-blank exactly when <see cref="ExitCode" /> is <c>null</c>.
    /// </summary>
    public string? MissReason { get; }

    /// <summary>Whether the check was killed by its timeout.</summary>
    public bool TimedOut { get; }

    /// <summary>A check that ran and exited zero.</summary>
    public static RecordedCheck Passed(
        string name, string command, string revision, IReadOnlyList<string> envKeys)
    {
        RequireNameCommand(name, command);
        RequireRevision(revision);
        return new RecordedCheck(name, command, 0, revision, SortedKeys(envKeys), null, false);
    }

    /// <summary>A check that ran and exited non-zero.</summary>
    public static RecordedCheck Failed(
        string name, string command, int exitCode, string revision, IReadOnlyList<string> envKeys)
    {
        RequireNameCommand(name, command);
        RequireRevision(revision);
        if (exitCode == 0)
            throw new ArgumentException("A failed check cannot exit 0; use Passed.", nameof(exitCode));
        return new RecordedCheck(name, command, exitCode, revision, SortedKeys(envKeys), null, false);
    }

    /// <summary>
    ///     A check that never produced a result: timeout, spawn failure,
    ///     cancellation, or no recorded revision. There is no exit code
    ///     because the process never produced one.
    /// </summary>
    public static RecordedCheck DidNotRun(
        string name,
        string command,
        string reason,
        string? revision,
        IReadOnlyList<string> envKeys,
        bool timedOut = false)
    {
        RequireNameCommand(name, command);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A check that did not run must say why.", nameof(reason));
        if (revision is not null && string.IsNullOrWhiteSpace(revision))
            throw new ArgumentException("A recorded revision must not be blank.", nameof(revision));
        return new RecordedCheck(name, command, null, revision, SortedKeys(envKeys), reason, timedOut);
    }

    private static void RequireNameCommand(string name, string command)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A check must have a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("A check must name the command it ran.", nameof(command));
    }

    private static void RequireRevision(string revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
            throw new ArgumentException(
                "A check that ran must name the revision it ran against.", nameof(revision));
    }

    private static IReadOnlyList<string> SortedKeys(IReadOnlyList<string> envKeys)
    {
        ArgumentNullException.ThrowIfNull(envKeys);
        var copy = new List<string>(envKeys.Count);
        for (int i = 0; i < envKeys.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(envKeys[i]))
                throw new ArgumentException("Environment keys must not be blank.", nameof(envKeys));
            copy.Add(envKeys[i]);
        }
        copy.Sort(StringComparer.Ordinal);
        return copy;
    }
}

/// <summary>
///     One entry under <c>Verified</c> (epic #42, slice S5, #379): a check
///     that exited zero on the frozen head, with everything a reader needs to
///     re-run it. Internal construction — only <see cref="ChangeReport" />
///     builds these, from the S3/S4 artifacts it is given.
/// </summary>
public sealed record VerifiedCheckEntry
{
    internal VerifiedCheckEntry(
        string name, string command, int exitCode, string revision, IReadOnlyList<string> envKeys)
    {
        Name = name;
        Command = command;
        ExitCode = exitCode;
        Revision = revision;
        EnvKeys = envKeys;
    }

    /// <summary>Check name from the operator's declaration.</summary>
    public string Name { get; }

    /// <summary>The exact command line that was executed.</summary>
    public string Command { get; }

    /// <summary>Process exit code (always zero here).</summary>
    public int ExitCode { get; }

    /// <summary>Revision it ran on (always the frozen head here).</summary>
    public string Revision { get; }

    /// <summary>Effective environment keys, sorted.</summary>
    public IReadOnlyList<string> EnvKeys { get; }
}

/// <summary>
///     One entry under <c>NotVerified</c> (epic #42, slice S5, #379):
///     something deliberately not verified, and why. Internal construction —
///     only <see cref="ChangeReport" /> builds these, by derivation.
/// </summary>
public sealed record UnverifiedEntry
{
    internal UnverifiedEntry(string what, string reason)
    {
        What = what;
        Reason = reason;
    }

    /// <summary>What was not verified. Never blank.</summary>
    public string What { get; }

    /// <summary>Why it was not verified. Never blank.</summary>
    public string Reason { get; }
}

/// <summary>
///     Verification report (epic #42, slice S5, #379): the deliverable
///     rendering of recorded artifacts only. <c>Changed</c> comes from the
///     frozen change set (S3), <c>Verified</c> from the check results (S4);
///     <c>NotVerified</c> and <c>KnownRisks</c> are derived, never authored.
/// </summary>
/// <remarks>
///     <para>
///         <b>Nothing here is independently settable.</b> The only constructor
///         is private, the copy constructor is private too (so <c>with</c>
///         cannot slip in an unvalidated value), and
///         <see cref="VerifiedCheckEntry" /> / <see cref="UnverifiedEntry" />
///         are internal-constructible. Every instance is built by
///         <see cref="Create" />, which is what lets the derivation below be
///         trusted: the agent has no API to add or remove
///         <c>Verified</c> / <c>NotVerified</c> entries.
///     </para>
///     <para>
///         <b>Deterministic.</b> Entries are sorted (ordinal), JSON keys are
///         written in a fixed order, and no clock is read — the same inputs
///         produce byte-identical output. Timestamps live in the manifest
///         only, never in the report body.
///     </para>
/// </remarks>
public sealed record ChangeReport
{
    /// <summary>Exit code: all declared checks passed (or none were declared).</summary>
    public const int ExitAllPassed = 0;

    /// <summary>Exit code: at least one check failed, timed out, or never ran.</summary>
    public const int ExitCheckFailed = 1;

    /// <summary>
    ///     Exit code (for the future <c>harbor run report</c> verb): the report
    ///     cannot be produced — missing or corrupt artifact.
    /// </summary>
    public const int ExitCannotProduce = 2;

    /// <summary>Plain-text width. The text rendering never exceeds this.</summary>
    public const int TextWidth = 80;

    /// <summary>
    ///     Everything outside the change set is, by construction, not
    ///     observable by the report. Always the last <c>NotVerified</c> entry.
    /// </summary>
    public static readonly string ExternalSideEffectsEntry =
        "external side effects: not observable";

    private ChangeReport(
        string runId,
        string baseRevision,
        string headRevision,
        IReadOnlyList<ChangedPathEntry> changed,
        IReadOnlyList<VerifiedCheckEntry> verified,
        IReadOnlyList<UnverifiedEntry> notVerified,
        int timeoutSeconds,
        int maxSteps,
        IReadOnlyList<string> knownRisks,
        int exitCode)
    {
        RunId = runId;
        BaseRevision = baseRevision;
        HeadRevision = headRevision;
        Changed = changed;
        Verified = verified;
        NotVerified = notVerified;
        TimeoutSeconds = timeoutSeconds;
        MaxSteps = maxSteps;
        KnownRisks = knownRisks;
        ExitCode = exitCode;
    }

    /// <summary>
    ///     Private on purpose: <c>with</c> is the standard way to build a
    ///     near-copy that skips <see cref="Create" />, so it is closed here
    ///     and "the report cannot hold an underived value" is a property of
    ///     the type rather than a convention.
    /// </summary>
    private ChangeReport(ChangeReport original)
        : this(
            original.RunId, original.BaseRevision, original.HeadRevision,
            original.Changed, original.Verified, original.NotVerified,
            original.TimeoutSeconds, original.MaxSteps, original.KnownRisks, original.ExitCode)
    {
    }

    /// <summary>Minted run id (matches the run directory name).</summary>
    public string RunId { get; }

    /// <summary>Pinned base revision the freeze diffed against.</summary>
    public string BaseRevision { get; }

    /// <summary>Frozen head revision the checks ran against.</summary>
    public string HeadRevision { get; }

    /// <summary>Frozen change set, sorted by path (ordinal).</summary>
    public IReadOnlyList<ChangedPathEntry> Changed { get; }

    /// <summary>
    ///     Checks that exited zero on the frozen head, sorted by name.
    ///     A check with no recorded revision never appears here.
    /// </summary>
    public IReadOnlyList<VerifiedCheckEntry> Verified { get; }

    /// <summary>
    ///     Derived, never authored: non-zero / timed-out / spawn-failed /
    ///     cancelled checks, revision mismatches, the <c>no checks
    ///     declared</c> case, ignored-path exclusions, and the external side
    ///     effects constant.
    /// </summary>
    public IReadOnlyList<UnverifiedEntry> NotVerified { get; }

    /// <summary>Wall-clock budget from the contract limits, in seconds.</summary>
    public int TimeoutSeconds { get; }

    /// <summary>Max agent steps from the contract limits.</summary>
    public int MaxSteps { get; }

    /// <summary>
    ///     Derived risks, sorted. Non-empty whenever ignored paths were
    ///     dropped, the change set is empty, the worktree holds state not in
    ///     the frozen set, or a check timed out — otherwise explicitly empty.
    /// </summary>
    public IReadOnlyList<string> KnownRisks { get; }

    /// <summary>
    ///     <c>0</c> when every declared check passed; <c>1</c> when at least
    ///     one failed, timed out, or never ran. A run with no declared checks
    ///     exits <c>0</c> with the explicit <c>no checks declared</c> entry —
    ///     zero checks is legal (S4), not a failure.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    ///     Build the report from recorded artifacts. Blank ids or revisions,
    ///     a negative ignored-path count, or non-positive limits are violated
    ///     preconditions (<see cref="ArgumentException" />).
    /// </summary>
    /// <param name="runId">Minted run id.</param>
    /// <param name="baseRevision">Pinned base revision (from S1/S2).</param>
    /// <param name="headRevision">Frozen head revision (from S3).</param>
    /// <param name="changed">Frozen change set entries (from S3).</param>
    /// <param name="checks">Recorded check results (from S4).</param>
    /// <param name="ignoredPathCount">Git-ignored paths excluded from the freeze (from S3).</param>
    /// <param name="timeoutSeconds">Wall-clock budget from the contract limits.</param>
    /// <param name="maxSteps">Max agent steps from the contract limits.</param>
    /// <param name="worktreeDirty">
    ///     Whether the worktree holds state not in the frozen set
    ///     (tracked dirt observed after the freeze).
    /// </param>
    public static ChangeReport Create(
        string runId,
        string baseRevision,
        string headRevision,
        IReadOnlyList<ChangedPathEntry> changed,
        IReadOnlyList<RecordedCheck> checks,
        int ignoredPathCount,
        int timeoutSeconds,
        int maxSteps,
        bool worktreeDirty)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("A report must name its run.", nameof(runId));
        if (string.IsNullOrWhiteSpace(baseRevision))
            throw new ArgumentException("A report must name its base revision.", nameof(baseRevision));
        if (string.IsNullOrWhiteSpace(headRevision))
            throw new ArgumentException("A report must name its frozen head.", nameof(headRevision));
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(checks);
        if (ignoredPathCount < 0)
            throw new ArgumentException("The ignored-path count cannot be negative.", nameof(ignoredPathCount));
        if (timeoutSeconds <= 0)
            throw new ArgumentException("The timeout budget must be positive.", nameof(timeoutSeconds));
        if (maxSteps <= 0)
            throw new ArgumentException("The step budget must be positive.", nameof(maxSteps));

        var sortedChanged = new List<ChangedPathEntry>(changed);
        sortedChanged.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));

        var verified = new List<VerifiedCheckEntry>();
        var notVerified = new List<UnverifiedEntry>();
        bool anyFailed = false;
        bool anyTimedOut = false;

        if (checks.Count == 0)
        {
            notVerified.Add(new UnverifiedEntry("checks", "no checks declared"));
        }

        for (int i = 0; i < checks.Count; i++)
        {
            RecordedCheck check = checks[i];
            ArgumentNullException.ThrowIfNull(check);
            if (check.ExitCode.HasValue && !check.TimedOut && check.MissReason is null)
            {
                if (check.ExitCode.Value != 0)
                {
                    anyFailed = true;
                    notVerified.Add(new UnverifiedEntry(
                        check.Name,
                        $"exited {check.ExitCode.Value.ToString(CultureInfo.InvariantCulture)} " +
                        $"on revision {check.Revision}"));
                    continue;
                }
                if (!string.Equals(check.Revision, headRevision, StringComparison.Ordinal))
                {
                    notVerified.Add(new UnverifiedEntry(
                        check.Name,
                        $"ran on revision {check.Revision}, which is not the frozen head {headRevision} — " +
                        "it says nothing about the frozen tree"));
                    continue;
                }
                verified.Add(new VerifiedCheckEntry(
                    check.Name, check.Command, check.ExitCode.Value, check.Revision!, check.EnvKeys));
                continue;
            }

            anyFailed = true;
            if (check.TimedOut)
                anyTimedOut = true;
            notVerified.Add(new UnverifiedEntry(
                check.Name,
                check.Revision is null
                    ? $"{DescribeMiss(check)}; no revision was recorded"
                    : $"{DescribeMiss(check)} (declared revision {check.Revision})"));
        }

        verified.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        if (ignoredPathCount > 0)
        {
            notVerified.Add(new UnverifiedEntry(
                "ignored paths",
                $"{ignoredPathCount.ToString(CultureInfo.InvariantCulture)} " +
                "path(s) excluded from the freeze by .gitignore were not examined"));
        }

        notVerified.Add(new UnverifiedEntry("external side effects", "not observable"));
        notVerified.Sort(static (a, b) =>
        {
            int byWhat = string.Compare(a.What, b.What, StringComparison.Ordinal);
            return byWhat != 0
                ? byWhat
                : string.Compare(a.Reason, b.Reason, StringComparison.Ordinal);
        });

        var knownRisks = new List<string>();
        if (ignoredPathCount > 0)
            knownRisks.Add(
                $"ignored paths were dropped from the freeze " +
                $"({ignoredPathCount.ToString(CultureInfo.InvariantCulture)}); they were not examined");
        if (sortedChanged.Count == 0)
            knownRisks.Add("the change set is empty — nothing was proven about nothing");
        if (worktreeDirty)
            knownRisks.Add("the worktree holds state not in the frozen set");
        if (anyTimedOut)
            knownRisks.Add("a check timed out — the frozen tree may still be broken where it was not examined");
        knownRisks.Sort(StringComparer.Ordinal);

        return new ChangeReport(
            runId, baseRevision, headRevision, sortedChanged, verified, notVerified,
            timeoutSeconds, maxSteps, knownRisks, anyFailed ? ExitCheckFailed : ExitAllPassed);
    }

    private static string DescribeMiss(RecordedCheck check)
    {
        if (check.TimedOut)
            return $"timed out: {check.MissReason}";
        return check.MissReason ?? "did not run";
    }

    /// <summary>
    ///     Machine rendering: deterministic JSON with sorted keys in a fixed
    ///     order. The same inputs produce byte-identical output; no clock is
    ///     read. <c>knownRisks</c> is always present (possibly <c>[]</c>).
    /// </summary>
    public byte[] ToJsonBytes()
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("runId", RunId);
            writer.WriteString("baseRevision", BaseRevision);
            writer.WriteString("headRevision", HeadRevision);

            writer.WriteStartArray("changed");
            for (int i = 0; i < Changed.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("path", Changed[i].Path);
                writer.WriteString("status", Changed[i].Status);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("verified");
            for (int i = 0; i < Verified.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("command", Verified[i].Command);
                writer.WriteString("envKeys", string.Join(",", Verified[i].EnvKeys));
                writer.WriteNumber("exitCode", Verified[i].ExitCode);
                writer.WriteString("name", Verified[i].Name);
                writer.WriteString("revision", Verified[i].Revision);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("notVerified");
            for (int i = 0; i < NotVerified.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("reason", NotVerified[i].Reason);
                writer.WriteString("what", NotVerified[i].What);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("limits");
            writer.WriteNumber("maxSteps", MaxSteps);
            writer.WriteNumber("timeoutSeconds", TimeoutSeconds);
            writer.WriteEndObject();

            writer.WriteStartArray("knownRisks");
            for (int i = 0; i < KnownRisks.Count; i++)
                writer.WriteStringValue(KnownRisks[i]);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    ///     Plain-text rendering: six blocks in fixed order, always present —
    ///     an empty block prints <c>(none)</c> and is never omitted. Fits
    ///     <see cref="TextWidth" /> columns, contains no ANSI escapes, and is
    ///     identical when stdout is redirected.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        var runLines = new List<string>(3) { RunId, $"base {BaseRevision}", $"head {HeadRevision}" };
        AppendBlock(sb, "Run", runLines.Count, runLines);

        var changedLines = new List<string>(Changed.Count);
        for (int i = 0; i < Changed.Count; i++)
            changedLines.Add($"{Changed[i].Status} {Changed[i].Path}");
        AppendBlock(sb, "Changed", Changed.Count, changedLines);

        var verifiedLines = new List<string>();
        for (int i = 0; i < Verified.Count; i++)
        {
            VerifiedCheckEntry v = Verified[i];
            verifiedLines.Add($"{v.Name}: {v.Command}");
            verifiedLines.Add(
                $"    exit {v.ExitCode.ToString(CultureInfo.InvariantCulture)}, " +
                $"rev {v.Revision}, env [{string.Join(", ", v.EnvKeys)}]");
        }
        AppendBlock(sb, "Verified", Verified.Count, verifiedLines);

        var notVerifiedLines = new List<string>(NotVerified.Count);
        for (int i = 0; i < NotVerified.Count; i++)
            notVerifiedLines.Add($"{NotVerified[i].What}: {NotVerified[i].Reason}");
        AppendBlock(sb, "NotVerified", NotVerified.Count, notVerifiedLines);

        AppendBlock(
            sb, "Limits", 2,
            [$"timeout {TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s",
             $"max steps {MaxSteps.ToString(CultureInfo.InvariantCulture)}"]);

        AppendBlock(sb, "KnownRisks", KnownRisks.Count, KnownRisks);
        return sb.ToString();
    }

    private static void AppendBlock(
        StringBuilder sb, string name, int count, IReadOnlyList<string> lines)
    {
        sb.Append("== ").Append(name).Append(" (")
            .Append(count.ToString(CultureInfo.InvariantCulture)).AppendLine(") ==");
        if (lines.Count == 0)
        {
            sb.AppendLine("  (none)");
            sb.AppendLine();
            return;
        }
        for (int i = 0; i < lines.Count; i++)
            AppendWrapped(sb, lines[i]);
        sb.AppendLine();
    }

    private static void AppendWrapped(StringBuilder sb, string line)
    {
        const string firstIndent = "  ";
        const string contIndent = "    ";
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            sb.AppendLine(firstIndent + "(none)");
            return;
        }
        var current = new StringBuilder(firstIndent, TextWidth + 16);
        bool fresh = true;
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            if (word.Length > TextWidth - contIndent.Length)
            {
                if (!fresh)
                {
                    sb.AppendLine(current.ToString());
                    current.Clear().Append(contIndent);
                }
                AppendOverlongWord(sb, word, current.ToString(), contIndent);
                current.Clear().Append(contIndent);
                fresh = true;
                continue;
            }
            int need = word.Length + (fresh ? 0 : 1);
            if (current.Length + need > TextWidth)
            {
                sb.AppendLine(current.ToString());
                current.Clear().Append(contIndent);
                fresh = true;
            }
            if (!fresh)
                current.Append(' ');
            current.Append(word);
            fresh = false;
        }
        if (!fresh)
            sb.AppendLine(current.ToString());
    }

    private static void AppendOverlongWord(StringBuilder sb, string word, string head, string contIndent)
    {
        int headWidth = TextWidth - head.Length;
        int firstLen = Math.Min(headWidth, word.Length);
        sb.Append(head).Append(word, 0, firstLen).AppendLine();
        int width = TextWidth - contIndent.Length;
        for (int at = firstLen; at < word.Length; at += width)
        {
            int len = Math.Min(width, word.Length - at);
            sb.Append(contIndent).Append(word, at, len).AppendLine();
        }
    }
}
