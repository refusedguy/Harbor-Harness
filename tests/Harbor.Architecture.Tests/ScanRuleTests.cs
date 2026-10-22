// ScanRuleTests.cs — the instrument proves itself on synthetic source.
//
// A shared matcher that quietly under-matches returns plausible zeros (#591:
// TrimUnsafe's first run reported 2 instead of 3; ProviderPayload's carrier
// matcher matched only its object[] branch). Every behaviour below is asserted
// without touching the disk, except the discovery floor — which is the one
// behaviour that is ABOUT the disk.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>Self-tests for <see cref="ScanRunner" /> over synthetic source.</summary>
public sealed class ScanRuleTests
{
    private static ScanRule ProbeRule(
        ScanBaseline[]? baseline = null,
        Func<string, string, IEnumerable<ScanHit>>? custom = null,
        int minHits = 1) =>
        new()
        {
            Id = "PROBE",
            Trees = ["src"],
            Forbidden =
            [
                new ScanForbidden("PROBE-NO-LOAD", new Regex(@"\bAssembly\s*\.\s*Load\s*\(", RegexOptions.Compiled), "do not load assemblies at run time."),
            ],
            Baseline = baseline ?? [],
            Controls =
            [
                new ScanControl("hit.cs", "var a = Assembly.Load(bytes);", "PROBE-NO-LOAD"),
                new ScanControl("quiet.cs", "var name = element.GetProperty(\"models\");", null),
                new ScanControl("prose.cs", "/// Loads via Assembly.Load(byte[]).", null),
            ],
            MinHits = minHits,
            CustomParse = custom,
        };

