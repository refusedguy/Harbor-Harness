// UiFrameworkNullabilityRules.cs — guard for the `null!` wave in src/Harbor.Ui.Framework*.
//
// The TEA state layer is 0/157 files on Result, and that is correct: a reducer
// cannot fail, so a Result in a transition would be a lie about an impossibility.
// What the layer was NOT free of was the other nullable lie — a member declared
// non-nullable that is initialised with the null-forgiving operator, so the
// compiler is asked to accept a value nobody assigned.
//
// Six of those lived in one class, ProjectionCache in
// src/Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs. They are
// now `required`, which makes the compiler state the invariant instead of a
// comment. This file stops the next contributor from putting one back.
//
// SCOPE. Deliberately src/Harbor.Ui.Framework* only. The composition root
// (src/Harbor.Hosting, HarborCompositionContext) carried the same five members
// and is gated separately by HostingCompositionNullabilityRules (#562).
// IAgent.State was the third such lie; #559 removed it by typing the property as
// Maybe<AgentState>, and AgentStateContractRules guards that contract. Widening
// THIS gate would make it red on a tree this PR is not changing.
//
// WHAT IS ALLOWED, and why — recorded once in SourceNullabilityScan, which now
// owns the shared scanner: `default!` is a different idiom with a legitimate
// use (StoreSubscriberViewModel.Selector<T>._last holds a default(T) sentinel
// behind a flag), and the literal `null!` inside a comment must not fail the
// gate.
//
// MECHANISM (#1086, step 2, conveyor g1)
// -------------------------------------
// The ban below is a ScanRule: the shared `SourceNullabilityScan.NullForgivingOnNull`
// verdict as one documented shape, no baseline, three planted controls, a discovery
// floor. Enumeration, matching and the control/discovery verdicts are ScanRunner's;
// this file keeps the issue prose and the test names.
//
// No Func-overload: the tabular line scan IS the old scanner — `Collect` strips
// with the shared `SourceScan.StripComments` and matches the shared verdict per
// line, which is byte-for-byte what `SourceNullabilityScan.FindNullForgivingNull`
// did. The scope is the `Harbor.Ui.Framework*` glob spelled out as explicit trees
// (the meta-package dir holds no .cs today and stays listed so a file added there
// is still graded): no nested tests/ or contrib/ directory exists under them, so
// the runner's walk yields the same set the old glob did.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that no file under <c>src/Harbor.Ui.Framework*/</c> suppresses a
///     nullable warning with the <c>null</c> literal plus the null-forgiving
///     operator.
/// </summary>
/// <remarks>
///     <para>
///         The rule is a source scan rather than a reflection test because
///         <c>null!</c> compiles away: by the time an assembly is loaded there is
///         nothing left to inspect, so the only way to see the operator is the
///         text. It therefore reuses <see cref="RepoPaths" />, the same
///         repo-root discovery the README gate uses, and degrades to "nothing to
///         check" when the marker file is absent (a published test host).
///     </para>
///     <para>
///         Runs as part of the build via the HarborArchitectureGate target in
///         Directory.Build.props, so a re-introduced <c>null!</c> fails CI without
///         anyone remembering to run this project.
///     </para>
/// </remarks>
public class UiFrameworkNullabilityRules
{
    /// <summary>
    ///     The <c>Harbor.Ui.Framework*</c> glob spelled out: every project directory
    ///     under <c>src/</c> the old walk matched, including the meta-package (no
    ///     <c>.cs</c> today — listed so a file added there is graded, not skipped).
    /// </summary>
    private static readonly string[] SliceTrees =
    [
        "src/Harbor.Ui.Framework",
        "src/Harbor.Ui.Framework.Abstractions",
        "src/Harbor.Ui.Framework.Projection",
        "src/Harbor.Ui.Framework.Rendering",
        "src/Harbor.Ui.Framework.Services",
        "src/Harbor.Ui.Framework.Sessions",
        "src/Harbor.Ui.Framework.State",
        "src/Harbor.Ui.Framework.ViewModels",
    ];

