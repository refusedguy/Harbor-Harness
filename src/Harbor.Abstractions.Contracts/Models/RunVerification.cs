using System.Globalization;
using System.IO;
using System.Text;

namespace Harbor.Abstractions.Models;

/// <summary>
///     How ONE check ended (epic #41, slice B3.1).
/// </summary>
/// <remarks>
///     <para>
///         Three members and no fourth. <see cref="DidNotRun" /> is a fact about
///         the check — it produced no result — not an epistemic hedge about the
///         record; the record's own "we cannot tell" marker is
///         <see cref="RunEvidenceState.Unreadable" />, which lives on a
///         different axis entirely. Putting "unknown" in here would be a fourth
///         verdict and would let <see cref="DidNotRun" /> and "we do not know"
///         print the same word, which is the #711 shape (two meanings in one
///         field).
///     </para>
///     <para>
///         <see cref="DidNotRun" /> and <see cref="Failed" /> are opposites, not
///         near-neighbours: a failed check is evidence, and it is counted.
///     </para>
/// </remarks>
public enum RunCheckVerdict
{
    /// <summary>The command ran to completion and exited zero.</summary>
    Passed,

    /// <summary>The command ran to completion and exited non-zero.</summary>
    Failed,

    /// <summary>
    ///     The command never produced a result: the tool was missing, it timed
    ///     out, the tree was dirty, the workspace was not isolated. Carries a
    ///     non-empty reason and no exit code, because there is none.
    /// </summary>
    DidNotRun,
}

/// <summary>
///     What kind of thing an artifact is (epic #41, slice B3.1). The kind is
///     recorded so a reader can tell a test log from a build output without
///     guessing from the file name.
/// </summary>
public enum RunArtifactKind
{
    /// <summary>Captured stdout/stderr of a check.</summary>
    Log,

    /// <summary>Build or test output in a machine-readable form (trx, junit xml, coverage).</summary>
    Report,

    /// <summary>A patch or diff the run produced.</summary>
    Diff,

    /// <summary>Any other artifact; the path still has to be a path that exists.</summary>
    Other,
}

/// <summary>
///     One executed check: what was run, where, and what came back (epic #41,
///     slice B3.1).
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a fact, not a verdict.</b> A
///         <see cref="RunCheckVerdict.Passed" /> on revision
///         <c>abc123</c> in environment <c>linux/10.0</c> says exactly that
///         sentence and nothing else. It is checkable by re-running it, which is
///         what makes the record worth keeping; a bare "verified" string is an
///         opinion, cannot be re-checked, and goes stale silently.
///     </para>
///     <para>
///         <see cref="Revision" /> is the identity that keeps the record from
///         rotting. A check that passed on some other commit says nothing about
///         the pinned tree, and <see cref="RunVerificationRecord" /> enforces
///         that by not counting it — see that type's
///         <see cref="RunVerificationRecord.Verification" />.
///     </para>
///     <para>
///         Built through the three factories rather than a public constructor so
///         the two invariants that hold between fields cannot be violated: a
///         <see cref="RunCheckVerdict.DidNotRun" /> check must carry a reason
///         and must not carry an exit code, and a check that ran must carry one.
///         Those are <see cref="ArgumentException" />s rather than
///         <c>Result</c> failures on purpose — they are violated preconditions
///         on this type's own API, not facts about the world. A run-time fact
///         (an artifact path that is not there) is a <c>Result</c>, and is
///         handled that way in <see cref="RunArtifact.Create" />.
///     </para>
/// </remarks>
public sealed record RunCheck
{
    private RunCheck(
        string command,
        int? exitCode,
        string revision,
        string environment,
        TimeSpan duration,
        RunCheckVerdict verdict,
        string? reason)
    {
        Command = command;
        ExitCode = exitCode;
        Revision = revision;
        Environment = environment;
        Duration = duration;
        Verdict = verdict;
        Reason = reason;
    }

    /// <summary>The exact command line that was executed. Never blank.</summary>
    public string Command { get; }

