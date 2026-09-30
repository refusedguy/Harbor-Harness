// TokenTrackingRatchet.cs — the RATCHET for #471, slice 1: ITokenTracker.
//
// WHY THIS IS A RATCHET AND NOT A GATE
// ------------------------------------
// The first version of this file was a gate. It required ITokenTracker to
// ALREADY be composed of four role interfaces, and it was landed red on
// purpose. That was the wrong instrument. A gate that demands a refactor
// nobody has done is not a gate — it is a standing build failure, and a
// standing build failure is how a team learns to reach for `--no-verify`. The
// split is real, owed work; four attempts at it died half-finished in a
// worktree. None of that is a reason to leave the build permanently red.
//
// So this is a ratchet. It freezes the shape exactly as measured and fires when
// the shape gets WORSE:
//
//   * an eighth member on the aggregate,
//   * a seventeenth file that holds the aggregate,
//   * a consumer that starts calling a member it did not call before.
//
// Every one of those is an ADDITION, and an addition is the only direction that
// makes the interface-segregation violation worse. It also fires when the shape
// SHRINKS — see RATCHET SEMANTICS, which is the part that makes it a ratchet
// and not a to-do list.
//
// THE SPLIT IS STILL OWED. THIS FILE DOES NOT DO IT AND IS NOT TRYING TO.
// -----------------------------------------------------------------------
// Seven members, and they fall into four groups that no two consumers share.
// The target is four disjoint roles, each spelled out below with its PROVEN
// consumers — consumers taken from the measurement, not invented for symmetry.
// TargetSlices carries them as data, and the two tests at the bottom check that
// the plan is still internally consistent AND that its consumer list still
// matches the measured one:
//
//   ITokenEstimator        Estimate, EstimateMessage, EstimateTokens
//                          -> CompactionService, TurnRunner, CompactionBehavior
//   IUsageRecorder         RecordAppendedMessage, RecordTurnUsage
//                          -> TurnRunner, SteeringDrainBehavior, BackgroundDrain
//   ICompactionHeuristic   ShouldCompact
//                          -> CompactionBehavior
//   IUsageStats            GetStats
//                          -> ReplLifecycle, PromptPipeline
//
// TurnRunner and CompactionBehavior each need TWO slices, and that is correct:
// a consumer that genuinely needs two capabilities should depend on two
// interfaces. What must never happen is one of them holding all four. Note also
// why RecordTurnUsage and RecordAppendedMessage stay together: TurnRunner is
// the only caller of the first and it also calls the second, so separating them
// narrows nobody.
//
// TWO FACTS A READER SHOULD NOT HAVE TO RE-DERIVE
// ------------------------------------------------
//   * ITokenEstimator ALREADY EXISTS, in this very namespace —
//     src/Harbor.Abstractions/Sessions/ICompactionService.cs:69 — and its
//     Estimate / EstimateMessage are byte-identical to the aggregate's. The
//     counting slice needs no new interface at all; it needs the aggregate to
//     stop redeclaring what is already declared next door.
//   * The third counting member does NOT match it. The aggregate declares
//     `EstimateTokens(IReadOnlyList<AgentMessage>)`; ITokenEstimator declares
//     `EstimateMessages(IEnumerable<AgentMessage>)`. Same job, different name,
//     narrower parameter. Collapsing them is a RENAME, and a rename is a
//     product decision — a reader should not discover that by trying it.
//
// AND THE AGENTLOOP FIELD IS DEAD WEIGHT
// --------------------------------------
// `AgentLoop._tokenTracker` is assigned at AgentLoop.cs:91 and read ZERO times.
// The constructor parameter it is fed from exists only to be forwarded to
// CompactionBehavior / SteeringDrainBehavior / BackgroundDrain / TurnRunner. So
// AgentLoop is a ninth HOLDER of the aggregate and a consumer of nothing. This
// ratchet measures that rather than trusting the prose: the row below is marked
// a composition root with an EMPTY member list, so the day someone starts
// reading that field the row grows and the build goes red.
//
// RATCHET SEMANTICS — WHY IT ALSO FIRES WHEN THE SHAPE SHRINKS
// ------------------------------------------------------------
// A ratchet that only ever counts up is a to-do list that rots. Every delta
// here is reported in one of two buckets and BOTH are asserted empty:
//
//   * Regressions — something was ADDED. This is the violation.
//   * Improvements — something was REMOVED. This is very likely the split
//     landing, which is the point; but it is also what a careless rename, a
//     moved file, or a member quietly deleted looks like, and none of those
//     should pass unremarked.
//
// So the split, when it lands, will deliberately redden this file and its
// author will update the tables in the same commit. That friction is the
// point: the shape of this aggregate cannot change without a human deciding to
// change it, and deciding is cheap when the measured before/after is printed
// in the failure message.
//
// HOW THE MEASUREMENT IS TAKEN, AND ITS ONE KNOWN IMPRECISION
// -----------------------------------------------------------
// Two scans, both over src/ + apps/ (SourceScan.ProductTrees), comments
// stripped first (SourceCommentStripper) so that the XML docs which NAME the
// aggregate to explain it are not graded as holders of it — ContextUsage.cs:16
// and StatusViewModel.cs:114 both mention it in prose and neither is a consumer.
//
//   * Holders: files whose comment-stripped text names the type.
//   * Calls per holder: receiver-AGNOSTIC member access, i.e. `<anything>?.<Member>(`
//     or `<anything>.<Member><`. Receiver-agnostic on purpose — a scan
//     anchored on the receiver identifier `tokenTracker` would go blind the
//     moment somebody renames the field, which is precisely when a new call
//     could slip in. The cost is over-attribution, and there is exactly one
//     known instance: TokenTracker.cs's `Estimate` / `EstimateMessage` are its
//     own delegations to the ITokenEstimator it holds, not calls on the
//     aggregate. A receiver-agnostic scan cannot tell those apart, so that row
//     is deliberately over-attributed. Over-attribution is the SAFE direction
//     — it can only make the ratchet stricter, never looser — and it is called
//     out in the row's Role text rather than hidden.
//
// NON-VACUITY
// -----------
// A ratchet that cannot fire buys false confidence, and a ratchet over an empty
// measurement is green forever. Three defences, all as tests in this file:
//
//   1. NonVacuity_TheScanFindsTheAggregateAndItsRealCallSites — the live
//      detector is exercised against real source: it must find the holders, it
//      must find CompactionService's three real calls, and it must report
//      AgentLoop's field as calling nothing.
//   2. Control_* (four of them) — synthetic WORSE states are fed through the
//      SAME comparison functions the real tests use, and each must be reported
//      as a regression. This is what makes the claim "the guard would go red"
//      checkable rather than asserted: if someone rewrites the comparator into
//      something that never reports a regression, the controls fail.
//   3. Control_TheSplitLandingIsReportedAsProgress — the mirror. When the
//      aggregate declares no members of its own, the comparator must report
//      that as PROGRESS, never as a regression. A comparator that could not
//      tell the two apart would make the split unreportable and would let a
//      regression be waved through as "just the refactor".

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One measured holder of <c>ITokenTracker</c> in the product trees.
/// </summary>
/// <param name="File">Repo-relative path of the holder.</param>
/// <param name="Members">
///     Members of the aggregate it actually CALLS, ordinal-sorted. Empty is a real
///     and meaningful measurement, not missing data: a composition root that hands
///     the aggregate on, and <c>AgentLoop</c>, whose field is write-only.
/// </param>
/// <param name="Role">What this file does with the aggregate, and why the row exists.</param>
/// <param name="IsCompositionRoot">
///     <see langword="true" /> when the file HOLDS the aggregate without exercising it —
///     the declaration, the implementor, DI registration and resolution, a pass-through,
///     and the write-only field. Composition roots legitimately name the wide type;
///     leaf consumers are the ones the split is for, so the plan's consumer lists are
///     derived over the <see langword="false" /> rows only.
/// </param>
internal sealed record TokenHolderBaseline(
    string File,
    IReadOnlyList<string> Members,
    string Role,
    bool IsCompositionRoot);