    /// <summary>The one banned shape, as data; the grading is the shared verdict.</summary>
    private const string NullForgiving = "NULL-FORGIVING-NULL";

    /// <summary>
    ///     The ban as data: the shared null-forgiving verdict over the slice, no
    ///     baseline, three planted controls, the slice itself as the floor.
    /// </summary>
    private static readonly ScanRule NullRule = new()
    {
        Id = "UiFrameworkNullability.NoNullForgivingNull",
        Trees = SliceTrees,
        Forbidden =
        [
            new ScanForbidden(
                NullForgiving,
                SourceNullabilityScan.NullForgivingOnNull,
                "a `null!` on a non-nullable member asks the compiler to accept a value nobody "
                + "assigned. Declare the member `required` when it is always assigned at a visible "
                + "construction site (ProjectionCache in DefaultUiProjector.cs), `Maybe<T>` when "
                + "absence is real, or `T?` when there is a working default."),
        ],
        Controls =
        [
            // The #562-era spelling: a non-nullable member initialised with the operator.
            new ScanControl("Shipped.cs", """
                public IProjectionCache Cache { get; set; } = null!;
                """, NullForgiving),
            // The fixed spelling: `required` states the invariant, no operator anywhere.
            new ScanControl("Fixed.cs", """
                public required IProjectionCache Cache { get; set; }
                """, null),
            // A comment documenting the old spelling is prose, not a violation: the
            // operator sits on a comment line, so the silence below is earned by the
            // comment stripper rather than by an accidental absence.
            new ScanControl("Documented.cs", """
                // Was `= null!` before the member became `required`.
                public required IProjectionCache Cache { get; set; }
                """, null),
        ],
        // Mirrors Assert_UiFrameworkSliceIsDiscoverable below (`> 100`): the walk must
        // yield at least 101 files, or the tree filter broke and the rule reads nothing.
        MinHits = 101,
        MustContain =
        [
            "src/Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs",
        ],
    };

    /// <summary>Every <c>.cs</c> file under the TEA state layer, sorted for a stable failure message.</summary>
    private static IReadOnlyList<string> EnumerateUiFrameworkSources() =>
        SourceNullabilityScan.EnumerateSources("Harbor.Ui.Framework*");

    /// <summary>
    ///     No file under <c>src/Harbor.Ui.Framework*/</c> initialises a field or
    ///     property with <c>null!</c>.
    /// </summary>
    /// <remarks>
    ///     Declared <c>required</c> when the member is genuinely always assigned —
    ///     that is what this file removed. Use <c>Maybe&lt;T&gt;</c> when absence is
    ///     real and the consumer must branch, and a plain <c>T?</c> when there is a
    ///     working default. Only a member that is assigned exactly once, at a
    ///     construction site the compiler can see, belongs in <c>required</c>.
    /// </remarks>
    [Test]
    public async Task Assert_NoNullForgivingNullInUiFramework()
    {
        IReadOnlyList<string> violations = ScanRunner.Evaluate(NullRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "src/Harbor.Ui.Framework* must not suppress nullable warnings with `null!`. "
                + "Use `required` when the member is always assigned at a visible construction site, "
                + "`Maybe<T>` when absence is real and the consumer must branch, or `T?` when there is "
                + "a working default. See ProjectionCache in DefaultUiProjector.cs for the worked example. "
                + $"Violations: {(violations.Count == 0 ? "(none)" : string.Join(", ", violations))}");
    }

    /// <summary>
    ///     The slice is discoverable at all — i.e. <see cref="RepoPaths.RepoRoot" />
    ///     resolved and at least one <c>src/Harbor.Ui.Framework*</c> directory exists.
    /// </summary>
    /// <remarks>
    ///     Without this, a broken repo-root discovery would make the gate above
    ///     vacuously green, which is the failure mode a ratchet has to guard
    ///     against.
    /// </remarks>
    [Test]
    public async Task Assert_UiFrameworkSliceIsDiscoverable()
    {
        var sources = EnumerateUiFrameworkSources();

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("Harbor.slnx must sit above the test host, or the null-forgiving gate checks nothing.");
        await Assert.That(sources.Count).IsGreaterThan(100)
            .Because($"expected the full TEA slice (9 projects, >100 files); found {sources.Count}.");
    }

