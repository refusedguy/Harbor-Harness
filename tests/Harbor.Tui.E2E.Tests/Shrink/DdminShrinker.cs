namespace Harbor.Tui.E2E.Tests.Shrink;

/// <summary>
///     Outcome of one harness-driven replay of a candidate sequence under a
///     fixed schedule.
/// </summary>
public sealed record CheckResult(bool IsFailure, string FailureSignature)
{
    public static CheckResult Pass() => new(false, string.Empty);

    public static CheckResult Fail(string failureSignature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureSignature);
        return new(true, failureSignature);
    }
}

/// <summary>
///     One harness-driven replay of a candidate sequence. Must be synchronous
///     and cooperative: honor <see cref="CancellationToken" /> so a hung check
///     cannot outlive the run budget.
/// </summary>
public delegate CheckResult ShrinkCheck(
    IReadOnlyList<UiSequenceAction> candidate,
    UiStepSchedule schedule,
    CancellationToken cancellationToken);

/// <summary>
///     Input to a shrink run: the failing sequence, the schedule it failed
///     under, and the provenance the failure artifact needs.
/// </summary>
public sealed record ShrinkRequest(
    IReadOnlyList<UiSequenceAction> FailingSequence,
    UiStepSchedule Schedule,
    int Seed,
    string GeneratorVersion,
    string CommitSha);

/// <summary>
///     The shrink verdict. Exhaustion always carries the best-known failing
///     case; there is no verdict that reports the run itself as a failure.
/// </summary>
public sealed record ShrinkReport(
    IReadOnlyList<UiSequenceAction> BestKnownFailingCase,
    string FailureSignature,
    int ChecksPerformed,
    TimeSpan Elapsed,
    bool StoppedByBudget,
    string Note)
{
    /// <summary>
    ///     Builds the failure artifact for this report. The reduced sequence
    ///     still reproduces <see cref="FailureSignature" /> under the request
    ///     schedule — that is the shrinker's postcondition.
    /// </summary>
    public FailureArtifact ToArtifact(ShrinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FailureArtifact.Create(
            request.Seed,
            BestKnownFailingCase,
            request.GeneratorVersion,
            FailureSignature,
            request.CommitSha);
    }
}

/// <summary>
///     The determinism gate refused the run: the full sequence did not
///     reproduce the same failure twice under the same schedule. Reported as a
///     finding about the sequence, not as a shrink result.
/// </summary>
public sealed record NonDeterminismFinding(
    IReadOnlyList<UiSequenceAction> Sequence,
    CheckResult FirstRun,
    CheckResult SecondRun,
    string Detail);

/// <summary>
///     Outcome of <see cref="DdminShrinker.TryShrink" />: either a report
///     carrying the best-known failing case, or a refusal carrying the
///     non-determinism finding that stopped the run before it started.
/// </summary>
public abstract record ShrinkOutcome;

/// <summary> The run completed; <see cref="Shrunk.Report" /> holds the best-known failing case. </summary>
public sealed record Shrunk(ShrinkReport Report) : ShrinkOutcome;

/// <summary> The run refused to start; <see cref="Refused.Finding" /> says why. </summary>
public sealed record Refused(NonDeterminismFinding Finding) : ShrinkOutcome;

/// <summary>
///     ddmin shrinking for UI-action sequences: time-boxed and deterministic.
///     Runner integration lives elsewhere; this type only shrinks.
/// </summary>
/// <remarks>
///     <para>Contract, as agreed for the UI-sequence flakes:</para>
///     <list type="bullet">
///         <item><description>
///             Determinism gate first: the full sequence replays twice under the
///             same schedule, and both replays must fail with the same
///             signature. Otherwise the run refuses with a
///             <see cref="NonDeterminismFinding" /> — shrinking a flake without
///             a controlled schedule would shrink noise into a passing case.
///         </description></item>
///         <item><description>
///             Budgets: at most <see cref="MaxShrinkChecks" /> checks and at
///             most <see cref="MaxShrinkDuration" /> from the start of the run.
///             Either bound stops the loop and the report carries the
///             best-known failing case.
///         </description></item>
///         <item><description>
///             The per-check token carries the remaining run budget
///             (<c>CancelAfter(remaining)</c> anchored at run start), so one
///             hung cooperative check cannot blow the duration budget.
///             Residual risk, stated openly: a check that ignores its token
///             overruns anyway — the harness must only hand in cooperative
///             checks.
///         </description></item>
///         <item><description>
///             The report wording claims "no further reduction by chosen
///             transforms", never a stronger optimality claim.
///         </description></item>
///     </list>
/// </remarks>
public sealed class DdminShrinker
{
    /// <summary> Maximum predicate evaluations per run, gate replays included. </summary>
    public const int MaxShrinkChecks = 50;

    /// <summary> Maximum wall of one run, measured from the start of the run. </summary>
    public static readonly TimeSpan MaxShrinkDuration = TimeSpan.FromSeconds(30);

    /// <summary> The only reduction claim a report may make. </summary>
    public const string ReductionNote = "no further reduction by chosen transforms";