/// <summary>
///     One of the four roles the split of <c>ITokenTracker</c> is owed, with the
///     consumers that prove it is needed. This is the plan, as data.
/// </summary>
/// <param name="Role">Proposed role interface name, in the aggregate's own namespace.</param>
/// <param name="Members">Members it would own, ordinal-sorted.</param>
/// <param name="Consumers">
///     The leaf consumers measured to call at least one of <paramref name="Members" />.
///     Checked against <see cref="TokenTrackingRatchet.BaselineHolders" />, so the plan
///     cannot quietly drift away from the evidence it claims to rest on.
/// </param>
internal sealed record TokenTargetSlice(
    string Role,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Consumers);

/// <summary>
///     The two halves of a baseline comparison. Both are asserted empty by the real
///     tests; the difference is what each one MEANS, and the failure messages say so.
///     The buckets are <see cref="List{T}" /> rather than <see cref="IReadOnlyList{T}" /> so that
///     every assertion below binds TUnit's collection overload on a concrete list, the
///     same form the rest of this project uses.
/// </summary>
/// <param name="Regressions">Items present now and absent from the baseline: the shape got worse.</param>
/// <param name="Improvements">Items in the baseline and absent now: the shape shrank or moved.</param>
internal sealed record RatchetDelta(List<string> Regressions, List<string> Improvements);