    /// <summary>
    ///     The shared verdict flags the shipped spelling through the rule's own
    ///     matcher: a miss means the rule is inert and its passing means nothing.
    /// </summary>
    [Test]
    public async Task Matcher_FlagsTheNullForgivingSpelling()
    {
        const string Shipped = "public IProjectionCache Cache { get; set; } = null!;";

        await Assert.That(ScanRunner.Collect(NullRule, [("probe.cs", Shipped)]).Count).IsGreaterThan(0)
            .Because("the `= null!` spelling is a real positive control; a miss means the rule is inert.");
    }

    /// <summary>
    ///     The fixed spelling must not be flagged, or the guard cannot be adopted.
    /// </summary>
    [Test]
    public async Task Matcher_AcceptsTheRequiredSpelling()
    {
        const string Fixed = "public required IProjectionCache Cache { get; set; }";

        await Assert.That(ScanRunner.Collect(NullRule, [("probe.cs", Fixed)]).Count).IsEqualTo(0)
            .Because("the `required` spelling is the fix; flagging it would make the rule unsatisfiable.");
    }

    /// <summary>
    ///     A comment documenting the old spelling is prose, not a violation — and the
    ///     second assertion proves the silence is earned by the comment stripper.
    /// </summary>
    [Test]
    public async Task Matcher_IgnoresTheCommentDocumentingTheOldSpelling()
    {
        const string DocLine = "// Was `= null!` before the member became `required`.";
        const string Documented = """
            // Was `= null!` before the member became `required`.
            public required IProjectionCache Cache { get; set; }
            """;

        await Assert.That(ScanRunner.Collect(NullRule, [("probe.cs", Documented)]).Count).IsEqualTo(0)
            .Because(
                "StripComments runs before this matcher, so a contributor documenting WHY the member is no "
                + "longer null-forgiving must not fail the gate.");

        await Assert.That(SourceNullabilityScan.NullForgivingOnNull.IsMatch(DocLine)).IsTrue()
            .Because(
                "Positive control for the assertion above: with the comment stripper out of the path this "
                + "line is a violation, which is exactly the false positive the stripper exists to remove. "
                + "If this ever goes false the assertion above stops proving anything.");
    }

    /// <summary>
    ///     The planted controls — the shipped spelling and both fixed ones — drive
    ///     the REAL matcher: the offender reported under its sub-id, the fixes silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> failures = ScanRunner.CheckControls(NullRule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "the three planted controls must agree with the three Matcher cases above through "
                + "the rule's own parser — otherwise the rule guards nothing, or forbids the fix. "
                + string.Join("; ", failures));
    }

    /// <summary>
    ///     The rule's own scope really yields the TEA slice: over a hundred files
    ///     including DefaultUiProjector, so "no `null!`" means "nobody writes it"
    ///     rather than "the walk saw nothing".
    /// </summary>
    [Test]
    public async Task Scanner_SeesUiFrameworkSources()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(NullRule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/Harbor.Ui.Framework* holds far more than 100 .cs files including "
                + "DefaultUiProjector. A count this low — or a scope that stopped seeing that "
                + "file — means the tree walk broke and the rule above would be reporting 'clean' "
                + "about nothing. " + string.Join("; ", discovery));
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: it is wired from the first
    ///     row so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(NullRule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            NullRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(NullRule)));

        await Assert.That(stale).IsEmpty()
            .Because(
                "A baseline row that no longer matches reality is a lie: the site was fixed (or the file "
                + "left the scope), the row grandfathers nothing and hides the next real violation. "
                + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }
}