    /// <summary>The line scan reports the offender and nothing else.</summary>
    [Test]
    public async Task Collect_ReportsTheOffenderOnly()
    {
        List<ScanHit> hits = ScanRunner.Collect(
            ProbeRule(),
            [("hit.cs", "var a = Assembly.Load(bytes);"), ("quiet.cs", "var x = 1;")]);

        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].Report()).IsEqualTo("hit.cs:1  [PROBE-NO-LOAD]  Assembly.Load(");
    }

    /// <summary>Comment prose about the banned shape is not a hit.</summary>
    [Test]
    public async Task Collect_StripsCommentsBeforeMatching()
    {
        List<ScanHit> hits = ScanRunner.Collect(
            ProbeRule(), [("prose.cs", "/// Loads via Assembly.Load(byte[]) — see docs.")]);

        await Assert.That(hits).IsEmpty();
    }

    /// <summary>Exact and prefix baseline rows excuse their hits.</summary>
    [Test]
    public async Task Evaluate_SubtractsExactAndPrefixBaseline()
    {
        var rule = ProbeRule(
        [
            new ScanBaseline(
                "PROBE-NO-LOAD src/Harbor.Plugins.X/A.cs",
                IsPrefix: false,
                Reason: "an exact row excuses this one file and no other file in the project."),
            new ScanBaseline(
                "src/Allowed/",
                IsPrefix: true,
                Reason: "a prefix row excuses every hit under it, which is why prefixes are rare."),
        ]);

        List<string> failures = ScanRunner.EvaluateOver(
            [
                ("src/Harbor.Plugins.X/A.cs", "var a = Assembly.Load(b);"),
                ("src/Allowed/B.cs", "var a = Assembly.Load(b);"),
                ("src/Other/C.cs", "var a = Assembly.Load(b);"),
            ],
            rule);

        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0].StartsWith("src/Other/C.cs", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Controls pass when the matcher honours every planted verdict.</summary>
    [Test]
    public async Task CheckControls_PassesOnHonestMatcher()
    {
        await Assert.That(ScanRunner.CheckControls(ProbeRule())).IsEmpty();
    }

    /// <summary>A stale exact row is reported; a live one is not.</summary>
    [Test]
    public async Task StaleBaselineKeys_ReportsOnlyTheDeadRow()
    {
        var rule = ProbeRule(
        [
            new ScanBaseline(
                "PROBE-NO-LOAD live.cs",
                IsPrefix: false,
                Reason: "this row still has a violation behind it so it must not be reported stale."),
            new ScanBaseline(
                "PROBE-NO-LOAD gone.cs",
                IsPrefix: false,
                Reason: "this row has no violation behind it so it must be reported stale here."),
        ]);

        List<string> stale = ScanRunner.StaleBaselineKeys(
            rule, [("live.cs", "var a = Assembly.Load(b);")]);

        await Assert.That(stale).IsEquivalentTo(new[] { "PROBE-NO-LOAD gone.cs" });
    }

    /// <summary>The Func-overload replaces the line scan when set.</summary>
    [Test]
    public async Task CustomParse_ReplacesTheLineScan()
    {
        var rule = ProbeRule(custom: (file, raw) =>
        {
            if (raw.Contains("MAGIC", StringComparison.Ordinal))
            {
                return (IEnumerable<ScanHit>)[new ScanHit("PROBE-NO-LOAD", file, 7, "MAGIC")];
            }

            return [];
        });

        List<ScanHit> hits = ScanRunner.Collect(
            rule,
            [("a.cs", "MAGIC // Assembly.Load(b)"), ("b.cs", "var a = Assembly.Load(b);")]);

        await Assert.That(hits.Select(h => h.File)).IsEquivalentTo(new[] { "a.cs" });
        await Assert.That(hits[0].Line).IsEqualTo(7);
    }

    /// <summary>A short reason fails the reasons check; a real one passes.</summary>
    [Test]
    public async Task CheckReasons_FlagsAReasonThatStatesNothing()
    {
        var bad = ProbeRule(
        [
            new ScanBaseline("PROBE-NO-LOAD x.cs", IsPrefix: false, Reason: "later"),
        ]);

        await Assert.That(ScanRunner.CheckReasons(bad)).IsNotEmpty();

        var good = ProbeRule(
        [
            new ScanBaseline(
                "PROBE-NO-LOAD x.cs",
                IsPrefix: false,
                Reason: "this file loads the plugin assembly because loading code at run time is its product."),
        ]);

        await Assert.That(ScanRunner.CheckReasons(good)).IsEmpty();
    }

    /// <summary>The discovery floor fails when set above reality.</summary>
    [Test]
    public async Task CheckDiscovery_FloorFailsAboveReality()
    {
        var impossible = ProbeRule(minHits: int.MaxValue);

        await Assert.That(ScanRunner.CheckDiscovery(impossible)).IsNotEmpty();
        await Assert.That(ScanRunner.CheckDiscovery(ProbeRule())).IsEmpty();
    }

    /// <summary>
    ///     The snapshot path says what the raw path says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The text rules used to read every file of the product tree and
    ///         strip its comments themselves, once per rule per test. They now
    ///         read the shared snapshot's stripped view instead, and the meter
    ///         in <see cref="StripCommentsMeter" /> counted 950 251 strip calls
    ///         over 651 571 350 characters in one shard run — the tree read and
    ///         stripped about twenty-six times over.
    ///     </para>
    ///     <para>
    ///         A cache that changes what a rule sees is not a cache, it is a
    ///         different rule: a rule that reads different bytes reports
    ///         different findings, and the test suite would still be green
    ///         because a rule that finds nothing is green. So the two paths are
    ///         compared over the LIVE tree, with the real rule's own patterns
    ///         and the real rule's own scope — the old path beside the new
    ///         one, and the hits compared one by one.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Snapshot_Path_Says_What_The_Raw_Path_Says()
    {
        ScanRule rule = ReflectionConventionRule.Rule;

        IReadOnlyList<string> scope = ScanRunner.ScopeFiles(rule);

        await Assert.That(scope.Count).IsGreaterThan(100).Because(
            "the comparison below is over the live product tree, and a scope of a handful of files "
            + "would compare two answers about a tree nobody grades. The rule's scope is the scope "
            + "the rule itself walks.");

        // The OLD path: read the file, strip it, split it, match it.
        List<ScanHit> raw = ScanRunner.Collect(rule, ScanRunner.ReadSources(scope));

        // The NEW path: the shared snapshot's stripped view, and its line view.
        List<ScanHit> cached = ScanRunner.CollectStripped(rule, ScanRunner.ReadStrippedSources(rule, scope));

        await Assert.That(cached.Count).IsEqualTo(raw.Count).Because(
            "the snapshot's stripped view must be the text the rule used to strip itself, or the "
            + "rule is a different rule now. The old path found "
            + raw.Count + " hit(s); the cached path found " + cached.Count + ".");

        await Assert.That(string.Join(" | ", cached.Select(static h => h.Report())))
            .IsEqualTo(string.Join(" | ", raw.Select(static h => h.Report())))
            .Because(
                "the hits have to be the SAME hits — the same file, the same line, the same text — "
                + "because a rule that finds a different site is a rule whose verdict moved, and a "
                + "moved verdict is invisible in a green suite.");

        // And the VERDICT, which is the baseline subtracted from the hits.
        await Assert.That(string.Join(" | ", ScanRunner.EvaluateOverStripped(
                       ScanRunner.ReadStrippedSources(rule, scope), rule)))
            .IsEqualTo(string.Join(" | ", ScanRunner.EvaluateOver(ScanRunner.ReadSources(scope), rule)))
            .Because(
                "the two paths have to agree on what the rule REPORTS, not only on what it matched: "
                + "the baseline is subtracted from the hits, and a path that lost a hit could still "
                + "produce the same report by accident.");
    }
}