/// <summary>
///     Freezes the measured shape of <c>ITokenTracker</c> and fails the build when
///     that shape grows — and when it changes at all without being re-measured. The
///     split it is a ratchet FOR is recorded in the header and in
///     <see cref="TargetSlices" />; it has not been done, and this file does not do it.
/// </summary>
public sealed class TokenTrackingRatchet
{
    /// <summary>The aggregate this ratchet governs.</summary>
    private const string AggregateName = "ITokenTracker";

    /// <summary>How many members it declared when this table was measured.</summary>
    private const int MeasuredMemberCount = 7;

    /// <summary>How many product files held it when this table was measured.</summary>
    private const int MeasuredHolderCount = 16;

    /// <summary>
    ///     How many holders call at least one member: the seven leaf consumers, plus the
    ///     implementor row that is over-attributed (see the header).
    /// </summary>
    private const int MeasuredFilesCallingSomething = 8;

    /// <summary>The aggregate's full name, resolved by name rather than by <c>typeof</c>.</summary>
    private const string AggregateFullName = "Harbor.Abstractions.Sessions." + AggregateName;

    // =====================================================================
    // The measured baseline. Re-measure and update these in the same commit
    // as whatever changed the shape — the failure messages print the delta so
    // the new table is a mechanical edit.
    // =====================================================================

    /// <summary>
    ///     Every member <c>ITokenTracker</c> declared when measured, ordinal-sorted. This
    ///     list is also the vocabulary of the per-consumer call scan, so an eighth member
    ///     is watched for in consumer code the moment the interface test rejects it.
    /// </summary>
    private static readonly string[] BaselineMembers =
    [
        "Estimate",
        "EstimateMessage",
        "EstimateTokens",
        "GetStats",
        "RecordAppendedMessage",
        "RecordTurnUsage",
        "ShouldCompact",
    ];

    /// <summary>
    ///     Every product file that holds the aggregate, with the members it calls.
    ///     Nine composition roots, seven leaf consumers.
    /// </summary>
    private static readonly TokenHolderBaseline[] BaselineHolders =
    [
        new("apps/Harbor.App.Avalonia/AppHost.cs", [],
            "exposes the singleton to the desktop host; names the type, calls nothing",
            true),
        new("apps/Harbor.App.Cli/Commands/CliInfrastructure.cs", [],
            "resolves it once out of the container and hands it to the REPL",
            true),
        new("apps/Harbor.App.Cli/Repl/CellForgeReplRunner.cs", [],
            "holds it so the REPL surface can offer it; calls nothing itself",
            true),
        new("apps/Harbor.App.Cli/Repl/PromptPipeline.cs", ["GetStats"],
            "CLI status line: reads the totals and nothing else", false),
        new("apps/Harbor.App.Cli/Repl/ReplLifecycle.cs", ["GetStats"],
            "CLI status line: reads the totals and nothing else", false),
        new("apps/Harbor.App.Cli/Repl/ReplRunner.cs", [],
            "holds it to pass down to the behaviours; calls nothing itself",
            true),
        new("src/Harbor.Abstractions/Sessions/ITokenTracker.cs", [],
            "the declaration itself", true),
        new("src/Harbor.Application/Agents/AgentLoop.cs", [],
            "the write-only field: assigned at AgentLoop.cs:91, read zero times. The ctor "
            + "parameter it is fed from exists only to be forwarded to the four behaviours",
            true),
        new("src/Harbor.Application/Agents/BackgroundDrain.cs", ["RecordAppendedMessage"],
            "appends drained background output to the running estimate", false),
        new("src/Harbor.Application/Agents/Pipeline/CompactionBehavior.cs",
            ["EstimateTokens", "ShouldCompact"],
            "asks whether to compact, and sizes the histogram it logs either way", false),
        new("src/Harbor.Application/Agents/Pipeline/SteeringDrainBehavior.cs",
            ["RecordAppendedMessage"],
            "records the steer message that was appended to the history", false),
        new("src/Harbor.Application/Agents/TurnRunner.cs",
            ["EstimateTokens", "RecordAppendedMessage", "RecordTurnUsage"],
            "records what the turn spent and what it appended, and sizes the turn for a metric",
            false),
        new("src/Harbor.Application/Sessions/CompactionService.cs",
            ["Estimate", "EstimateMessage", "EstimateTokens"],
            "counts text and history; never records and never decides", false),
        new("src/Harbor.Application/Sessions/TokenTracker.cs", ["Estimate", "EstimateMessage"],
            "the sole implementor. KNOWN OVER-ATTRIBUTION: these two are its own delegations "
            + "to the ITokenEstimator it holds, not calls on the aggregate — a "
            + "receiver-agnostic scan cannot tell them apart. Over-attribution can only make "
            + "this ratchet stricter, so the safe direction is the right one here",
            true),
        new("src/Harbor.Hosting/Modules/CoreModule.cs", [],
            "the DI registration: one singleton, bound once", true),
        new("src/Harbor.Hosting/Modules/IntelligenceModule.cs", [],
            "resolves that instance to hand it to AgentLoop", true),
    ];