    /// <summary>
    ///     The process exit code, or <c>null</c> when
    ///     <see cref="Verdict" /> is <see cref="RunCheckVerdict.DidNotRun" />.
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    ///     The revision this check actually ran against — the identity of what
    ///     was verified. Required, because a check without one describes no tree
    ///     at all.
    /// </summary>
    public string Revision { get; }

    /// <summary>
    ///     Environment fingerprint (OS/runtime/toolchain identity). Required for
    ///     the same reason as <see cref="Revision" />: "tests pass" is a claim
    ///     about an environment too.
    /// </summary>
    public string Environment { get; }

    /// <summary>Wall-clock time the check took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How this one check ended.</summary>
    public RunCheckVerdict Verdict { get; }

    /// <summary>
    ///     Why the check did not run. Required and non-blank exactly when
    ///     <see cref="Verdict" /> is <see cref="RunCheckVerdict.DidNotRun" />;
    ///     <c>null</c> otherwise.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    ///     A check that ran and exited zero.
    /// </summary>
    /// <param name="command">The executed command line.</param>
    /// <param name="exitCode">The exit code; must be zero.</param>
    /// <param name="revision">The revision the check ran against.</param>
    /// <param name="environment">Environment fingerprint.</param>
    /// <param name="duration">Wall-clock duration.</param>
    /// <returns>The recorded check.</returns>
    public static RunCheck Passed(
        string command,
        int exitCode,
        string revision,
        string environment,
        TimeSpan duration)
    {
        if (exitCode != 0)
        {
            throw new ArgumentException(
                $"A passed check cannot exit {exitCode}; use {nameof(Failed)}.", nameof(exitCode));
        }

        return Build(command, exitCode, revision, environment, duration, RunCheckVerdict.Passed, reason: null);
    }

    /// <summary>
    ///     A check that ran and exited non-zero.
    /// </summary>
    /// <param name="command">The executed command line.</param>
    /// <param name="exitCode">The non-zero exit code.</param>
    /// <param name="revision">The revision the check ran against.</param>
    /// <param name="environment">Environment fingerprint.</param>
    /// <param name="duration">Wall-clock duration.</param>
    /// <returns>The recorded check.</returns>
    public static RunCheck Failed(
        string command,
        int exitCode,
        string revision,
        string environment,
        TimeSpan duration)
    {
        if (exitCode == 0)
        {
            throw new ArgumentException(
                "A failed check cannot exit 0; use Passed, or DidNotRun if it never ran.", nameof(exitCode));
        }

        return Build(command, exitCode, revision, environment, duration, RunCheckVerdict.Failed, reason: null);
    }

    /// <summary>
    ///     A check that could not run — missing tool, timeout, dirty tracked
    ///     tree, workspace not isolated. There is no exit code because the
    ///     process never produced one, and the reason is mandatory because
    ///     "it did not run" with no reason is indistinguishable from "nobody
    ///     tried".
    /// </summary>
    /// <param name="command">The command that was attempted.</param>
    /// <param name="reason">Why it could not run. Must be non-blank.</param>
    /// <param name="revision">The revision the check would have run against.</param>
    /// <param name="environment">Environment fingerprint.</param>
    /// <param name="duration">Time spent before it gave up.</param>
    /// <returns>The recorded check.</returns>
    public static RunCheck DidNotRun(
        string command,
        string reason,
        string revision,
        string environment,
        TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A check that could not run must say why; an absent reason reads as 'nobody tried'.",
                nameof(reason));
        }

        return Build(
            command, exitCode: null, revision, environment, duration, RunCheckVerdict.DidNotRun, reason);
    }

    private static RunCheck Build(
        string command,
        int? exitCode,
        string revision,
        string environment,
        TimeSpan duration,
        RunCheckVerdict verdict,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A check must name the command it ran.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new ArgumentException(
                "A check must name the revision it ran against; a check with no revision describes no tree.",
                nameof(revision));
        }

        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException(
                "A check must name the environment it ran in.", nameof(environment));
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentException("A check cannot have a negative duration.", nameof(duration));
        }

        return new RunCheck(command, exitCode, revision, environment, duration, verdict, reason);
    }
}

