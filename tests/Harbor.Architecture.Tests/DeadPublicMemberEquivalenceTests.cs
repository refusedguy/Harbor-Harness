// DeadPublicMemberEquivalenceTests.cs — the proof that the rewritten member
// scan says the SAME thing as the scan before the rewrite.
//
// WHY THIS FILE EXISTS
// --------------------
// The rewrite made `DeadPublicMemberRule`'s scan cheaper in two ways:
//
//   1. the types of a file are parsed only in the CONTRACT trees — the only
//      trees whose types are graded; and
//   2. the member list is walked once per FILE instead of once per TYPE.
//
// Both are optimisations, and an optimisation that changes a rule's verdicts
// is not an optimisation: a rule that starts catching something else is a
// rule that moved without anybody deciding that it should. So the scan before
// the rewrite is kept — as `DeadPublicMemberScanMode.Legacy` — and this file
// runs BOTH over the live tree and requires the same answer.
//
// WHAT IS COMPARED, AND WHY IT IS NOT THE WHOLE TREE TWICE
// ----------------------------------------------------------
//   * The MEMBER WALK is compared over EVERY contract-tree file: those are
//     the only files whose members are graded, so they are the only files
//     where the walk can change a verdict. The comparison is complete, not a
//     sample.
//   * The TYPE-PARSE GATE is compared over the contract trees PLUS a live
//     sample of the files outside them. The gate removes the type parse of
//     the 2 441 files the rule reads but never grades, and the test proves
//     two things about it: the work really is skipped (the parse counter and
//     the per-file type count say so), and the verdict does not move.
//
// The legacy scan of the WHOLE tree is not run, and that is deliberate: it
// would put back the whole cost this change removed, in the shard this
// change is about.
//
// WHAT IS NOT VACUOUS
// -------------------
// Every assertion here can fail. The synthetic control at the end plants a
// file with TWO top-level types and a nested type between them — the shape
// a forward-only cursor can get wrong — and requires the exact set of graded
// members. A cursor that lost the first type's members, or picked up the
// nested type's, fails it.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Proves that the rewritten member scan grades the live tree exactly as
///     the scan before the rewrite graded it.
/// </summary>
public sealed class DeadPublicMemberEquivalenceTests
{
    /// <summary>How many files outside the contract trees the gate test grades.</summary>
    /// <remarks>
    ///     The sample is the LARGEST non-contract files: the gate's claim is
    ///     that their types are never graded, and the biggest files are the
    ///     ones with the most types to not-grade. Fifty is enough to prove
    ///     the gate over real files and cheap enough to keep out of the
    ///     shard's wall — the oracle parses every one of them the old way,
    ///     twice.
    /// </remarks>
    private const int OutsideSampleSize = 50;

    /// <summary>
    ///     The member walk finds the same members, in the same files, as the
    ///     walk before the rewrite — over EVERY contract-tree file.
    /// </summary>
    [Test]
    public async Task The_Member_Walk_Finds_The_Same_Members_As_The_Walk_Before_The_Rewrite()
    {
        List<(string Rel, string Source)> contract =
            DeadPublicMemberProbe.ReadSources(RepoPaths.RepoRoot, DeadPublicMemberProbe.ContractTrees);

        await Assert.That(contract.Count).IsGreaterThan(100)
            .Because(
                "the seven contract trees hold the whole published surface of the product; a walk that "
                + "found a handful of files means the tree filter broke and the comparison below proved "
                + "nothing. Files: " + contract.Count);

        DeadPublicMemberReport current = DeadPublicMemberProbe.ScanFiles(
            contract,
            DeadPublicMemberScanMode.Current);

        DeadPublicMemberReport legacy = DeadPublicMemberProbe.ScanFiles(
            contract,
            DeadPublicMemberScanMode.Legacy);

        var scopedDifference = Difference(
            current.Scoped.Select(static s => s.FullTypeName + "." + s.Member + " (" + s.Kind + ")"),
            legacy.Scoped.Select(static s => s.FullTypeName + "." + s.Member + " (" + s.Kind + ")"));

        await Assert.That(scopedDifference).IsEmpty()
            .Because(
                "the graded surface is what the rule promises and what the tolerance table is checked "
                + "against; a member that the cursor walk lost is a member the rule stopped watching, and "
                + "one it picked up that it should not have is a false finding. Members the two walks "
                + "disagree about: " + string.Join(" | ", scopedDifference.Take(20)));

        var findingDifference = Difference(
            current.Findings.Select(static f => f.Key),
            legacy.Findings.Select(static f => f.Key));

        await Assert.That(findingDifference).IsEmpty()
            .Because(
                "the findings are the rule's verdict. A member that the cursor walk lost is a promise "
                + "nobody keeps that the rule no longer reports, and one it invented is a promise the rule "
                + "never made. Findings the two scans disagree about: "
                + string.Join(" | ", findingDifference.Take(20)));
    }