    private readonly IShrinkClock _clock;

    public DdminShrinker(IShrinkClock? clock = null)
    {
        _clock = clock ?? SystemShrinkClock.Instance;
    }

    public ShrinkOutcome TryShrink(
        ShrinkRequest request,
        ShrinkCheck check,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(request.FailingSequence);
        ArgumentNullException.ThrowIfNull(request.Schedule);

        DateTime runStart = _clock.UtcNow;
        int checks = 0;
        var ownedTimeouts = new List<CancellationTokenSource>();
        try
        {
            CancellationToken TokenWithRemainingBudget()
            {
                TimeSpan remaining = MaxShrinkDuration - (_clock.UtcNow - runStart);
                var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ownedTimeouts.Add(timeout);
                timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                return timeout.Token;
            }

            // Determinism gate: the failure must reproduce twice under the
            // same schedule before any reduction is attempted. The two replays
            // count toward the check budget.
            CheckResult first = check(request.FailingSequence, request.Schedule, TokenWithRemainingBudget());
            checks++;
            CheckResult second = check(request.FailingSequence, request.Schedule, TokenWithRemainingBudget());
            checks++;

            if (!first.IsFailure || !second.IsFailure ||
                !string.Equals(first.FailureSignature, second.FailureSignature, StringComparison.Ordinal))
            {
                string detail =
                    $"determinism gate did not reproduce the failure: " +
                    $"run 1 = {(first.IsFailure ? "failing" : "passing")} " +
                    $"({Display(first.FailureSignature)}), run 2 = " +
                    $"{(second.IsFailure ? "failing" : "passing")} " +
                    $"({Display(second.FailureSignature)}); shrinking did not start";
                return new Refused(new NonDeterminismFinding(
                    request.FailingSequence, first, second, detail));
            }

            string signature = first.FailureSignature;
            var current = new List<UiSequenceAction>(request.FailingSequence);
            bool stoppedByBudget = false;
            int granularity = 2;

            bool StillFails(CheckResult r) =>
                r.IsFailure && string.Equals(r.FailureSignature, signature, StringComparison.Ordinal);

            CheckResult? Run(IReadOnlyList<UiSequenceAction> candidate, out bool outOfBudget)
            {
                outOfBudget = false;
                if (checks >= MaxShrinkChecks || _clock.UtcNow - runStart >= MaxShrinkDuration)
                {
                    outOfBudget = true;
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                CheckResult result = check(candidate, request.Schedule, TokenWithRemainingBudget());
                checks++;
                return result;
            }

            while (current.Count >= 2 && !stoppedByBudget)
            {
                bool reduced = false;

                foreach ((int offset, int length) in Partitions(current.Count, granularity))
                {
                    List<UiSequenceAction> subset = current.GetRange(offset, length);
                    CheckResult? r = Run(subset, out bool outOfBudget);
                    if (outOfBudget)
                    {
                        stoppedByBudget = true;
                        break;
                    }

                    if (r is not null && StillFails(r))
                    {
                        current = new List<UiSequenceAction>(subset);
                        granularity = 2;
                        reduced = true;
                        break;
                    }
                }

                if (stoppedByBudget || reduced)
                {
                    continue;
                }

                foreach ((int offset, int length) in Partitions(current.Count, granularity))
                {
                    var complement = new List<UiSequenceAction>(current);
                    complement.RemoveRange(offset, length);
                    CheckResult? r = Run(complement, out bool outOfBudget);
                    if (outOfBudget)
                    {
                        stoppedByBudget = true;
                        break;
                    }

                    if (r is not null && StillFails(r))
                    {
                        current = complement;
                        granularity = Math.Max(granularity - 1, 2);
                        reduced = true;
                        break;
                    }
                }

                if (stoppedByBudget || reduced)
                {
                    continue;
                }

                if (granularity >= current.Count)
                {
                    break;
                }

                granularity = Math.Min(granularity * 2, current.Count);
            }

            IReadOnlyList<UiSequenceAction> best = current.ToArray();
            string note = stoppedByBudget
                ? $"budget exhausted ({MaxShrinkChecks} checks / " +
                  $"{MaxShrinkDuration.TotalSeconds:0} s); returning best-known " +
                  $"failing case — {ReductionNote}"
                : ReductionNote;
            var report = new ShrinkReport(
                best, signature, checks, _clock.UtcNow - runStart, stoppedByBudget, note);
            return new Shrunk(report);
        }
        finally
        {
            foreach (CancellationTokenSource timeout in ownedTimeouts)
            {
                timeout.Dispose();
            }
        }
    }

    private static IEnumerable<(int Offset, int Length)> Partitions(int count, int granularity)
    {
        int chunk = (count + granularity - 1) / granularity;
        if (chunk < 1)
        {
            chunk = 1;
        }

        for (int offset = 0; offset < count; offset += chunk)
        {
            yield return (offset, Math.Min(chunk, count - offset));
        }
    }

    private static string Display(string signature) =>
        signature.Length == 0 ? "<empty>" : signature;
}