/// <summary>
///     One artifact the run produced, by path and kind (epic #41, slice B3.1).
/// </summary>
/// <remarks>
///     <para>
///         The path is checked for existence at record time, which is the one
///         filesystem capability this Domain assembly carries — see
///         <c>ContractsArtifactCapabilityRule</c> for why that is a recorded,
///         guarded decision rather than an accident. A record that named a file
///         nobody produced would be the record's own version of a plausible
///         zero.
///     </para>
///     <para>
///         <b>Not a positional record on purpose.</b> A positional one gets a
///         public constructor, and a public constructor taking a path is a
///         constructor that skips the existence check — the check would then be
///         a suggestion that
///         <see cref="RunVerificationRecord.WithArtifact" /> merely declined to
///         use. The constructor is <c>internal</c> — reachable from
///         <see cref="RunVerificationRecord" />, which builds the entries, and
///         from nobody outside this assembly — so the rule is the type's shape
///         rather than a caller's discipline.
///     </para>
/// </remarks>
public sealed record RunArtifact
{
    internal RunArtifact(string path, RunArtifactKind kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Path of the artifact, as recorded at run time. Never blank, and it exists.</summary>
    public string Path { get; }

    /// <summary>What kind of artifact it is.</summary>
    public RunArtifactKind Kind { get; }

    /// <summary>
    ///     Record an artifact, rejecting a blank path and a path that does not
    ///     exist. Existence is the check: an artifact the run claims to have
    ///     produced but cannot point at is not an artifact.
    /// </summary>
    /// <param name="path">Path of the artifact.</param>
    /// <param name="kind">What kind of artifact it is.</param>
    /// <returns>The artifact, or a failure naming the offending path.</returns>
    public static Result<RunArtifact> Create(string path, RunArtifactKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result.Failure<RunArtifact>("An artifact must have a path.");
        }

        if (!File.Exists(path))
        {
            return Result.Failure<RunArtifact>(
                $"Artifact path does not exist: '{path}'. A recorded artifact has to point at a real file — "
                + "a path that is not there is an assertion, not evidence.");
        }

        return Result.Success(new RunArtifact(path, kind));
    }
}

/// <summary>
///     Something that was deliberately NOT verified, and why (epic #41, slice
///     B3.1).
/// </summary>
/// <remarks>
///     <para>
///         This is the list the whole record exists to be able to print. A run
///         that verified nothing and says nothing about it is indistinguishable,
///         to the reader, from a run that verified everything.
///     </para>
///     <para>
///         The constructor is <c>internal</c> for the same reason
///         <see cref="RunArtifact" />'s is: a public one taking two free strings
///         would accept <c>("", "")</c>, and the one thing this list must never
///         contain is an omission with no reason. Every instance is built by
///         <see cref="RunVerificationRecord.WithNotVerified" /> or by
///         <see cref="RunVerificationRecord.WithCheck" />, and both refuse a
///         blank.
///     </para>
/// </remarks>
public sealed record NotVerifiedEntry
{
    internal NotVerifiedEntry(string what, string reason)
    {
        What = what;
        Reason = reason;
    }

    /// <summary>What was not verified — a check name, a gate, a claim. Never blank.</summary>
    public string What { get; }

    /// <summary>Why it was not verified. Never blank.</summary>
    public string Reason { get; }

    /// <summary>The message a reader sees for this omission.</summary>
    public string Describe() => $"{What}: {Reason}";
}

/// <summary>
///     What the checks actually support (epic #41, slice B3.1) — three
///     verdicts, and only three.
/// </summary>
/// <remarks>
///     These are mutually exclusive by construction, so the record is never
///     asked to be two of them at once. "We cannot tell which of these applies"
///     is NOT a fourth member: it is <see cref="RunEvidenceState" />, carried
///     beside this value, because a missing fact and a negative fact are
///     different shapes and a flat list of four would let a caller print them
///     with the same word.
/// </remarks>
public enum RunVerificationVerdict
{
    /// <summary>
    ///     The evidence is readable and nothing passed at the pinned revision.
    ///     A definite statement, not a shrug: we looked, and there is no proof.
    /// </summary>
    NotVerified,