    /// <summary>
    ///     The gate that parses a file's types only in the contract trees
    ///     removes work, not verdicts.
    /// </summary>
    [Test]
    public async Task The_Gate_Skips_The_Type_Parse_And_The_Verdict_Does_Not_Move()
    {
        List<(string Rel, string Source)> all = DeadPublicMemberProbe.ReadSources(
            RepoPaths.RepoRoot,
            DeadPublicMemberProbe.ScanTrees);

        var contract = new List<(string Rel, string Source)>();
        var outside = new List<(string Rel, string Source)>();

        foreach ((string rel, string source) in all)
        {
            if (DeadPublicMemberProbe.IsInContractTree(rel))
            {
                contract.Add((rel, source));
            }
            else
            {
                outside.Add((rel, source));
            }
        }

        // The sample is the LARGEST files outside the contract trees: the
        // ones with the most types for the gate to skip.
        var sample = outside
            .OrderByDescending(static s => s.Source.Length)
            .Take(OutsideSampleSize)
            .ToList();

        await Assert.That(sample.Count).IsEqualTo(OutsideSampleSize)
            .Because(
                "the tree holds far more files outside the contract trees than the sample takes; a short "
                + "sample means the tree changed and the gate is being proved over nothing. Sample: "
                + sample.Count);

        // Non-vacuity: the sample really does hold files with public types,
        // so the gate really does skip work that the legacy scan did.
        var withTypes = 0;
        foreach ((string rel, string source) in sample)
        {
            if (DeadPublicMemberProbe.PublicTypeCount(rel, source, DeadPublicMemberScanMode.Legacy) > 0)
            {
                withTypes++;
            }

            await Assert.That(DeadPublicMemberProbe.PublicTypeCount(rel, source, DeadPublicMemberScanMode.Current))
                .IsEqualTo(0)
                .Because(
                    "the gate's claim is that the types of a file outside the contract trees are never "
                    + "parsed, and a file whose types ARE parsed under the current scan is a gate that "
                    + "does not gate. File: " + rel);
        }

        await Assert.That(withTypes).IsGreaterThan(0)
            .Because(
                "the sample holds no file with a public type, so the gate is being proved over files whose "
                + "types there was nothing to skip — the proof would pass for the wrong reason. Files with "
                + "types under the legacy scan: " + withTypes);

        var sources = new List<(string Rel, string Source)>(contract);
        sources.AddRange(sample);

        DeadPublicMemberReport current = DeadPublicMemberProbe.ScanFiles(
            sources,
            DeadPublicMemberScanMode.Current);

        DeadPublicMemberReport legacy = DeadPublicMemberProbe.ScanFiles(
            sources,
            DeadPublicMemberScanMode.Legacy);

        var scopedDifference = Difference(
            current.Scoped.Select(static s => s.FullTypeName + "." + s.Member + " (" + s.Kind + ")"),
            legacy.Scoped.Select(static s => s.FullTypeName + "." + s.Member + " (" + s.Kind + ")"));

        await Assert.That(scopedDifference).IsEmpty()
            .Because(
                "the types of a file outside the contract trees are never graded, so parsing them must not "
                + "move the graded surface. Members the two scans disagree about: "
                + string.Join(" | ", scopedDifference.Take(20)));

        var findingDifference = Difference(
            current.Findings.Select(static f => f.Key),
            legacy.Findings.Select(static f => f.Key));

        await Assert.That(findingDifference).IsEmpty()
            .Because(
                "the findings are the rule's verdict, and the gate removes the type parse of "
                + outside.Count + " files the rule reads. Findings the two scans disagree about: "
                + string.Join(" | ", findingDifference.Take(20)));
    }