    /// <summary>
    ///     The split this ratchet is owed, as data. NOT IMPLEMENTED — the header explains
    ///     why, and the two tests that close this section keep the plan honest without
    ///     pretending any of it has landed.
    /// </summary>
    private static readonly TokenTargetSlice[] TargetSlices =
    [
        new("ITokenEstimator", ["Estimate", "EstimateMessage", "EstimateTokens"],
        [
            "src/Harbor.Application/Agents/Pipeline/CompactionBehavior.cs",
            "src/Harbor.Application/Agents/TurnRunner.cs",
            "src/Harbor.Application/Sessions/CompactionService.cs",
        ]),
        new("IUsageRecorder", ["RecordAppendedMessage", "RecordTurnUsage"],
        [
            "src/Harbor.Application/Agents/BackgroundDrain.cs",
            "src/Harbor.Application/Agents/Pipeline/SteeringDrainBehavior.cs",
            "src/Harbor.Application/Agents/TurnRunner.cs",
        ]),
        new("ICompactionHeuristic", ["ShouldCompact"],
        ["src/Harbor.Application/Agents/Pipeline/CompactionBehavior.cs"]),
        new("IUsageStats", ["GetStats"],
        [
            "apps/Harbor.App.Cli/Repl/PromptPipeline.cs",
            "apps/Harbor.App.Cli/Repl/ReplLifecycle.cs",
        ]),
    ];

    /// <summary>
    ///     Matches a line that names the aggregate, with word boundaries.
    /// </summary>
    private static readonly Regex AggregateMention = new($@"\b{AggregateName}\b", RegexOptions.Compiled);

    /// <summary>
    ///     Matches a call to one of <see cref="BaselineMembers" /> on ANY receiver, and
    ///     captures the member name in group 1. <c>\??\.</c> covers both <c>x.M(</c> and the
    ///     null-conditional <c>x?.M(</c> the CLI status pollers use; <c>[(<]</c> also admits
    ///     a generic method so a future member cannot hide behind one.
    ///     <para>
    ///         Built by a method, not inline, so it reads <see cref="BaselineMembers" />
    ///         AFTER the static initialisers above have run — a field initialiser order
    ///         dependency written as an interpolated string would be a silent empty regex.
    ///     </para>
    /// </summary>
    private static readonly Regex MemberCall = BuildMemberCallRegex();

    // =====================================================================
    // 1. The ratchet. These pass on the state measured above.
    // =====================================================================

    /// <summary>
    ///     The aggregate still declares exactly the seven measured members. An eighth is
    ///     the regression this ratchet exists to stop: it is a member every implementor and
    ///     every test double must now satisfy, added to an interface that is already too
    ///     wide.
    /// </summary>
    [Test]
    public async Task Ratchet_AggregateDeclaresNoMoreThanTheSevenMeasuredMembers()
    {
        await Assert.That(Resolve(AggregateName)).IsNotNull()
            .Because($"{AggregateFullName} must exist for this ratchet to measure anything. "
                   + "If it does not, the aggregate was renamed or deleted and every row of "
                   + "BaselineHolders is stale.");

        IReadOnlyList<string> measured = MeasureAggregateMembers();

        await Assert.That(measured.Count).IsEqualTo(MeasuredMemberCount)
            .Because($"the aggregate declared {MeasuredMemberCount} members when this table was "
                   + "measured and it may only hold that line. A larger number is a member added "
                   + "to an interface that is already the subject of an unpaid ISP violation "
                   + "(#471); the counted name is in the comparison below");

        RatchetDelta delta = CompareSets("member declared on the aggregate", BaselineMembers, measured);

        await Assert.That(delta.Regressions).IsEmpty()
            .Because($"an eighth member on {AggregateName} is the regression: every implementor, "
                   + "every test double and every consumer's declared type grows with it, and none "
                   + "of the seven current consumers asked for it. Put the new capability on one of "
                   + "the four roles in TargetSlices instead. Added: "
                   + string.Join(", ", delta.Regressions));

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("a member left the aggregate. That is either the split landing — in which "
                   + "case this ratchet has done its job and these tables get updated in this same "
                   + "commit — or a member deleted outright, which is not a decision a ratchet "
                   + "should let pass unremarked. Re-measure and update BaselineMembers. Removed: "
                   + string.Join(", ", delta.Improvements));
    }