    /// <summary>At least one check passed at the pinned revision and none failed.</summary>
    Verified,

    /// <summary>At least one check failed at the pinned revision.</summary>
    VerificationFailed,
}

/// <summary>
///     Whether the evidence itself can be trusted — the epistemic axis, kept
///     SEPARATE from <see cref="RunVerificationVerdict" /> (epic #41, slice
///     B3.1).
/// </summary>
/// <remarks>
///     <para>
///         This is the "unknown" the #399 analysis asked for, and it is
///         deliberately not a verdict. The distinction is load-bearing:
///         <see cref="Unreadable" /> means the evidence could not be read, and
///         <see cref="RunVerificationVerdict.NotVerified" /> means it was read
///         and held nothing. Collapsing them is the #782 shape — a plausible
///         value standing in for a missing fact — so they are two fields rather
///         than one four-member enum, and
///         <see cref="RunVerificationRecord.Summary" /> gives them different
///         text.
///     </para>
///     <para>
///         The producer of the evidence is the only thing that can set this, and
///         the producer is the one that knows — a garbled store read-back is
///         what makes evidence unreadable, which is #406's detection feeding
///         this value. Nothing here guesses it from the contents.
///     </para>
/// </remarks>
public enum RunEvidenceState
{
    /// <summary>Every check the run attempted is listed and readable.</summary>
    Complete,

    /// <summary>
    ///     The evidence could not be read back, or is known to be partial. No
    ///     verdict may be asserted from it.
    /// </summary>
    Unreadable,
}

/// <summary>
///     The record-level answer to "was this verified?": a verdict and what is
///     known about the evidence behind it (epic #41, slice B3.1).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Verdict" /> is <c>null</c> exactly when
///         <see cref="Evidence" /> is <see cref="RunEvidenceState.Unreadable" />.
///         That is not a convenience: asserting a verdict from evidence we could
///         not read is precisely the failure this type exists to prevent, and a
///         nullable verdict makes that unrepresentable rather than merely
///         discouraged.
///     </para>
/// </remarks>
/// <param name="Verdict">What the readable checks support, or null when they cannot be read.</param>
/// <param name="Evidence">Whether the evidence can be trusted at all.</param>
public readonly record struct RunVerification(RunVerificationVerdict? Verdict, RunEvidenceState Evidence)
{
    /// <summary>
    ///     True only for a readable evidence set with at least one pass at the
    ///     pinned revision and no failure. This is the single definition of
    ///     "verified" in the product, and it is deliberately narrow.
    /// </summary>
    public bool IsVerified => Verdict == RunVerificationVerdict.Verified;

    /// <summary>
    ///     True when no verdict may be asserted — either the evidence is
    ///     <see cref="RunEvidenceState.Unreadable" />, or there is no verdict.
    /// </summary>
    /// <remarks>
    ///     The second disjunct exists for <c>default(RunVerification)</c>, which
    ///     is what a struct's zero value is and what an uninitialised field
    ///     holds. A zero value that read as a definite "not verified" would be
    ///     the #782 shape in its purest form: a plausible negative standing in
    ///     for the absence of any reading at all. It reads as unknown, which is
    ///     what it is.
    /// </remarks>
    public bool IsUnknown => Evidence == RunEvidenceState.Unreadable || Verdict is null;

    /// <summary>
    ///     The one line a reader sees. Every state gets its own wording: the
    ///     three verdicts and "unknown" are four different sentences, because
    ///     the point of the record is that the reader can tell them apart.
    /// </summary>
    public string Describe()
    {
        if (Evidence == RunEvidenceState.Unreadable)
        {
            return "unknown - the evidence for this run could not be read back, so no verdict is asserted";
        }

        return Verdict switch
        {
            RunVerificationVerdict.Verified => "verified - at least one check passed at the pinned revision, none failed",
            RunVerificationVerdict.VerificationFailed => "verification failed - at least one check failed at the pinned revision",
            RunVerificationVerdict.NotVerified => "not verified - no check passed at the pinned revision",
            null => "unknown - the evidence for this run could not be read back, so no verdict is asserted",
        };
    }
}