    /// <summary>
    ///     The control: a file with TWO top-level types and a nested type
    ///     between them loses no member, and the two scans agree on it.
    /// </summary>
    /// <remarks>
    ///     This is the shape a forward-only member cursor can get wrong: it
    ///     must not lose the FIRST type's members when it reaches the second
    ///     type, and it must not pick up the nested type's members, which are
    ///     a level deeper than the contract's surface.
    /// </remarks>
    [Test]
    public async Task A_File_With_Several_Types_Loses_No_Member()
    {
        const string Synthetic =
            """
            namespace Harbor.Abstractions.Synthetic;

            public sealed class SyntheticFirst
            {
                public string FirstDead { get; set; } = string.Empty;

                public sealed class Nested
                {
                    public string NestedDead { get; set; } = string.Empty;
                }

                public void FirstAlsoDead()
                {
                }
            }

            public sealed class SyntheticSecond
            {
                public string SecondDead { get; set; } = string.Empty;
            }
            """;

        var sources = new List<(string Rel, string Source)>
        {
            ("src/Harbor.Abstractions/Synthetic/SyntheticSeveralTypes.cs", Synthetic),
        };

        foreach (DeadPublicMemberScanMode mode in new[]
                 {
                     DeadPublicMemberScanMode.Current,
                     DeadPublicMemberScanMode.Legacy,
                 })
        {
            DeadPublicMemberReport report = DeadPublicMemberProbe.ScanFiles(sources, mode);

            var graded = report.Scoped
                .Select(static s => s.FullTypeName + "." + s.Member)
                .OrderBy(static s => s, StringComparer.Ordinal)
                .ToArray();

            var expected = new[]
            {
                "Harbor.Abstractions.Synthetic.SyntheticFirst.FirstAlsoDead",
                "Harbor.Abstractions.Synthetic.SyntheticFirst.FirstDead",
                "Harbor.Abstractions.Synthetic.SyntheticSecond.SecondDead",
            };

            await Assert.That(graded).IsEquivalentTo(expected)
                .Because(
                    "the member walk must find the first type's members, the second type's members, and "
                    + "NOT the nested type's members — the nested type is one level deeper than the "
                    + "contract's surface. Graded under " + mode + ": "
                    + string.Join(" | ", graded));

            var reported = report.Findings
                .Select(static f => f.Key)
                .OrderBy(static s => s, StringComparer.Ordinal)
                .ToArray();

            await Assert.That(reported).IsEquivalentTo(expected)
                .Because(
                    "nothing references these members, so all three are dead — and a walk that lost the "
                    + "first type's members would report fewer, and a walk that picked up the nested "
                    + "type's would report more. Reported under " + mode + ": "
                    + string.Join(" | ", reported));
        }
    }

    /// <summary>
    ///     The entries of two sets that the first one has and the second one
    ///     does not, and the other way round.
    /// </summary>
    /// <param name="left">The first set.</param>
    /// <param name="right">The second set.</param>
    private static List<string> Difference(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);

        var difference = new List<string>();
        difference.AddRange(leftSet.Except(rightSet, StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal));
        difference.AddRange(rightSet.Except(leftSet, StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal));

        return difference;
    }
}
