// PanelKeyRouteReachabilityRule's equivalence — the proof that the cheap
// type→file map finds the SAME declarations the whole-file match collection
// found.
//
// WHY THIS FILE EXISTS
// --------------------
// The rule's scan spent 74 072 ms of its 74 253 ms wall on ONE thing: the
// type→file map, built by enumerating a whole-file match collection per file.
// The pattern's lazy type part can grow ACROSS the whole file — its character
// class holds whitespace and word characters, so nothing stops it at a line
// boundary — and every match attempt therefore walked the rest of the file.
//
// The map is now built ONE MATCH PER LINE: the same pattern over the same
// bytes, with the line as the unit the pattern is written for (the pattern is
// anchored per line by design — the rule's own header says the multiline flag
// is load-bearing). MEASURED over the product tree: 225 ms against 26 537 ms,
// 118 times less.
//
// An optimisation that changes a rule's verdicts is not an optimisation, so
// the map before the rewrite is kept — as `TypeScanStrategy.WholeText` — and
// this file runs BOTH over the live tree and requires the same answer.
//
// WHAT IS COMPARED
// ----------------
//   * the TYPE MAP, over EVERY file of the product tree. The map is what
//     resolves a call qualified by a type name (`Type.Member(`), it is GLOBAL,
//     and a difference anywhere in it can move a verdict — so the comparison is
//     complete, not a sample.
//   * the SCAN's REPORT — the hand-offs, the orphans and their chains — over the
//     same tree. The map is an input to the verdict, and the verdict is what
//     the rule is for.
//   * a SYNTHETIC case where the two strategies CAN differ, so the proof is
//     shown to discriminate rather than to answer a constant.
//
// WHY THE WHOLE TREE, AND WHAT IT COSTS
// -------------------------------------
// The oracle over the whole tree is 26 537 ms — the cost this change removed
// from the rule — and it is paid ONCE, by a class that runs ALONE at the end
// of the assembly (`[NotInParallel]`), so it neither sits on the rule's
// critical path nor distorts the duration of any other rule in this project.
//
// The whole tree is the scope ON PURPOSE. The two strategies can differ in
// exactly one shape: a declaration whose type part crosses a line boundary.
// A per-line scan cannot see such a declaration, and a whole-file scan can.
// So the whole-tree comparison is the guard over the very risk this change
// introduces: a declaration split across lines, appearing anywhere in the
// product tree, is caught here the day it is written. A sampled comparison
// would leave that unguarded.
//
// WHAT IS NOT VACUOUS
// -------------------
// The synthetic control plants the one shape where the two strategies differ —
// a declaration whose type part crosses a line boundary — and requires that the
// strategies DO differ on it. A proof that always answered "equal" would look
// the same as a proof that can fail, and this one has to be the second kind.

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Proves that the type→file map the rule builds is the map the whole-file
///     match collection builds, and that the scan's verdict does not move.
/// </summary>
[NotInParallel]
public sealed class PanelKeyRouteEquivalenceTests
{
    /// <summary>
    ///     The oracle's map: the whole-file match collection, built ONCE for
    ///     the assembly and shared by the two tests below.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, List<string>>> OracleTypeOwner = new(
        () => PanelKeyRouteProbe.BuildTypeOwner(
            PanelKeyRouteProbe.ReadProductTrees(),
            TypeScanStrategy.WholeText));

    /// <summary>
    ///     The map the rule builds — one match per line — is the map the
    ///     whole-file match collection builds, over EVERY file of the product
    ///     tree.
    /// </summary>
    [Test]
    public async Task The_Type_Map_Is_The_Same_Whichever_Way_It_Is_Built()
    {
        IReadOnlyList<SourceTreeFile> sources = PanelKeyRouteProbe.ReadProductTrees();

        var watch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, List<string>> oracle = OracleTypeOwner.Value;
        long oracleMs = watch.ElapsedMilliseconds;

        watch.Restart();
        Dictionary<string, List<string>> candidate =
            PanelKeyRouteProbe.BuildTypeOwner(sources, TypeScanStrategy.PerLine);
        long candidateMs = watch.ElapsedMilliseconds;

        List<string> differences = PanelKeyRouteProbe.CompareTypeMaps(oracle, candidate);

        Console.WriteLine(
            $"[arch-cost] panel-key-route type-map oracle-ms={oracleMs} "
            + $"per-line-ms={candidateMs} types={oracle.Count}");

        Console.WriteLine(
            $"[arch-count] panel-key-route type-map differences={differences.Count}");

        await Assert.That(differences).IsEmpty().Because(
            "the map is what resolves a call qualified by a type name, so a difference in it is "
            + "a difference in a verdict. The two strategies read the same bytes with the same "
            + "pattern; they can differ only where a declaration's type part crosses a line "
            + "boundary, and the product tree has none. Differences: "
            + (differences.Count == 0 ? "(none)" : string.Join(" | ", differences.Take(5))));
    }

    /// <summary>
    ///     The scan reports the same hand-offs and the same orphans whichever
    ///     map it is given.
    /// </summary>
    [Test]
    public async Task The_Scan_Reports_The_Same_Hand_Offs_And_The_Same_Orphans()
    {
        IReadOnlyList<SourceTreeFile> sources = PanelKeyRouteProbe.ReadProductTrees();

        KeyRouteReport withOracle = PanelKeyRouteProbe.ScanWithTypeOwner(
            sources,
            OracleTypeOwner.Value);

        KeyRouteReport withCandidate = PanelKeyRouteProbe.ScanWithTypeOwner(
            sources,
            PanelKeyRouteProbe.BuildTypeOwner(sources, TypeScanStrategy.PerLine));

        await Assert.That(withCandidate.HandOffs).IsEquivalentTo(withOracle.HandOffs).Because(
            "the hand-offs are the rule's subject: the same seams, at the same lines, with the "
            + "same receivers and the same performers.");

        await Assert.That(withCandidate.Orphans).IsEquivalentTo(withOracle.Orphans).Because(
            "the orphans are the rule's verdict: the routers no product code can reach, with the "
            + "same chains. A map that moved an orphan out of the set would be a rule that "
            "stopped seeing a defect.");
    }

    /// <summary>
    ///     The control: the shape where the two strategies differ, so the
    ///     comparison above is shown to be able to fail.
    /// </summary>
    [Test]
    public async Task A_Declaration_Split_Across_Lines_Is_The_Shape_The_Proof_Catches()
    {
        // The declaration's type part crosses the line boundary: the whole-file
        // pattern's lazy type part grows past the newline and finds "Router",
        // while a per-line pattern cannot leave the line it started on.
        const string Split = """
            namespace Synthetic.Control;

            public sealed class Synthetic
                Router(
            """;

        SourceTreeFile file = SourceTreeFile.FromStrippedText("SyntheticSplit.cs", Split);

        Dictionary<string, List<string>> oracle =
            PanelKeyRouteProbe.BuildTypeOwner([file], TypeScanStrategy.WholeText);

        Dictionary<string, List<string>> candidate =
            PanelKeyRouteProbe.BuildTypeOwner([file], TypeScanStrategy.PerLine);

        await Assert.That(oracle.ContainsKey("Router")).IsTrue().Because(
            "the whole-file pattern's lazy type part grows past the newline — its character class "
            + "holds whitespace and word characters — so the declaration split across the lines "
            + "is found. If this stops being true, the whole-file strategy is no longer the "
            + "oracle and the comparison above proves nothing.");

        await Assert.That(candidate.ContainsKey("Router")).IsFalse().Because(
            "a per-line pattern cannot leave the line it started on, so the same declaration is "
            + "NOT found. THIS IS THE LIMIT OF THE CHANGE, and the reason the whole-tree "
            + "comparison is a live gate: a declaration split across lines, written anywhere in "
            + "the product tree, would move the map, and the gate above is what says so.");
    }
}