/// <summary>
///     What was verified about one run: the baseline it was pinned to, the
///     checks that ran against it, the artifacts they produced, and what was
///     deliberately left unverified (epic #41, slice B3.1).
/// </summary>
/// <remarks>
///     <para>
///         <b>Fact, not opinion.</b> Every member is a thing that was observed,
///         not a conclusion: a commit, a command, an exit code, a moment. That
///         is what makes the record checkable — a reader can re-run a check and
///         compare — and it is why <see cref="BaseRevision" /> is a projection of
///         the pinned contract rather than a field anyone can set. A record of
///         the form "verified" cannot be re-checked, and a month later it
///         describes a tree nobody ran anything against.
///     </para>
///     <para>
///         <b>Identity is what keeps it from rotting.</b> A check that ran on a
///         revision other than the pin is not counted toward the verdict and is
///         not dropped: it is surfaced in <see cref="NotVerified" /> with the
///         reason. That is the whole anti-staleness mechanism, and it is a
///         derivation rather than a comment, so it cannot be forgotten by the
///         next caller.
///     </para>
///     <para>
///         <b>Nothing here is independently settable.</b> The only constructor
///         is private, the properties are get-only, and the copy constructor is
///         private too, so a <c>with</c> expression cannot be used to slip in an
///         unvalidated value. Every instance is built by
///         <see cref="Pin" /> and widened by the <c>With*</c> methods, which is
///         what lets the derivation above be trusted.
///     </para>
/// </remarks>
public sealed record RunVerificationRecord
{
    private RunVerificationRecord(
        WorkspaceContract contract,
        DateTimeOffset recordedAt,
        RunEvidenceState evidence,
        IReadOnlyList<RunCheck> checks,
        IReadOnlyList<RunArtifact> artifacts,
        IReadOnlyList<NotVerifiedEntry> notVerified)
    {
        Contract = contract;
        RecordedAt = recordedAt;
        Evidence = evidence;
        Checks = checks;
        Artifacts = artifacts;
        NotVerified = notVerified;
    }

    /// <summary>
    ///     Private on purpose. For a record the compiler would otherwise
    ///     synthesise a copy constructor for, and <c>with</c> is the standard
    ///     way to build a near-copy that skips every factory in this file. Both
    ///     are closed here so "the record cannot hold an unvalidated value" is a
    ///     property of the type rather than a convention.
    /// </summary>
    private RunVerificationRecord(RunVerificationRecord original)
    {
        Contract = original.Contract;
        RecordedAt = original.RecordedAt;
        Evidence = original.Evidence;
        Checks = original.Checks;
        Artifacts = original.Artifacts;
        NotVerified = original.NotVerified;
    }

    /// <summary>
    ///     The workspace contract this run was pinned to. The single source of
    ///     the base revision — the record does not grow a second one.
    /// </summary>
    public WorkspaceContract Contract { get; }

    /// <summary>
    ///     The commit the run was pinned to, projected from
    ///     <see cref="Contract" />. Read-only by construction: there is no
    ///     setter and no constructor parameter, so it cannot drift from the pin.
    /// </summary>
    public string BaseRevision => Contract.BaseRevision;

    /// <summary>
    ///     When the evidence was sealed. A record without a moment cannot be
    ///     aged, and an un-ageable record is one that silently keeps describing
    ///     a tree that has since moved on.
    /// </summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>
    ///     Whether the evidence can be trusted. Defaults to
    ///     <see cref="RunEvidenceState.Complete" /> and is set by whoever
    ///     produced the evidence — a garbled read-back is
    ///     <see cref="RunEvidenceState.Unreadable" />.
    /// </summary>
    public RunEvidenceState Evidence { get; }

