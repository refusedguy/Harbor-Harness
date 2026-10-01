// ApprovalGateWaiterRaceRules.cs — source-level guard for the #765/#797 flake
// class: a test that asserts a terminal approval disposition while a
// WaitForDecisionAsync waiter is parked on the very TCS the decision completes.
//
// THE DEFECT THIS GUARDS
// ----------------------
// ApprovalCoordinator holds each gate in a `GateSlot` whose `Tcs` is built with
// RunContinuationsAsynchronously (ApprovalCoordinator.cs:26). Deciding a gate
// calls `TrySetResult` OUTSIDE the lock, which queues the waiter's continuation
// on the thread pool instead of running it inline. When that continuation runs,
// WaitForDecisionAsync calls `ForgetGate` (:213 / :218), which REMOVES the slot
// from `_gates`.
//
// So there is a window, opened by the first decision and closed by the pool, in
// which the same gate answers two different dispositions:
//
//   slot still registered  -> AlreadyDecided / AlreadyCancelled
//   slot already forgotten -> StaleGate (the UnknownGate branch, :112-115)
//
// Both are correct answers for their own state — a consumed gate is gone and
// fails closed. The defect is that the TEST owned neither. A test written as
// "park a waiter, decide, then stamp a twin and assert AlreadyDecided" is
// asserting a scheduling outcome: it usually wins on an idle runner and loses on
// a loaded one. That is exactly the reported symptom —
// "Expected to be equal to AlreadyDecided, but received StaleGate", green on
// `rerun --failed`.
//
// It is a FLAKE, not a false report, which is the dangerous part: the approval
// path is the one place a flake gets filed as noise the day it is real.
//
// WHY A SOURCE-TEXT RULE AND NOT AN ANALYZER
// ------------------------------------------
// The defect lives in test code, spans two test projects that do not reference
// each other (Harbor.Application.Tests and Harbor.App.Cli.Tests), and turns on
// the RELATIVE ORDER of three statements in one method. No existing analyzer sees
// statement order, and a Roslyn analyzer cannot be added to a rule set that every
// other gate in this project spells as a source scan.
//
// WHY THE SCOPE IS ALL OF tests/ AND NOT A LIST OF THREE FILES
// -----------------------------------------------------------
// A closed list is the #912 shape: the rule is stated over the files someone
// happened to be looking at when the bug was filed, and every test written after
// that is unguarded. The detector below keys on a shape, not on a filename, so it
// is walked over every `*.cs` under `tests/`.
//
// Note that this guard CANNOT use SourceScan.EnumerateCsFiles for that walk:
// SourceScan.IsBuildOutput returns true for any path containing "/tests/", and
// the repository root's own tests live under exactly that segment. Passing "tests"
// to EnumerateCsFiles would filter out every file it just enumerated and the rule
// would report green while enforcing nothing — the #901/#906 trap. Hence
// EnumerateTestSources below.
//
// THE RULE, IN FULL
// -----------------
// Inside one test method: if a `WaitForDecisionAsync` result is parked into a
// local, then asserting `AlreadyDecided` or `AlreadyCancelled` BEFORE that local
// is awaited is a build failure.
//
// Only those two dispositions are policed, and the exclusions are load-bearing:
//
//   * `Accepted` is the call that COMPLETES the TCS. At that moment the slot is
//     by construction present and undecided, and the waiter's continuation cannot
//     have run yet, so `Accepted` is deterministic. Four current tests rely on it.
//   * `StaleGate` asserted on a still-undecided gate comes from the identity
//     mismatch branch, which the parked waiter does not touch. Also
//     deterministic, and four current tests rely on it.
//   * A terminal disposition asserted AFTER `await wait` is outside the window
//     this rule describes, and a disposition asserted in a method that parks no
//     waiter at all is the shape #808 introduced.
//
// "PARKED" IS DETECTED TWO WAYS, AND BOTH MATTER
// ---------------------------------------------
// A local initialiser (`var wait = …`, or an explicitly typed one) names the local,
// so the window can be closed precisely at `await <that name>`. A waiter held some
// other way — a tuple element, an object initialiser, a field — has no name to close
// it with, and the honest reading is that nothing in the method can be shown to close
// the window, so it runs to the end of the method.
//
// The first cut of this file had only the local form, and a fixture holding the
// waiter in a named tuple came out clean. That is the same race, so it belongs in the
// class rather than in a list of exceptions. `WaiterCall` is the second pass.
//
// NON-VACUITY
// -----------
// Measured on this tree, not asserted about it:
//
//   * pre-#808 (`5d8a73d4`)  -> the detector reports 3 violations, one per site
//     #797 identified, at the line of each twin's assertion.
//   * current `dev`          -> 0 violations across the whole `tests/` tree.
//
// Four tests defend that in CI, so a future edit that silently breaks the
// detector fails here instead of leaving a rule that cannot fail:
//
//   * `Scanner_FindsTheTestTree` — the walk really reached the test sources, the
//     three #797 files are in scope, and this file is deliberately not.
//   * `Rule_IsGreenOnTheCurrentTreeAtZeroViolations` — pins the count at 0, so a
//     detector that starts matching nothing is a failure rather than a pass.
//   * `Detector_FiresOnTheThreeShapesFromIssue765` — the detector, in isolation,
//     fires on six racy shapes: the three from pre-#808, the AlreadyCancelled
//     sibling no current test happens to write, an explicitly typed local, and a
//     waiter held in a named tuple.
//   * `Detector_StaysQuietOnTheDispositionsThatAreDeterministic` — and stays
//     quiet on six that it must not flag.
//
//   * `Detector_IgnoresProseThatDescribesTheRule` — the #808 tests explain
//     themselves in comments naming these dispositions; prose cannot fail a build.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #765/#797 guard: no test asserts a terminal approval disposition while a
///     parked <c>WaitForDecisionAsync</c> waiter can still forget the gate slot.
/// </summary>
public class ApprovalGateWaiterRaceRules
{
    /// <summary>
    ///     A method header up to its opening parenthesis. The return type is a
    ///     permissive character class because these are test methods
    ///     (<c>Task</c>, <c>ValueTask</c>) and the rule must not care — but it
    ///     deliberately contains NO whitespace, so the match cannot walk across a
    ///     newline and swallow a whole class body before reaching some later
    ///     <c>Name(</c>.
    /// </summary>
    private static readonly Regex MethodHeader = new(
        @"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\.\?]+[ \t]+(?<name>\w+)[ \t]*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     A waiter parked into a local. <c>var</c> is not required: an explicitly
    ///     typed local (<c>Task&lt;ApprovalResolution?&gt; wait = …</c>) is the same
    ///     race. The statement is bounded by <c>;</c> so a match cannot drift back
    ///     across an earlier statement.
    /// </summary>
    private static readonly Regex ParkedWaiter = new(
        @"(?:var|[\w<>\[\]\.]+)\s+(?<waiter>\w+)\s*=\s*[^;]*?WaitForDecisionAsync\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     The same call found anywhere, not only as a local initialiser.
    /// </summary>
    /// <remarks>
    ///     A SECOND pass, not a widening of <see cref="ParkedWaiter" />, and the
    ///     reason is worth recording: the local form carries the awaited name in its
    ///     own declaration, so its window can be closed precisely. A call parked some
    ///     other way — a tuple element, an object initialiser, a field — has no such
    ///     name, and nothing in the method can be shown to close the window, so it runs
    ///     to the end of the method.
    ///     <para>
    ///         The first cut of this guard had only the local pass, and a fixture
    ///         holding the waiter in a named tuple came out clean. Same race, so it
    ///         belongs in the class rather than in a list of exceptions.
    ///     </para>
    /// </remarks>
    private static readonly Regex WaiterCall = new(
        @"WaitForDecisionAsync\s*\(",
        RegexOptions.Compiled);

    /// <summary>The same local being awaited, which closes the race window.</summary>
    private static readonly Regex AwaitWaiter = new(@"\bawait\s+(?<waiter>\w+)\b", RegexOptions.Compiled);

    /// <summary>
    ///     The dispositions that are only correct while the slot is still in
    ///     <c>_gates</c>, and therefore only deterministic while no waiter can have
    ///     consumed it. <c>Accepted</c> and <c>StaleGate</c> are excluded on purpose —
    ///     see the header.
    /// </summary>
    private static readonly Regex TerminalDisposition = new(
        @"ApprovalDecisionDisposition\.(?<disposition>AlreadyDecided|AlreadyCancelled)\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     This file's own name, excluded from the walk.
    /// </summary>
    /// <remarks>
    ///     Every other guard in this project keeps its scan scope and its own location
    ///     disjoint — they police <c>src/</c> or <c>apps/</c> from a file under
    ///     <c>tests/</c>. This one walks <c>tests/</c>, so it necessarily walks ITSELF,
    ///     and the <c>mustFail</c> fixtures above are deliberately-bad sample code: run
    ///     the rule over this file unmodified and it reports 11 violations, 10 of them
    ///     inside <c>Detector_FiresOnTheThreeShapesFromIssue765</c> reading a fixture
    ///     string. That is the #899 trap — a guard that counts itself. Caught by running
    ///     the rule over the real tree before opening the PR, not by inspection.
    ///     <para>
    ///         The exclusion is one filename, not "any file containing a fixture": widening
    ///         it to a shape would reintroduce the #912 hole (a rule that is silent about
    ///         code next door). What this file does is pinned by the fixture tests, which
    ///         call <see cref="FindRaces" /> directly and cannot be skipped by the walk.
    ///     </para>
    /// </remarks>
    private const string SelfFileName = "ApprovalGateWaiterRaceRules.cs";

    // ── the rule ─────────────────────────────────────────────────────────────

    [Test]
    public async Task NoTest_AssertsATerminalDispositionWhileAParkedWaiterCanForgetTheGate()
    {
        var violations = new List<string>();

        foreach (string file in EnumerateTestSources())
        {
            if (SourceScan.TryReadAllText(file) is not { } source)
            {
                continue;
            }

            foreach (WaiterRace race in FindRaces(source))
            {
                violations.Add(
                    $"{SourceScan.Relative(file)}:{race.Line} — `{race.Method}` asserts "
                    + $"ApprovalDecisionDisposition.{race.Disposition} while its local "
                    + $"`{race.Waiter}` is still parked on the gate's TCS. The decision that "
                    + "completes that TCS queues the waiter (RunContinuationsAsynchronously), "
                    + "and on consume it calls ForgetGate, which drops the slot — so the same "
                    + "gate answers AlreadyDecided or StaleGate depending on pool timing. "
                    + "Stamp the twin with no waiter in flight, then await the waiter "
                    + "afterwards (the shape #808 introduced).");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A terminal disposition is a property of the slot, not of the scheduler. Asserted "
                + "with a live waiter it is a coin flip that reports green until the runner is busy — "
                + "and this is the approval path, where the flake reads as noise the day it is a real "
                + "regression. Park no waiter before the assertion, or await it first.");
    }

    // ── non-vacuity ──────────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheTestTree()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        var files = EnumerateTestSources();

        await Assert.That(files.Count).IsGreaterThan(400)
            .Because(
                $"The test tree should hold hundreds of source files; found {files.Count}. A near-zero "
                + "count means the walk went stale (or someone reused SourceScan.EnumerateCsFiles, whose "
                + "IsBuildOutput rejects every path containing \"/tests/\" — including this tree).");

        // The walk must reach the three files that actually carry the shape, or the
        // rule is enforcing nothing on the code it was written for.
        string[] mustBeInScope =
        [
            "tests/Harbor.Application.Tests/ApprovalCoordinatorTests.cs",
            "tests/Harbor.Application.Tests/ApprovalCoordinatorTupleTests.cs",
            "tests/Harbor.App.Cli.Tests/ApprovalGateRouterTupleTests.cs"
        ];

        foreach (string required in mustBeInScope)
        {
            string absolute = Path.Combine(root, required);
            await Assert.That(File.Exists(absolute)).IsTrue()
                .Because($"The three #797 twins live in {required}; if it moved, this guard's scope must follow it.");
            await Assert.That(files.Contains(absolute)).IsTrue()
                .Because($"{required} is in scope for the rule but the walk did not return it — the walk is filtering live files.");
        }

        // The one deliberate exclusion, asserted rather than assumed: this file walks
        // all of tests/ and therefore walks itself, and its mustFail fixtures are
        // deliberately-bad sample code. See SelfFileName. Dropping the exclusion makes
        // Rule_IsGreenOnTheCurrentTreeAtZeroViolations report 11, so this is belt and
        // braces — the point is that the exclusion is a decision on the record, not a
        // filter that appeared.
        string self = Path.Combine(
            root,
            "tests",
            "Harbor.Architecture.Tests",
            SelfFileName);

        await Assert.That(File.Exists(self)).IsTrue()
            .Because("This guard excludes itself from its own walk; if the file was renamed, the exclusion is dead and needs updating with it.");
        await Assert.That(files.Contains(self)).IsFalse()
            .Because(
                "This guard's own mustFail fixtures are deliberately racy sample code. If the file is in "
                + "scope, the rule reports 11 violations of itself — the #899 trap. SelfFileName must stay "
                + "in step with this file's name.");
    }

    [Test]
    public async Task Rule_IsGreenOnTheCurrentTreeAtZeroViolations()
    {
        // Pins the count rather than the shape, so a detector edit that starts
        // matching nothing fails here. Measured on this tree: 0.
        var count = 0;
        foreach (string file in EnumerateTestSources())
        {
            if (SourceScan.TryReadAllText(file) is { } source)
            {
                count += FindRaces(source).Count;
            }
        }

        await Assert.That(count).IsEqualTo(0)
            .Because(
                "The whole point of #808 was to leave this class empty, and the rule above reports each "
                + "site separately. If this number is anything but 0, the fix regressed or the tree grew the "
                + "shape again — read the list from the failing test above before assuming the detector moved.");
    }

    [Test]
    public async Task Detector_FiresOnTheThreeShapesFromIssue765()
    {
        // The first three are the literal pre-#808 sources (5d8a73d4) — one per twin
        // #797 identified. If the detector is quiet on those it guards nothing, and a
        // green run on the current tree means nothing: that is the #948 failure mode,
        // a guard reporting 8/8 while the bug was alive. The last three are siblings
        // of the same class, so a detector that only knows the shapes that happened
        // to exist in #797 is a #912-shaped rule wearing a fixture list.
        string[] mustFail =
        [
            // ApprovalGateRouterTupleTests.BoundMatch_Accepted_WaiterResolves, pre-#808.
            """
            [Test]
            public async Task BoundMatch_Accepted_WaiterResolves()
            {
                var coordinator = NewCoordinator();
                var gate = coordinator.BeginGate();
                var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);
                await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
                await Assert.That(coordinator.DecideApproval(gate.Id, "inv-1", 1, Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);
                var outcome = await wait;
            }
            """,
            // ApprovalCoordinatorTests.DoubleDecide_SecondIsAlreadyDecided, pre-#808.
            """
            [Test]
            public async Task DoubleDecide_SecondIsAlreadyDecided()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.Accepted);
                await Assert.That(coordinator.DecideApproval("g1", new ApprovalResolution(false, false)))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);
                var outcome = await wait;
            }
            """,
            // ApprovalCoordinatorTupleTests.TupleDoubleDecide_SecondIsAlreadyDecided, pre-#808.
            """
            [Test]
            public async Task TupleDoubleDecide_SecondIsAlreadyDecided()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1", "inv-1", 1);
                var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
                await Assert.That(ui.Approve()).IsEqualTo(ApprovalDecisionDisposition.Accepted);
                await Assert.That(ui.Deny()).IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);
                var outcome = await wait;
            }
            """,
            // The AlreadyCancelled sibling of the same race, which no current test
            // happens to write: RequestCancel completes the TCS, the waiter forgets
            // the slot, and the twin reads StaleGate instead of AlreadyCancelled.
            """
            [Test]
            public async Task Cancel_ThenTwin_AssertsAlreadyCancelled()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
                coordinator.RequestCancel(new FakeRunner());
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyCancelled);
                var outcome = await wait;
            }
            """,
            // An explicitly typed local is the same race. Listed here so the
            // detector's `var`-tolerance is pinned: a rule that only understood
            // `var wait = …` would miss it without failing anything.
            """
            [Test]
            public async Task ExplicitlyTypedWaiter()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                Task<ApprovalResolution?> wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.Accepted);
                await Assert.That(coordinator.DecideApproval("g1", Deny()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);
                var outcome = await wait;
            }
            """,
            // A waiter held in a named tuple: the call is not a local initialiser, so
            // only the anywhere-pass sees it, and nothing in the method can be shown
            // to close the window before the twin. The first cut of this guard missed
            // it entirely, which is the reason the second pass exists.
            """
            [Test]
            public async Task TupleHeldWaiter()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                var parked = (Wait: coordinator.WaitForDecisionAsync("g1", CancellationToken.None));
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.Accepted);
                await Assert.That(coordinator.DecideApproval("g1", Deny()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);
                var outcome = await parked.Wait;
            }
            """
        ];

        foreach (string bad in mustFail)
        {
            await Assert.That(FindRaces(bad)).IsNotEmpty()
                .Because(
                    "This is the shape #797 fixed and #765 reported. A detector that does not fire on it "
                    + "reports green on the very tree the bug was alive on:\n" + bad);
        }
    }

    [Test]
    public async Task Detector_StaysQuietOnTheDispositionsThatAreDeterministic()
    {
        // Every one of these is a real, current test shape. Flagging any of them
        // would make the guard cry wolf and get deleted.
        string[] mustPass =
        [
            // BoundMatch_Accepted_WaiterResolves as #808 left it: the first decision
            // is the one that completes the TCS, so `Accepted` cannot race.
            """
            [Test]
            public async Task BoundMatch_Accepted_WaiterResolves()
            {
                var gate = coordinator.BeginGate();
                var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);
                await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
                var outcome = await wait;
            }
            """,
            // StaleReplay_Rejected_GenuineKeyStillLands: StaleGate comes from the
            // identity-mismatch branch on a still-undecided gate.
            """
            [Test]
            public async Task StaleReplay_Rejected_GenuineKeyStillLands()
            {
                var gate = coordinator.BeginGate();
                var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);
                await Assert.That(coordinator.DecideApproval(gate.Id, "inv-2", 1, Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.StaleGate);
                await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
                var outcome = await wait;
            }
            """,
            // The #808 fix itself: the twin is stamped with NO waiter in flight, so
            // AlreadyDecided follows slot.Decided alone.
            """
            [Test]
            public async Task BoundMatch_LateTwin_IsAlreadyDecided()
            {
                var gate = coordinator.BeginGate();
                await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
                await Assert.That(coordinator.DecideApproval(gate.Id, "inv-1", 1, Deny()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyDecided);
                var outcome = await coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);
            }
            """,
            // DecideAfterCancel_IsAlreadyCancelled as it exists today: the waiter is
            // created AFTER the assertion, so there is no parked waiter to race.
            """
            [Test]
            public async Task DecideAfterCancel_IsAlreadyCancelled()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                coordinator.RequestCancel(new FakeRunner());
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyCancelled);
                var outcome = await coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
            }
            """,
            // Terminal disposition asserted after the waiter is consumed: outside
            // the window this rule describes.
            """
            [Test]
            public async Task AfterConsume_TheSlotIsGone()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                var wait = coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.Accepted);
                var outcome = await wait;
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.StaleGate);
            }
            """,
            // A waiter call that is immediately awaited and never held: nothing is
            // parked, so no later assertion can land inside a window. This is the
            // shape most current tests use, and the one the anywhere-pass must not
            // mistake for a parked waiter.
            """
            [Test]
            public async Task WaitedInline_NothingIsParked()
            {
                var coordinator = NewCoordinator();
                coordinator.RegisterGate("g1");
                coordinator.RequestCancel(new FakeRunner());
                await Assert.That(coordinator.DecideApproval("g1", Approve()))
                    .IsEqualTo(ApprovalDecisionDisposition.AlreadyCancelled);
                var outcome = await coordinator.WaitForDecisionAsync("g1", CancellationToken.None);
                await Assert.That(outcome).IsNull();
            }
            """
        ];

        foreach (string good in mustPass)
        {
            await Assert.That(FindRaces(good)).IsEmpty()
                .Because(
                    "This is not the racy shape — flagging it would block correct tests and get the guard "
                    + "deleted:\n" + good);
        }
    }

    [Test]
    public async Task Detector_IgnoresProseThatDescribesTheRule()
    {
        // The #808 tests explain themselves in comments, and the comment in
        // BoundMatch_Accepted_WaiterResolves names AlreadyDecided in prose inside
        // the very method that parks a waiter. A guard that failed on its own
        // documentation is a guard nobody keeps.
        const string Described = """
            [Test]
            public async Task ExplainsItself()
            {
                var gate = coordinator.BeginGate();
                // A parked waiter forgets the slot on consume: the test thread usually
                // sees AlreadyDecided, a loaded runner sees StaleGate instead.
                var wait = coordinator.WaitForDecisionAsync(gate.Id, CancellationToken.None);
                await Assert.That(router.TryRouteApprovalKey(KeyEvent.Char(new Rune('y')))).IsTrue();
                var outcome = await wait;
            }
            """;

        await Assert.That(FindRaces(Described)).IsEmpty()
            .Because(
                "Comments are stripped before the rule runs, so prose describing this exact race cannot "
                + "fail the build. The three #808 tests are full of such prose.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>One racy assertion, with the line the assertion sits on.</summary>
    private sealed record WaiterRace(int Line, string Method, string Waiter, string Disposition);

    /// <summary>
    ///     Every <c>*.cs</c> under <c>tests/</c>, minus build output and sibling
    ///     worktrees.
    /// </summary>
    /// <remarks>
    ///     Deliberately NOT <c>SourceScan.EnumerateCsFiles</c>: that helper's
    ///     <c>IsBuildOutput</c> predicate returns true for any path containing
    ///     <c>/tests/</c>, so it would discard every file in the tree it was asked to
    ///     walk and the rule would pass vacuously.
    /// </remarks>
    private static IReadOnlyList<string> EnumerateTestSources()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string dir = Path.Combine(root, "tests");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string normalised = file.Replace('\\', '/');
            if (normalised.Contains("/obj/", StringComparison.Ordinal)
                || normalised.Contains("/bin/", StringComparison.Ordinal)
                || normalised.Contains("/.worktrees/", StringComparison.Ordinal)
                || string.Equals(
                    Path.GetFileName(file),
                    SelfFileName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            found.Add(file);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     Every terminal-disposition assertion that a parked waiter can still race,
    ///     in the order it appears in the file.
    /// </summary>
    private static IReadOnlyList<WaiterRace> FindRaces(string source)
    {
        var races = new List<WaiterRace>();

        // Comments carry the prose that documents this very class, and a string
        // literal could name the enum member; both are blanked so only code counts.
        string code = BlankOutLiterals(SourceScan.StripComments(source));

        foreach ((string method, int bodyStart, int bodyEnd) in MethodBodies(code))
        {
            string body = code[bodyStart..bodyEnd];

            // Two passes over the same body, because "where the waiter is parked" and
            // "where the window closes" are two different questions:
            //
            //   * ParkedWaiter — a local initialiser. It names the local, so the window
            //     can be closed precisely at `await <that local>`.
            //   * WaiterCall — the call appears with no name of its own (a tuple
            //     element, an object initialiser, a field). Nothing in the method can
            //     be shown to close the window, so it runs to the end of the method.
            //
            // The second pass deliberately does not exclude sites the first already
            // reported: a tuple-held waiter that IS awaited by a name the first pass
            // cannot see is still the same race, and reporting it once under the
            // first rule and again under the second would be noise. So the local pass
            // runs first and the anywhere-pass skips a site it already produced.
            // The span each ParkedWaiter match already covers, so the anywhere-pass
            // below can skip exactly those calls. Testing ParkedWaiter at the call's
            // own index would not work: a local-initialiser match STARTS at
            // `var <name> =`, which is BEFORE the call, so an anchored test at the
            // call's index misses it and every parked waiter gets reported twice.
            // Leaning on the line-level dedup in AddRaces to hide that would be
            // worse — the rule would then be right by accident, and a genuine
            // double-report would be indistinguishable from the benign one.
            var covered = new List<(int Start, int End)>();

            foreach (Match parked in ParkedWaiter.Matches(body))
            {
                string waiter = parked.Groups["waiter"].Value;
                int afterPark = parked.Index + parked.Length;
                covered.Add((parked.Index, afterPark));

                // The window closes at the first `await <waiter>`. A local that is
                // never awaited leaves the window open to the end of the method,
                // which is correct: the continuation still runs ForgetGate.
                string tail = body[afterPark..];
                int? awaitedAt = AwaitWaiter.Matches(tail)
                    .FirstOrDefault(m => m.Groups["waiter"].Value == waiter)
                    ?.Index;
                int windowEnd = awaitedAt is null ? body.Length : afterPark + awaitedAt.Value;

                AddRaces(races, code, bodyStart, body, method, waiter, afterPark, windowEnd);
            }

            foreach (Match call in WaiterCall.Matches(body))
            {
                if (covered.Any(c => call.Index >= c.Start && call.Index < c.End))
                {
                    continue;
                }

                AddRaces(
                    races,
                    code,
                    bodyStart,
                    body,
                    method,
                    waiter: "<unnamed>",
                    windowStart: call.Index + call.Length,
                    windowEnd: body.Length);
            }
        }

        return races;
    }

    /// <summary>
    ///     Records every terminal disposition in <c>body[windowStart..windowEnd]</c>,
    ///     one report per assertion site.
    /// </summary>
    /// <remarks>
    ///     Deduplicated on line + method + disposition, NOT on the waiter name. A waiter
    ///     held in a named tuple is seen twice — once by each pass — and the two passes
    ///     disagree about what it is called (<c>parked</c> vs <c>&lt;unnamed&gt;</c>), so
    ///     keying on the name would report one defect twice. Two distinct assertions on
    ///     one physical line would also collapse, which costs nothing: both would be the
    ///     same shape and the line is what a reader has to go and look at.
    /// </remarks>
    private static void AddRaces(
        List<WaiterRace> races,
        string code,
        int bodyStart,
        string body,
        string method,
        string waiter,
        int windowStart,
        int windowEnd)
    {
        // Sliced rather than Regex.Matches(input, startat, length): the window reads
        // as a range of the body, and a slice keeps the offset arithmetic below
        // explicit instead of depending on which Matches overload binds.
        foreach (Match disposition in TerminalDisposition.Matches(body[windowStart..windowEnd]))
        {
            int absolute = bodyStart + windowStart + disposition.Index;

            int line = code[..absolute].Count(c => c == '\n') + 1;
            string what = disposition.Groups["disposition"].Value;

            // One report per assertion site. A local function inside a test method is a
            // body of its own AND part of the enclosing one, and a tuple-held waiter is
            // seen by both passes under two different names — either way the reader has
            // exactly one line to go and look at.
            if (races.Any(r => r.Line == line && r.Method == method && r.Disposition == what))
            {
                continue;
            }

            races.Add(new WaiterRace(line, method, waiter, what));
        }
    }

    /// <summary>
    ///     (name, start, end) for every method with a brace body. The parameter list
    ///     is matched with a balanced scan rather than <c>[^)]*</c> so a default
    ///     argument containing a closing parenthesis cannot truncate the header.
    /// </summary>
    private static IEnumerable<(string Name, int Start, int End)> MethodBodies(string code)
    {
        foreach (Match header in MethodHeader.Matches(code))
        {
            int open = code.IndexOf('(', header.Index + header.Length - 1);
            int close = BalancedClose(code, open);
            if (close < 0)
            {
                continue;
            }

            int brace = close + 1;
            while (brace < code.Length && char.IsWhiteSpace(code[brace]))
            {
                brace++;
            }

            // Expression-bodied, abstract, extern and interface members have no
            // block to scan.
            if (brace >= code.Length || code[brace] != '{')
            {
                continue;
            }

            int end = BalancedClose(code, brace, '{', '}');
            if (end < 0)
            {
                continue;
            }

            yield return (header.Groups["name"].Value, brace, end);
        }
    }

    /// <summary>
    ///     Index of the delimiter closing the one at <paramref name="open" />, or -1.
    /// </summary>
    private static int BalancedClose(string code, int open, char openChar = '(', char closeChar = ')')
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == openChar)
            {
                depth++;
            }
            else if (code[i] == closeChar)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    ///     Replaces string and character literal contents with spaces, preserving
    ///     offsets so reported line numbers still point at real source.
    /// </summary>
    private static string BlankOutLiterals(string source)
    {
        var blanked = new StringBuilder(source);
        int i = 0;
        while (i < source.Length)
        {
            char quote = source[i];
            if (quote is not ('"' or '\''))
            {
                i++;
                continue;
            }

            i++;
            while (i < source.Length && source[i] != quote)
            {
                // An escaped quote does not end the literal.
                if (source[i] == '\\' && i + 1 < source.Length)
                {
                    blanked[i] = ' ';
                    blanked[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                // A newline inside a literal is a verbatim/raw-string boundary we
                // do not model; stop rather than blank the rest of the file.
                if (source[i] == '\n')
                {
                    break;
                }

                blanked[i] = ' ';
                i++;
            }

            i++;
        }

        return blanked.ToString();
    }
}
