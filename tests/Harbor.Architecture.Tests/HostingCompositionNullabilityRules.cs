// HostingCompositionNullabilityRules.cs — guard for the `null!` wave in the
// composition root (src/Harbor.Hosting), issue #562.
//
// The TEA state layer already has this gate
// (UiFrameworkNullabilityRules). It deliberately scoped itself out of the
// composition root, and named this file as the reason:
//
//     "The same pattern exists elsewhere in the repo (HarborCompositionContext,
//      IAgent.State) and those are owned by their own issues."
//
// #562 is that issue for HarborCompositionContext. Five public non-nullable
// properties were initialised with `null!` and filled in by whichever AddHarbor
// module happened to run first:
//
//     public IEventBus EventBus { get; internal set; } = null!;
//     public AgentRegistry Agents { get; internal set; } = null!;    // + Tools,
//     public ProviderRegistry Providers { get; internal set; } = null!;  //   Providers, Panels
//
// The C# `non-nullable` annotation is a contract the compiler checks in ONE
// file and not at all once the value arrives through DI. In the composition
// root that is not academic: a reordered module in Registration.AddHarbor did
// not fail, it read a member that was null — either as an NRE three frames
// later, or (StorageModule reading ctx.Registries.SessionStores) as a silently
// empty registry and a container that looked complete.
//
// The fix was structural — the four registries are constructor arguments of
// HarborRegistries now, and the two assigned-later members throw by name
// instead of returning null. This file stops the next contributor from putting
// a `null!` back into the composition root, where the compiler cannot help.
//
// WHAT IS ALLOWED: see SourceNullabilityScan, which owns the shared scanner
// and the reasoning behind `default!` and comment-stripping. The scope here is
// src/Harbor.Hosting; IAgent.State (the other site #562-era audits name) is a
// separate type in a different layer and is not in this slice.
//
// Runs as part of the build via the HarborArchitectureGate target in
// Directory.Build.props, so a re-introduced `null!` fails CI without anyone
// remembering to run this project.
//
// MECHANISM (#1086, step 2, conveyor g2)
// -------------------------------------
// The ban below is a ScanRule: the shared `SourceNullabilityScan.NullForgivingOnNull`
// verdict as one documented shape, no baseline, three planted controls, a discovery
// floor. Enumeration, matching and the control/discovery verdicts are ScanRunner's;
// this file keeps the issue prose and the test names.
//
// No Func-overload: the tabular line scan IS the old scanner — `Collect` strips
// with the shared `SourceScan.StripComments` and matches the shared verdict per
// line, which is byte-for-byte what `SourceNullabilityScan.FindNullForgivingNull`
// did. The scope is `src/Harbor.Hosting` verbatim: no nested tests/ or contrib/
// directory exists under it, so the runner's walk yields the same set the old
// slice walk did.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that no file in the composition root suppresses a nullable
///     warning with the <c>null</c> literal plus the null-forgiving operator.
/// </summary>
public class HostingCompositionNullabilityRules
{
    /// <summary>The slice this gate covers — <c>src/&lt;name&gt;</c>.</summary>
    private const string Slice = "Harbor.Hosting";

    /// <summary>Repo-relative trees for the rule's scope — the slice verbatim.</summary>
    private static readonly string[] SliceTrees = ["src/Harbor.Hosting"];

    /// <summary>The one banned shape, as data; the grading is the shared verdict.</summary>
    private const string NullForgiving = "NULL-FORGIVING-NULL";

    /// <summary>
    ///     The ban as data: the shared null-forgiving verdict over the composition
    ///     root, no baseline, three planted controls, the slice itself as the floor.
    /// </summary>
    private static readonly ScanRule NullRule = new()
    {
        Id = "HostingCompositionNullability.NoNullForgivingNull",
        Trees = SliceTrees,
        Forbidden =
        [
            new ScanForbidden(
                NullForgiving,
                SourceNullabilityScan.NullForgivingOnNull,
                "a `null!` in the composition root is a wrong module order waiting to happen: "
                + "a value assigned by one AddHarbor module and read by another is not checked by "
                + "the compiler. Assign such a member through a set-once setter that throws by name, "
                + "or take it as a constructor argument. See issue #562."),
        ],
        Controls =
        [
            // Verbatim HarborCompositionContext.cs as it shipped — the #562 spelling.
            new ScanControl("Shipped.cs", """
                public IEventBus EventBus { get; internal set; } = null!;
                """, NullForgiving),
            // The fixed spelling: a get-only member assigned once in the constructor.
            new ScanControl("Fixed.cs", """
                public HarborRegistries Registries { get; }
                public HarborCompositionContext(HarborRegistries registries) { Registries = registries; }
                """, null),
            // A comment documenting the old spelling is prose, not a violation: the
            // operator sits on a comment line, so the silence below is earned by the
            // comment stripper rather than by an accidental absence.
            new ScanControl("Documented.cs", """
                // Was `= null!` before the registries became constructor arguments (#562).
                public HarborRegistries Registries { get; }
                """, null),
        ],
        // Mirrors Assert_HostingSliceIsDiscoverable below (`> 0`): the floor is
        // deliberately low — the point is that the scan RAN, not how much it covered.
        MinHits = 1,
        MustContain =
        [
            "src/Harbor.Hosting/HarborCompositionContext.cs",
        ],
    };