    /// <summary>Every check the run attempted, in the order they were added.</summary>
    public IReadOnlyList<RunCheck> Checks { get; }

    /// <summary>Every artifact the checks produced, in the order they were added.</summary>
    public IReadOnlyList<RunArtifact> Artifacts { get; }

    /// <summary>
    ///     What was deliberately not verified and why. Never silently trimmed:
    ///     the drift entries described on this type are added by
    ///     <see cref="WithCheck" />, so a record cannot under-report.
    /// </summary>
    public IReadOnlyList<NotVerifiedEntry> NotVerified { get; }

    /// <summary>
    ///     Start a record for a run pinned to <paramref name="contract" />. The
    ///     evidence is empty and complete, which is the honest state of a run
    ///     that has not run a check yet: nothing verified, and no reason to think
    ///     anything is wrong.
    /// </summary>
    /// <param name="contract">The workspace contract the run was pinned to.</param>
    /// <param name="recordedAt">When the evidence is being sealed.</param>
    /// <returns>The empty, complete record.</returns>
    public static RunVerificationRecord Pin(WorkspaceContract contract, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new RunVerificationRecord(contract, recordedAt, RunEvidenceState.Complete, [], [], []);
    }

    /// <summary>
    ///     Widen the record with one more check.
    /// </summary>
    /// <remarks>
    ///     A check whose <see cref="RunCheck.Revision" /> is not the pinned
    ///     <see cref="BaseRevision" /> is still recorded, and still counted as a
    ///     check — but it cannot support a <c>Verified</c> verdict, and a
    ///     <see cref="NotVerifiedEntry" /> saying so is appended. The check is
    ///     the one thing that cannot be quietly dropped, because dropping it is
    ///     how a record ends up describing a tree nobody ran anything against.
    /// </remarks>
    /// <param name="check">The check to add.</param>
    /// <returns>The widened record.</returns>
    public RunVerificationRecord WithCheck(RunCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var notVerified = new List<NotVerifiedEntry>(NotVerified);
        if (!string.Equals(check.Revision, BaseRevision, StringComparison.Ordinal))
        {
            notVerified.Add(new NotVerifiedEntry(
                check.Command,
                $"ran on revision {check.Revision}, which is not the pinned revision {BaseRevision} — "
                + "it says nothing about the tree this run was pinned to"));
        }

        return new RunVerificationRecord(
            Contract,
            RecordedAt,
            Evidence,
            [.. Checks, check],
            Artifacts,
            notVerified);
    }

    /// <summary>
    ///     Widen the record with one more artifact. Rejects a blank path and a
    ///     path that does not exist — see <see cref="RunArtifact.Create" />.
    /// </summary>
    /// <param name="artifact">The artifact to add.</param>
    /// <returns>The widened record, or a failure naming the offending path.</returns>
    public Result<RunVerificationRecord> WithArtifact(RunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        return Result.Success(new RunVerificationRecord(
            Contract,
            RecordedAt,
            Evidence,
            Checks,
            [.. Artifacts, artifact],
            NotVerified));
    }

    /// <summary>
    ///     Record something that was not verified, with its reason. This is how
    ///     a caller says "the integration suite could not run here" instead of
    ///     leaving it out of the record.
    /// </summary>
    /// <param name="what">What was not verified.</param>
    /// <param name="reason">Why it was not verified. Must be non-blank.</param>
    /// <returns>The widened record.</returns>
    public RunVerificationRecord WithNotVerified(string what, string reason)
    {
        if (string.IsNullOrWhiteSpace(what))
        {
            throw new ArgumentException("An omission must name what was not verified.", nameof(what));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "An omission must say why; an absent reason reads as 'nobody tried'.", nameof(reason));
        }