    /// <summary>
    ///     No seventeenth file holds the aggregate. Every row in the table above is a
    ///     composition root that legitimately needs the wide type, or one of the seven leaf
    ///     consumers the split exists for; a new one is coupling being added, not moved.
    /// </summary>
    [Test]
    public async Task Ratchet_NoNewFileHoldsTheAggregate()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> measured = MeasureHolders();

        await Assert.That(measured.Count).IsEqualTo(MeasuredHolderCount)
            .Because($"src/ and apps/ held the aggregate in {MeasuredHolderCount} files when this "
                   + "table was measured. More is a new dependency on the wide type; fewer means "
                   + "this ratchet is measuring a repository that has moved on and the table needs "
                   + "re-deriving. The named deltas are below");

        RatchetDelta delta = CompareSets(
            "file that holds the aggregate",
            BaselineHolderMap().Keys,
            measured.Keys);

        await Assert.That(delta.Regressions).IsEmpty()
            .Because("a new file holds the aggregate. That is the split running backwards: a leaf "
                   + "consumer declaring the wide type has all seven members in its constructor "
                   + "signature whatever its body touches, and re-imposes them on every implementor "
                   + "and test double. Declare the role it uses — ITokenEstimator to count, "
                   + "IUsageRecorder to record, ICompactionHeuristic to decide, IUsageStats to read. "
                   + "Added: " + string.Join(", ", delta.Regressions));

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("a holder disappeared: it was properly narrowed onto a role, or moved, or was "
                   + "deleted. All three are plausible and all three change what the rest of this "
                   + "file means, so re-measure BaselineHolders and delete the row in this same "
                   + "commit. Removed: " + string.Join(", ", delta.Improvements));
    }

    /// <summary>
    ///     No consumer calls more of the aggregate than it did. This is the per-consumer
    ///     half: the file set can stay at sixteen while a consumer quietly starts calling a
    ///     member it never wanted, and that is the coupling the split is meant to undo.
    /// </summary>
    [Test]
    public async Task Ratchet_NoConsumerCallsMoreOfTheAggregate()
    {
        RatchetDelta delta = CompareMemberCalls(BaselineHolderMap(), MeasureHolders());

        await Assert.That(delta.Regressions).IsEmpty()
            .Because("a holder now calls a member of the aggregate that it did not call when "
                   + "measured. Every such call is one more reason that file needs the wide type, "
                   + "which is the opposite of the direction #471 wants. The member belongs to one "
                   + "of the four roles in TargetSlices — take the narrow one. Added: "
                   + string.Join(", ", delta.Regressions));

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("a holder stopped calling a member, or stopped calling the aggregate at all. "
                   + "Very likely the split landing; also possibly a rename that moved the call to a "
                   + "receiver this scan can no longer see. Either way the measured row is now "
                   + "wrong, so re-measure BaselineHolders in this same commit. Removed: "
                   + string.Join(", ", delta.Improvements));
    }

    // =====================================================================
    // 2. The plan. Not implemented — checked only for internal consistency
    //    and for still resting on the measurement.
    // =====================================================================

    /// <summary>
    ///     The four target roles are disjoint and together cover the whole surface. This is
    ///     what makes the plan a plan rather than an aspiration: a member nobody assigned, or
    ///     two roles that both claim the same one, is caught here instead of during the split.
    /// </summary>
    [Test]
    public async Task Plan_TheFourTargetSlicesAreDisjointAndCoverTheSurface()
    {
        await Assert.That(TargetSlices.Length).IsEqualTo(4)
            .Because("the split of #471 is four roles, not three and not five: the seven members "
                   + "form four groups that no two consumers share, which is what the ISP "
                   + "violation is");

        var duplicated = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TokenTargetSlice slice in TargetSlices)
        {
            foreach (string member in slice.Members)
            {
                if (!seen.Add(member))
                {
                    duplicated.Add($"{member} (claimed twice)");
                }
            }
        }

        await Assert.That(duplicated).IsEmpty()
            .Because("a member reachable through more than one role is a member no consumer can be "
                   + "prevented from depending on, so the split would not have narrowed anything. "
                   + "Duplicated: " + string.Join(", ", duplicated));

        string[] uncovered = [.. BaselineMembers.Where(m => !seen.Contains(m))];
        await Assert.That(uncovered).IsEmpty()
            .Because("every member the aggregate declares has to land on one of the four roles, or "
                   + "the split silently drops capability. Uncovered: "
                   + (uncovered.Length == 0 ? "(none)" : string.Join(", ", uncovered)));
    }

    /// <summary>
    ///     Each target role's consumer list is the one the measurement supports. This is the
    ///     "proven consumers" claim checked rather than asserted: if a consumer's calls change,
    ///     the ratchet above goes red AND this goes red, so the plan cannot be left describing a
    ///     shape nobody has any more.
    /// </summary>
    [Test]
    public async Task Plan_EachSliceConsumersAreTheMeasuredOnes()
    {
        IReadOnlyDictionary<string, TokenHolderBaseline> byFile = BaselineHolders
            .ToDictionary(static row => row.File, StringComparer.Ordinal);

        var disagreements = new List<string>();
        foreach (TokenTargetSlice slice in TargetSlices)
        {
            var expected = byFile.Values
                .Where(row => !row.IsCompositionRoot
                              && row.Members.Intersect(slice.Members, StringComparer.Ordinal).Any())
                .Select(static row => row.File)
                .OrderBy(static f => f, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var claimed = slice.Consumers.ToHashSet(StringComparer.Ordinal);
            foreach (string file in expected.Except(claimed, StringComparer.Ordinal)
                         .OrderBy(static f => f, StringComparer.Ordinal))
            {
                disagreements.Add($"{slice.Role}: {file} calls it but the plan omits it");
            }

            foreach (string file in claimed.Except(expected, StringComparer.Ordinal)
                         .OrderBy(static f => f, StringComparer.Ordinal))
            {
                disagreements.Add($"{slice.Role}: {file} is claimed but calls none of its members");
            }
        }

        await Assert.That(disagreements).IsEmpty()
            .Because("TargetSlices is the record of what the split is owed, and it is only worth "
                   + "reading while it matches the measurement it claims to rest on. Disagreements: "
                   + string.Join("; ", disagreements));
    }

    // =====================================================================
    // 3. Non-vacuity. The live detector, then four synthetic worse states
    //    pushed through the SAME comparators the ratchet above uses.
    // =====================================================================

    /// <summary>
    ///     The scan is proven against real source, not against a synthetic string: it must
    ///     find the aggregate, find <c>CompactionService</c>'s three genuine calls, and
    ///     report <c>AgentLoop</c>'s field as calling nothing. A detector that silently
    ///     stopped matching would leave every test above green.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheScanFindsTheAggregateAndItsRealCallSites()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> measured = MeasureHolders();

        await Assert.That(measured.Count).IsGreaterThan(0)
            .Because("the scan found no product file naming ITokenTracker. Either the aggregate "
                   + "moved out of src/+apps/ or the scan stopped matching — and a ratchet over an "
                   + "empty measurement is green forever, which is the whole failure mode a "
                   + "ratchet has to avoid");

        await Assert.That(measured.ContainsKey("src/Harbor.Application/Agents/AgentLoop.cs")).IsTrue()
            .Because("the write-only holder is the finding the header makes a point of, so its "
                   + "absence from the measurement must be noticed. If the file genuinely stopped "
                   + "naming the aggregate, delete the row from BaselineHolders deliberately "
                   + "rather than letting a ratchet quietly stop watching it");

        await Assert.That(measured["src/Harbor.Application/Sessions/CompactionService.cs"].ToList())
            .IsEquivalentTo(new[] { "Estimate", "EstimateMessage", "EstimateTokens" })
            .Because("this is the detector proving it fires on real code — three real member calls "
                   + "in a real product file, found by the same receiver-agnostic pattern the "
                   + "ratchet above grades with");

        await Assert.That(measured["src/Harbor.Application/Agents/AgentLoop.cs"].Count).IsEqualTo(0)
            .Because("the field is assigned at AgentLoop.cs:91 and read zero times, so the correct "
                   + "measurement is EMPTY. A non-empty result here would mean the write-only claim "
                   + "in the header is no longer true, and the row above is no longer a finding");

        int calling = measured.Count(static pair => pair.Value.Count > 0);
        await Assert.That(calling).IsEqualTo(MeasuredFilesCallingSomething)
            .Because($"{MeasuredFilesCallingSomething} holders were measured calling something: the "
                   + "seven leaf consumers, plus TokenTracker.cs, whose two matches are the "
                   + "over-attributed delegations documented in the header. This is the count that "
                   + "tells you the per-consumer half of the ratchet has rows to grade at all — a "
                   + "ratchet whose every row is empty grades nothing");
    }

    /// <summary>
    ///     Control: an eighth member is reported as a regression.
    /// </summary>
    [Test]
    public async Task Control_AnEighthMemberIsReportedAsARegression()
    {
        var degraded = new List<string>(BaselineMembers) { "ResetUsage" };

        RatchetDelta delta = CompareSets("member declared on the aggregate", BaselineMembers, degraded);

        await Assert.That(delta.Regressions).IsEquivalentTo(new[] { "member declared on the aggregate: ResetUsage" })
            .Because("this is the claim that the ratchet above is load-bearing, made checkable: a "
                   + "member added to the aggregate must come out of the SAME comparator as a "
                   + "regression. If this fails, the comparator no longer reports growth and "
                   + "Ratchet_AggregateDeclaresNoMoreThanTheSevenMeasuredMembers is green for the "
                   + "wrong reason");

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("adding a member removes nothing");
    }

    /// <summary>
    ///     Control: a seventeenth holder is reported as a regression.
    /// </summary>
    [Test]
    public async Task Control_ASeventeenthHolderIsReportedAsARegression()
    {
        var degraded = new Dictionary<string, IReadOnlyList<string>>(BaselineHolderMap(), StringComparer.Ordinal)
        {
            ["src/Harbor.Application/Sessions/SomeNewService.cs"] = ["GetStats"],
        };

        RatchetDelta delta = CompareSets(
            "file that holds the aggregate",
            BaselineHolderMap().Keys,
            degraded.Keys);

        await Assert.That(delta.Regressions)
            .IsEquivalentTo(new[] { "file that holds the aggregate: src/Harbor.Application/Sessions/SomeNewService.cs" })
            .Because("a new file holding the aggregate must come out of the SAME comparator as a "
                   + "regression, or Ratchet_NoNewFileHoldsTheAggregate is green for the wrong reason");

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("adding a holder removes none");
    }

    /// <summary>
    ///     Control: a consumer that starts calling one more member is reported as a
    ///     regression, by the per-file comparator and not by the file-set one.
    /// </summary>
    [Test]
    public async Task Control_AConsumerCallingOneMoreMemberIsReportedAsARegression()
    {
        const string Consumer = "src/Harbor.Application/Agents/Pipeline/SteeringDrainBehavior.cs";
        var degraded = new Dictionary<string, IReadOnlyList<string>>(BaselineHolderMap(), StringComparer.Ordinal)
        {
            [Consumer] = ["GetStats", "RecordAppendedMessage"],
        };

        RatchetDelta delta = CompareMemberCalls(BaselineHolderMap(), degraded);

        await Assert.That(delta.Regressions)
            .IsEquivalentTo(new[] { "member called in " + Consumer + ": GetStats" })
            .Because("the file set is unchanged in this scenario, so only the per-file comparator "
                   + "can catch it. This is the control that proves "
                   + "Ratchet_NoConsumerCallsMoreOfTheAggregate is load-bearing: if the per-file "
                   + "comparison were dropped, this state would pass");

        await Assert.That(delta.Improvements).IsEmpty()
            .Because("the scenario only adds a call");
    }

    /// <summary>
    ///     Control, and the mirror of the other three: the split landing is reported as
    ///     PROGRESS, not as a regression. When the aggregate becomes a composition alias
    ///     over four roles it declares no members of its own, and the comparator has to say
    ///     "improvement" — otherwise the split could never be recorded, and a real regression
    ///     could be waved through as "just the refactor".
    /// </summary>
    [Test]
    public async Task Control_TheSplitLandingIsReportedAsProgressNotAsARegression()
    {
        RatchetDelta delta = CompareSets(
            "member declared on the aggregate",
            BaselineMembers,
            Array.Empty<string>());

        await Assert.That(delta.Regressions).IsEmpty()
            .Because("the split leaves the aggregate with no members of its own — everything is "
                   + "inherited from a role — so that state is the plan SUCCEEDING. Reporting it "
                   + "as a regression would mean the ratchet cannot tell progress from rot, which "
                   + "would make both the ratchet and its failure messages lies");

        await Assert.That(delta.Improvements).IsEquivalentTo(new[]
        {
            "member declared on the aggregate: Estimate",
            "member declared on the aggregate: EstimateMessage",
            "member declared on the aggregate: EstimateTokens",
            "member declared on the aggregate: GetStats",
            "member declared on the aggregate: RecordAppendedMessage",
            "member declared on the aggregate: RecordTurnUsage",
            "member declared on the aggregate: ShouldCompact",
        })
            .Because("all seven must show up as improvements, so the split's author is told exactly "
                   + "which rows to re-measure instead of discovering them one red build at a time");
    }

    // =====================================================================
    // Measurement and comparison.
    // =====================================================================

    /// <summary>
    ///     Resolves <c>Harbor.Abstractions.Sessions.{name}</c> against every loaded Harbor
    ///     assembly, or <see langword="null" /> when no assembly declares it. By name, not by
    ///     <c>typeof</c>, so a rename produces a readable failure rather than a build error.
    /// </summary>
    private static Type? Resolve(string name)
    {
        foreach (var asm in ArchitectureTestHelpers.LoadHarborAssemblies().Values)
        {
            if (asm.GetType($"Harbor.Abstractions.Sessions.{name}", throwOnError: false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    ///     The aggregate's declared members, ordinal-sorted. Empty when the type is missing,
    ///     which the interface test reports separately so the failure is not a silent zero.
    /// </summary>
    private static IReadOnlyList<string> MeasureAggregateMembers()
    {
        if (Resolve(AggregateName) is not { } aggregate)
        {
            return [];
        }

        return
        [
            .. aggregate
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static m => m.MemberType is not MemberTypes.Constructor)
                .Select(static m => m.Name)
                .OrderBy(static n => n, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Every product file whose comment-stripped text names the aggregate, mapped to the
    ///     aggregate members it calls, ordinal-sorted. Both halves read the same stripped
    ///     lines, so a file cannot be a holder without also being graded for its calls.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> MeasureHolders()
    {
        var measured = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            var members = new SortedSet<string>(StringComparer.Ordinal);
            bool namesAggregate = false;

            foreach (string line in SourceCommentStripper.StripAll(text.Split('\n')))
            {
                if (AggregateMention.IsMatch(line))
                {
                    namesAggregate = true;
                }

                foreach (Match call in MemberCall.Matches(line))
                {
                    members.Add(call.Groups[1].Value);
                }
            }

            if (namesAggregate)
            {
                measured[SourceScan.Relative(path)] = [.. members];
            }
        }

        return measured;
    }

    /// <summary>The measured table reshaped for lookup, so the tests compare like with like.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BaselineHolderMap()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (TokenHolderBaseline row in BaselineHolders)
        {
            map[row.File] = row.Members;
        }

        return map;
    }

    /// <summary>
    ///     Compares one named set against its baseline and labels each delta item with what
    ///     it is, so a failure message reads as a sentence. Every comparison in this file goes
    ///     through here, which is what lets the four <c>Control_</c> tests claim the ratchet is
    ///     load-bearing rather than merely asserting it.
    /// </summary>
    private static RatchetDelta CompareSets(string label, IEnumerable<string> baseline, IEnumerable<string> actual)
    {
        var expected = baseline.ToHashSet(StringComparer.Ordinal);
        var observed = actual.ToHashSet(StringComparer.Ordinal);

        var regressions = new List<string>();
        foreach (string item in observed.Except(expected, StringComparer.Ordinal)
                     .OrderBy(static item => item, StringComparer.Ordinal))
        {
            regressions.Add($"{label}: {item}");
        }

        var improvements = new List<string>();
        foreach (string item in expected.Except(observed, StringComparer.Ordinal)
                     .OrderBy(static item => item, StringComparer.Ordinal))
        {
            improvements.Add($"{label}: {item}");
        }

        return new RatchetDelta(regressions, improvements);
    }

    /// <summary>
    ///     Compares the per-holder member sets only, leaving the holder set to
    ///     <see cref="Ratchet_NoNewFileHoldsTheAggregate" />. A holder present in one table and
    ///     absent from the other compares against the empty set, so a rename or a move shows up
    ///     as a member delta here as well as a file delta there.
    /// </summary>
    private static RatchetDelta CompareMemberCalls(
        IReadOnlyDictionary<string, IReadOnlyList<string>> baseline,
        IReadOnlyDictionary<string, IReadOnlyList<string>> actual)
    {
        var regressions = new List<string>();
        var improvements = new List<string>();

        IEnumerable<string> files = baseline.Keys
            .Union(actual.Keys, StringComparer.Ordinal)
            .OrderBy(static file => file, StringComparer.Ordinal);

        foreach (string file in files)
        {
            RatchetDelta delta = CompareSets(
                $"member called in {file}",
                baseline.TryGetValue(file, out var expected) ? expected : Array.Empty<string>(),
                actual.TryGetValue(file, out var observed) ? observed : Array.Empty<string>());
            regressions.AddRange(delta.Regressions);
            improvements.AddRange(delta.Improvements);
        }

        return new RatchetDelta(regressions, improvements);
    }

    /// <summary>
    ///     Builds the per-consumer call pattern from <see cref="BaselineMembers" />. The
    ///     alternation is escaped because a member name is source, and a name like
    ///     <c>Estimate|x</c> must not become a pattern.
    /// </summary>
    private static Regex BuildMemberCallRegex()
    {
        string alternation = string.Join("|", BaselineMembers.Select(static m => Regex.Escape(m)));
        return new Regex($@"\??\.\s*({alternation})\s*[(<]", RegexOptions.Compiled);
    }
}