    /// <summary>
    ///     The five members #562 removed, spelled out so the failure message
    ///     points at the history rather than only at the line.
    /// </summary>
    private const string WhatItCatches =
        "HarborCompositionContext.EventBus, HarborRegistries.Agents / Tools / Providers / Panels were the five "
        + "members behind this gate: all were `= null!` and all were non-null only because an earlier AddHarbor "
        + "module assigned them. A reordered module then read a null — as an NRE, or (StorageModule reading "
        + "ctx.Registries.SessionStores) as a silently empty registry. Assign such a member through a "
        + "set-once setter that throws by name, or take it as a constructor argument.";

    /// <summary>
    ///     No file in <c>src/Harbor.Hosting</c> initialises a field or property
    ///     with <c>null!</c>.
    /// </summary>
    [Test]
    public async Task Assert_NoNullForgivingNullInHostingCompositionRoot()
    {
        List<string> violations = ScanRunner.Evaluate(NullRule);

        await Assert.That(violations).IsEmpty()
            .Because(
                "src/Harbor.Hosting is the composition root: a value assigned by one module and read by another "
                + "is not checked by the compiler, so a `null!` here is a wrong module order waiting to happen. "
                + $"{WhatItCatches} Violations: {(violations.Count == 0 ? "(none)" : string.Join(", ", violations))}");
    }

    /// <summary>
    ///     The slice is discoverable at all — i.e. <see cref="RepoPaths.RepoRoot" />
    ///     resolved and <c>src/Harbor.Hosting</c> exists.
    /// </summary>
    /// <remarks>
    ///     Without this, a broken repo-root discovery would make the gate above
    ///     vacuously green, which is the failure mode a ratchet has to guard
    ///     against. The floor is deliberately low (one file): the point is
    ///     that the scan RAN, not how much it covered.
    /// </remarks>
    [Test]
    public async Task Assert_HostingSliceIsDiscoverable()
    {
        IReadOnlyList<string> sources = SourceNullabilityScan.EnumerateSources(Slice);

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("Harbor.slnx must sit above the test host, or the null-forgiving gate checks nothing.");
        await Assert.That(sources.Count).IsGreaterThan(0)
            .Because($"expected the composition root slice (src/{Slice}); found {sources.Count}.");
    }

    /// <summary>
    ///     The shared verdict flags the shipped spelling through the rule's own
    ///     matcher: a miss means the rule is inert and its passing means nothing.
    /// </summary>
    [Test]
    public async Task Matcher_FlagsTheNullForgivingSpelling()
    {
        const string Shipped = "public IEventBus EventBus { get; internal set; } = null!;";

        await Assert.That(ScanRunner.Collect(NullRule, [("probe.cs", Shipped)]).Count).IsGreaterThan(0)
            .Because("the `= null!` spelling is a real positive control; a miss means the rule is inert.");
    }

    /// <summary>
    ///     The fixed spelling must not be flagged, or the guard cannot be adopted.
    /// </summary>
    [Test]
    public async Task Matcher_AcceptsTheConstructorAssignedSpelling()
    {
        const string Fixed = "public HarborRegistries Registries { get; }";

        await Assert.That(ScanRunner.Collect(NullRule, [("probe.cs", Fixed)]).Count).IsEqualTo(0)
            .Because("a get-only member assigned in the constructor is the fix; flagging it would make the rule unsatisfiable.");
    }

    /// <summary>
    ///     A comment documenting the old spelling is prose, not a violation — and the
    ///     second assertion proves the silence is earned by the comment stripper.
    /// </summary>
    [Test]
    public async Task Matcher_IgnoresTheCommentDocumentingTheOldSpelling()
    {
        const string DocLine = "// Was `= null!` before the registries became constructor arguments (#562).";
        const string Documented = """
            // Was `= null!` before the registries became constructor arguments (#562).
            public HarborRegistries Registries { get; }
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
    ///     The rule's own scope really yields the composition root including
    ///     HarborCompositionContext, so "no `null!`" means "nobody writes it"
    ///     rather than "the walk saw nothing".
    /// </summary>
    [Test]
    public async Task Scanner_SeesHostingSources()
    {
        List<string> discovery = ScanRunner.CheckDiscovery(NullRule);

        await Assert.That(discovery).IsEmpty()
            .Because(
                "src/Harbor.Hosting holds the composition root including HarborCompositionContext. "
                + "An empty scope — or one that stopped seeing that file — means the tree walk broke "
                + "and the rule above would be reporting 'clean' about nothing. "
                + string.Join("; ", discovery));
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