        var notVerified = new List<NotVerifiedEntry>(NotVerified) { new NotVerifiedEntry(what, reason) };
        return new RunVerificationRecord(Contract, RecordedAt, Evidence, Checks, Artifacts, notVerified);
    }

    /// <summary>
    ///     Mark the evidence unreadable. The verdict stops being asserted, and
    ///     the record says so rather than reporting a <c>NotVerified</c> it did
    ///     not actually establish.
    /// </summary>
    /// <param name="reason">Why the evidence cannot be read. Must be non-blank.</param>
    /// <returns>The record with unreadable evidence and the reason recorded as an omission.</returns>
    public RunVerificationRecord WithUnreadableEvidence(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Unreadable evidence must say what went wrong.", nameof(reason));
        }

        return new RunVerificationRecord(
            Contract,
            RecordedAt,
            RunEvidenceState.Unreadable,
            Checks,
            Artifacts,
            [.. NotVerified, new NotVerifiedEntry("the evidence itself", reason)]);
    }

    /// <summary>
    ///     The verdict this evidence supports.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Derived, never stored, and the derivation is the substance of the
    ///         type:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 Unreadable evidence ⇒ <c>null</c>. No verdict is asserted
    ///                 from a fact we do not have.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 ≥1 <see cref="RunCheckVerdict.Failed" /> at the pinned
    ///                 revision ⇒ <see cref="RunVerificationVerdict.VerificationFailed" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 ≥1 <see cref="RunCheckVerdict.Passed" /> at the pinned
    ///                 revision and no failure there ⇒
    ///                 <see cref="RunVerificationVerdict.Verified" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Otherwise ⇒ <see cref="RunVerificationVerdict.NotVerified" />.
    ///                 Note that a check which never ran, and a check that ran
    ///                 on the wrong revision, both land here: neither is
    ///                 evidence, and both are named in
    ///                 <see cref="NotVerified" />.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public RunVerification Verification
    {
        get
        {
            if (Evidence == RunEvidenceState.Unreadable)
            {
                return new RunVerification(null, RunEvidenceState.Unreadable);
            }

            bool anyFailed = false;
            bool anyPassed = false;
            for (int i = 0; i < Checks.Count; i++)
            {
                RunCheck check = Checks[i];
                if (!string.Equals(check.Revision, BaseRevision, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (check.Verdict)
                {
                    case RunCheckVerdict.Passed:
                        anyPassed = true;
                        break;
                    case RunCheckVerdict.Failed:
                        anyFailed = true;
                        break;
                    case RunCheckVerdict.DidNotRun:
                        break;
                }
            }

            RunVerificationVerdict verdict;
            if (anyFailed)
            {
                verdict = RunVerificationVerdict.VerificationFailed;
            }
            else if (anyPassed)
            {
                verdict = RunVerificationVerdict.Verified;
            }
            else
            {
                verdict = RunVerificationVerdict.NotVerified;
            }

            return new RunVerification(verdict, RunEvidenceState.Complete);
        }
    }

    /// <summary>
    ///     The one-line summary for a human reader — the accessor #42's report
    ///     consumes, and the only member of this type meant to be printed
    ///     verbatim. Carries the verdict, the number of checks behind it, and
    ///     every omission, so the line cannot be read as more than it says.
    /// </summary>
    public string Summary
    {
        get
        {
            RunVerification verification = Verification;
            var summary = new StringBuilder();
            summary.Append("[verification] ").Append(verification.Describe());
            summary.Append(" (rev=").Append(BaseRevision);
            summary.Append(", checks=").Append(Checks.Count.ToString(CultureInfo.InvariantCulture));
            summary.Append(", artifacts=").Append(Artifacts.Count.ToString(CultureInfo.InvariantCulture));
            summary.Append(", not-verified=")
                .Append(NotVerified.Count.ToString(CultureInfo.InvariantCulture));
            summary.Append(')');

            for (int i = 0; i < NotVerified.Count; i++)
            {
                summary.Append("\n  not verified - ").Append(NotVerified[i].Describe());
            }

            return summary.ToString();
        }
    }
}
