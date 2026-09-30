// ScanVisibilityRule.cs — a MEASUREMENT, not a fix: which top-level repository
// trees the shared scan helper can read, and which it silently returns nothing for.
//
// THE DEFECT
// ----------
// #877. `SourceScan.EnumerateCsFiles(params string[] trees)` takes a tree name,
// walks it, and returns the `*.cs` files it finds — filtered through
// `SourceScan.IsBuildOutput`, which rejects any path containing `/tests/`,
// `/contrib/`, `/.worktrees/`, `/obj/` or `/bin/`.
//
// For `tests` and `contrib` that filter eats every single file, so the method
// returns an EMPTY LIST BY CONSTRUCTION. And an empty list is exactly what
// "this tree contains no `.cs` files" looks like, and exactly what "the walk
// broke" looks like. Three different states, one return value.
//
// This is not hypothetical. An agent working #857 built a gate on
// `EnumerateCsFiles("tests")` to assert that panel input is dead, and the gate
// reported a healthy zero on a red defect — because the measurement could not see
// its own subject. It worked around it locally and recorded the shared behaviour
// as "pinned, not changed, 96 guards depend on it". That reasoning is WRONG, and
// this file is the evidence; see THE VERDICT below.
//
// Note the method's own XML doc, which is the sharpest statement of the problem:
//
//     Every `*.cs` file under the named repository-relative trees
//
// That is false for two of the five top-level trees in this repository. It
// promises the caller's tree and returns something else. A measurement whose
// documentation overstates its own reach is how a zero gets read as a clean bill
// of health.
//
// THE VERDICT: DECLARE, DO NOT FIX
// -------------------------------
// The obvious move is to delete the `/tests/` and `/contrib/` clauses from
// `IsBuildOutput`. Measured, that would be the wrong move, and the reason is worth
// recording because the "96 guards depend on it" argument inverts the truth:
//
//   1. NO SHIPPED GUARD IS AFFECTED BY EITHER CLAUSE. All 13 guard files that
//      route through `SourceScan` root their walk at `src/` and/or `apps/`. There
//      is no directory named `tests`, `contrib` or `.worktrees` ANYWHERE UNDER
//      `src/` or `apps/`, so for those walks the clauses can never fire. The
//      feared blast radius — "96 guards start seeing `tests/` and some go red" —
//      is measurably zero. It was never a real cost.
//   2. BUT DELETING THE CLAUSE IS STILL THE WRONG FIX, for a reason that has
//      nothing to do with cost. It would make `EnumerateCsFiles("tests")` return
//      781 files, and those files include the guard files themselves. Every rule
//      built on that helper is written for product code; pointing one at `tests/`
//      makes it police its own fixtures, which is precisely what
//      `SourceScan.IsBuildOutput`'s header says must not happen ("every planted
//      positive control would fail every other gate"). Deleting the clause
//      converts a deliberate, documented exclusion into an accident of a shared
//      constant.
//   3. THE NINE OTHER FILTERS ARE RIGHT AND SAY SO. Nine guard files carry a
//      PRIVATE `IsBuildOutput` that rejects only `obj/` and `bin/`. Three of them
//      carry a comment giving the reason: "no worktree filter here: the walk
//      starts at <root>/src and <root>/apps, so a nested checkout under
//      <root>/.worktrees is out of reach anyway — and matching on it would be
//      actively wrong, because a checkout that ITSELF lives in .worktrees would
//      filter itself out and the scan would find nothing."
//
//      That is not drift. That is a filter authored for a different root, with
//      the difference written down. This repository does not have one filter to
//      fix; it has two correct ones for two different questions, and the
//      disagreement is reasoned rather than accidental.
//
// So the exclusion stays. What is missing is not the filter — it is that the
// boundary is UNDECLARED at the point where the tree is chosen, and that an empty
// result cannot say which side of it the caller is on. This file states the
// property, measures it, and pins the measurement. Changing the filter's
// behaviour is deliberately not attempted here.
//
// THE SECOND "CANNOT SEE" IS A DIFFERENT FILE
// -------------------------------------------
// The other blind spot in this class is not a path filter at all: it is a MISSING
// PROJECT FILE. `src/Harbor.Storage.Shared` and `src/Harbor.Providers.Shared` hold
// compiled product code and have no `.csproj`, so nothing ever yields their names.
// See `ProjectKeyedScanRule.cs`. The two are kept apart on purpose: one is a path
// that is walked and then rejected, the other is a key that is never generated.
// A fix for either does not fix the other, and a header that conflated them
// would hide exactly the distinction a future reader needs.
//
// RELATION TO #863
// ----------------
// PR #863 (issue #843) hit the `/contrib/` half of this from the other direction
// and got the FORM right: it could not use `SourceScan.EnumerateCsFiles("contrib")`
// for the same reason this file exists, so it walks with a skip set that takes
// `contrib` back out, and it ASSERTS THE FAR SIDE WAS READ so that a silent miss
// cannot report itself as a clean tree. That is the precedent and this file
// reuses its shape — a non-vacuity assertion that a side produced files, and a
// boundary keyed on something that cannot be fooled by a prefix.
//
// It does not duplicate its decision. #863 measures NAMES crossing into
// `contrib/`; this file measures WHICH TREES THE SHARED HELPER CANNOT READ. The
// first-segment boundary keying #863 needs (because `ProjectOf` returns "apps"
// for `contrib/apps/...`) is not needed here: the trees under measurement are
// top-level, so there is no nested-prefix ambiguity to defend against.
//
// NON-VACUITY
// -----------
//   1. TheProductSideIsReadable — a tree the helper DOES read came back non-empty.
//      Without it, "every tree is invisible" and "the walk is broken" are the same
//      report, and this rule would pass on a helper that returned `[]` for
//      everything.
//   2. InvisibleTrees_AreInvisibleBecauseTheyWereFiltered — for every tree this
//      rule calls invisible, a NEUTRAL walk (not `IsBuildOutput` — that is the
//      thing under test, and reusing it would make the measurement circular) found
//      real `.cs` files there. So "invisible" means "the filter ate it", never
//      "there was nothing to eat".
//   3. TheFilterAnswersTheDeclaredQuestion — a positive control over
//      `SourceScan.IsBuildOutput` itself, on a fixed table of synthetic paths, so
//      the baseline below is anchored to a STATED contract rather than to whatever
//      the filter happens to do on the day it is pinned. The `.worktrees` clause
//      is pinned here rather than by the walk, because a sibling worktree is not
//      part of the repository and must not be counted as one.
//
// The first version of this file shipped with an EMPTY baseline on purpose: the
// red run is the measurement, and it is what puts a number in front of anyone.
// (There is no local dotnet in the authoring environment, so the red run's log is
// the only execution of this matcher there has ever been — the baseline is
// transcribed from it, not recomputed.)

namespace Harbor.Architecture.Tests;

/// <summary>One top-level repository tree, and what the shared walk did with it.</summary>
/// <param name="Name">Directory name, relative to the repository root.</param>
/// <param name="CsFilesOnDisk">
///     How many <c>*.cs</c> files a neutral walk found here. This count deliberately does
///     NOT use <c>SourceScan.IsBuildOutput</c> — that is the subject under test, and a
///     count taken with it could not detect it eating anything.
/// </param>
/// <param name="FilesReturned">How many files <c>SourceScan.EnumerateCsFiles</c> returned.</param>
internal sealed record TreeVisibility(string Name, int CsFilesOnDisk, int FilesReturned)
{
    /// <summary>
    ///     Files are demonstrably here, and the shared walk returned none of them. The
    ///     conjunction is the whole point: <c>FilesReturned == 0</c> alone would also be
    ///     true of a tree with no <c>.cs</c> files and of a walk that found nothing at all.
    /// </summary>
    internal bool IsInvisibleToTheHelper => CsFilesOnDisk > 0 && FilesReturned == 0;
}

/// <summary>Walks the repository the way this rule's subject does NOT.</summary>
internal static class ScanVisibilityProbe
{
    /// <summary>
    ///     Directory names this walk never descends into.
    /// </summary>
    /// <remarks>
    ///     Note what is NOT in this set. <c>tests</c> and <c>contrib</c> are walked, on
    ///     purpose — they are the trees whose invisibility is the measurement. <c>.git</c>
    ///     and <c>.worktrees</c> are skipped as hygiene rather than as policy: a sibling
    ///     worktree is a second copy of the repository nested inside this one, and counting
    ///     it would report a number about a tree that is not part of the subject. Their
    ///     invisibility is pinned by the positive control instead, which is the only place
    ///     it can be pinned honestly — a CI checkout has no sibling worktree, so a walk can
    ///     never observe one.
    /// </remarks>
    internal static readonly string[] NeverWalked = [".git", ".worktrees", "bin", "obj", "node_modules"];

    /// <summary>
    ///     Top-level directory names that are not trees of this repository, and so are not
    ///     asked about.
    /// </summary>
    /// <remarks>
    ///     This is a cost decision with a stated reason, not a silent skip. <c>.git</c> is a
    ///     database, not source: in a full-history checkout it holds hundreds of thousands of
    ///     files, and asking the shared helper to walk it would add minutes to a gate that runs
    ///     on every CI build for an answer that can only be "no <c>.cs</c> files". The
    ///     housekeeping names are the same set <see cref="NeverWalked" /> prunes, kept as a
    ///     separate declaration because the two roles differ — one decides what to descend
    ///     into, the other decides what to ask a question about.
    /// </remarks>
    internal static readonly string[] NotRepositoryTrees =
        [".git", ".worktrees", "bin", "obj", "node_modules"];

    /// <summary>Every <c>*.cs</c> file under <paramref name="root" />, on a neutral walk.</summary>
    /// <remarks>
    ///     Prunes rather than filters: a full <c>SearchOption.AllDirectories</c> enumeration
    ///     descends into <c>.git/</c> on every run. An unreadable directory is skipped rather
    ///     than thrown, because a measurement must not fail the build for a permission it did
    ///     not ask about — and because a directory this rule cannot read is a directory whose
    ///     file count it has no standing to assert anything about.
    /// </remarks>
    internal static IReadOnlyList<string> CsFiles(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current, "*.cs");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            found.AddRange(files);

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string directory in directories)
            {
                if (NeverWalked.Contains(Path.GetFileName(directory), StringComparer.Ordinal))
                {
                    continue;
                }

                pending.Push(directory);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}

/// <summary>
///     Pins which top-level repository trees <see cref="SourceScan.EnumerateCsFiles" /> can
///     read. The blind spot is the filter, not the walk — see the file header.
/// </summary>
public sealed class ScanVisibilityRule
{
    private static readonly Lazy<IReadOnlyList<TreeVisibility>> Report = new(Measure);

    /// <summary>
    ///     The trees the shared helper cannot read. EMPTY ON PURPOSE: the first CI run of
    ///     this file is the measurement, and it is what turns a set of names into a fact.
    /// </summary>
    /// <remarks>
    ///     Pinned as a set of NAMES, never as a count. A count is satisfiable by a tree
    ///     becoming readable on one side while another becomes invisible on the other: the
    ///     number holds, the hole changes shape, and the diff that did it reads as a wash.
    ///     Names make that impossible — a tree that becomes visible is a name somebody has to
    ///     delete from this array on purpose, which is the edit that deserves review.
    /// </remarks>
    private static readonly string[] MeasuredInvisibleTrees = [];

    /// <summary>How the shared helper answers, on a fixed table of paths.</summary>
    /// <remarks>
    ///     The declared contract, stated as data so a reader does not have to infer it from
    ///     the implementation. The two product paths must be accepted, or nothing is ever read
    ///     and every measurement above is vacuous. The five rejected paths are rejected for four
    ///     different reasons — <c>obj/</c> and <c>bin/</c> are build output, <c>contrib/</c> is
    ///     out of CI by owner decision, <c>tests/</c> holds the gates' own fixtures, and
    ///     <c>.worktrees/</c> is a sibling checkout — and the filter treats all four the same
    ///     way. That collapse is the simplification this rule exists to make visible, so it is
    ///     pinned as data rather than left to be re-derived from the implementation.
    /// </remarks>
    private static readonly (string Path, bool Rejected)[] DeclaredFilterContract =
    [
        ("src/Harbor.Abstractions/Result.cs", false),
        ("apps/Harbor.App.Cli/Program.cs", false),
        ("src/Harbor.Abstractions/obj/Debug/net10.0/Generated.cs", true),
        ("src/Harbor.Abstractions/bin/Release/net10.0/Build.cs", true),
        ("tests/Harbor.Architecture.Tests/ScanVisibilityRule.cs", true),
        ("contrib/apps/Harbor.App.Wpf/MainWindow.xaml.cs", true),
        ("src/Harbor.Abstractions/.worktrees/wt-1/Other.cs", true),
    ];

    private static IReadOnlyList<TreeVisibility> Measure()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var report = new List<TreeVisibility>();
        foreach (string directory in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (ScanVisibilityProbe.NotRepositoryTrees.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            int onDisk = ScanVisibilityProbe.CsFiles(directory).Count;
            int returned = SourceScan.EnumerateCsFiles(name).Count;
            report.Add(new TreeVisibility(name, onDisk, returned));
        }

        report.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return report;
    }

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     The trees the shared helper cannot read must equal
    ///     <see cref="MeasuredInvisibleTrees" />.
    /// </summary>
    /// <remarks>
    ///     When this fails, the message IS the measurement. That is why the baseline above
    ///     shipped empty: the red run is what put names and counts in front of anyone, which
    ///     no amount of reading the filter by eye had. Every tree is reported with both
    ///     counts, not only the failing ones, because "how many files are behind that zero"
    ///     is the number that decides whether the blind spot matters.
    /// </remarks>
    [Test]
    public async Task InvisibleTrees_MatchTheMeasuredBaseline()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule measures which trees the shared helper cannot read, and without a "
                   + "checkout there are no trees at all — an empty report would satisfy the "
                   + "baseline below for the wrong reason");

        var report = Report.Value;
        var live = report.Where(t => t.IsInvisibleToTheHelper).Select(t => t.Name).ToArray();

        await Assert.That(string.Join(" | ", live))
            .IsEqualTo(string.Join(" | ", MeasuredInvisibleTrees))
            .Because(
                "SourceScan.EnumerateCsFiles takes a tree name, walks it, and returns nothing for "
                + "any tree whose paths IsBuildOutput rejects. tests/ and contrib/ are real, "
                + "populated trees that this helper cannot read, and an empty result is "
                + "indistinguishable from 'this tree has no .cs files' and from 'the walk broke'. "
                + "That is the defect class of #877: a measurement that cannot see its subject "
                + "reports a healthy zero. An agent's #857 gate called EnumerateCsFiles(\"tests\") "
                + "and read the empty list as a passing result on a red defect. "
                + "Live invisible trees (" + live.Length + "): " + Describe(report)
                + " | baseline held " + MeasuredInvisibleTrees.Length + " name(s): "
                + (MeasuredInvisibleTrees.Length == 0 ? "(empty)" : string.Join(" | ", MeasuredInvisibleTrees)));
    }

    // =====================================================================
    // 2. Non-vacuity. The load-bearing ones.
    // =====================================================================

    /// <summary>
    ///     A tree the helper DOES read came back with files. Without this, "every tree is
    ///     invisible" and "the walk is broken" are the same report.
    /// </summary>
    [Test]
    public async Task TheProductSideIsReadable()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the product side is half the measurement, and without a checkout it reads "
                   + "nothing and every tree would look invisible");

        int readable = Report.Value.Count(t => t.FilesReturned > 0);
        await Assert.That(readable).IsGreaterThan(0)
            .Because(
                "if NO tree came back with files, then this rule's 'invisible' set would be "
                + "describing a broken walk rather than a filter, and the ratchet above would be "
                + "holding a baseline against a helper that reads nothing at all. A measurement "
                + "that cannot distinguish 'the filter ate this tree' from 'the walk is broken' is "
                + "the same defect this file is about. Every top-level tree, with both counts: "
                + Describe(Report.Value));

        await Assert.That(SourceScan.EnumerateProductCsFiles().Count).IsGreaterThan(0)
            .Because(
                "the product trees are the ones every gate actually depends on, so a zero here "
                + "would mean the shared helper is not merely blind to tests/ and contrib/ but "
                + "blind to everything — and 13 shipped guards route through it");
    }

    /// <summary>
    ///     Every tree this rule calls invisible really does hold <c>.cs</c> files, counted by
    ///     a walk that does not use the filter under test.
    /// </summary>
    [Test]
    public async Task InvisibleTrees_AreInvisibleBecauseTheyWereFiltered()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a checkout there is nothing to have been filtered");

        var invisible = Report.Value.Where(t => t.IsInvisibleToTheHelper).ToArray();
        var empty = invisible.Where(t => t.CsFilesOnDisk == 0).Select(t => t.Name).ToArray();

        await Assert.That(string.Join(" | ", empty))
            .IsEqualTo(string.Empty)
            .Because(
                "a tree cannot be invisible-because-filtered and invisible-because-empty at the "
                + "same time. The first is this file's subject; the second means the neutral walk "
                + "and the shared walk disagree about whether any .cs file exists there, which is "
                + "a different defect and would make every count above meaningless. Named here: "
                + (empty.Length == 0 ? "(none)" : string.Join(" | ", empty)));

        await Assert.That(invisible.Length).IsGreaterThan(0)
            .Because(
                "if nothing is invisible, this file has stopped measuring anything. Either the "
                + "helper grew a way to read tests/ and contrib/ — which would be a behaviour "
                + "change somebody has to review on purpose, not a side effect — or the blind "
                + "spot moved somewhere this rule does not look. Every top-level tree, with both "
                + "counts: " + Describe(Report.Value));
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     The filter answers the question it declares, on a fixed table of paths.
    /// </summary>
    /// <remarks>
    ///     Load-bearing for the baseline. A baseline pinned against whatever the filter does
    ///     on the day it is pinned is a baseline pinned against nothing: if the filter later
    ///     regressed to accepting everything, the ratchet above would still pass — every tree
    ///     would become visible, the live set would empty, and the failure would read as
    ///     "someone cleaned up two dead entries". This control fails first, and says so in
    ///     terms of paths rather than counts.
    /// </remarks>
    [Test]
    public async Task TheFilterAnswersTheDeclaredQuestion()
    {
        var wrong = DeclaredFilterContract
            .Where(pair => SourceScan.IsBuildOutput(pair.Path) != pair.Rejected)
            .Select(pair => pair.Path + " (expected " + (pair.Rejected ? "rejected" : "accepted")
                               + ", got " + (SourceScan.IsBuildOutput(pair.Path) ? "rejected" : "accepted") + ")")
            .ToArray();

        await Assert.That(string.Join(" | ", wrong))
            .IsEqualTo(string.Empty)
            .Because(
                "SourceScan.IsBuildOutput is the filter that makes EnumerateCsFiles return an empty "
                + "list for a populated tree, so the behaviour pinned in MeasuredInvisibleTrees is "
                + "only meaningful while the filter still answers the question stated above. The "
                + "two product paths must be accepted or nothing is ever read; the five rejected "
                + "paths are rejected for four different reasons (build output, out of CI, the "
                + "gates' own fixtures, a sibling checkout) and this rule's subject is precisely "
                + "that they are all rejected the same way. Mismatches: "
                + (wrong.Length == 0 ? "(none)" : string.Join(" | ", wrong)));
    }

    private static string Describe(IReadOnlyList<TreeVisibility> report) =>
        report.Count == 0
            ? "(no top-level trees found)"
            : string.Join(
                " | ",
                report.Select(t => t.Name + ": " + t.FilesReturned + " returned, " + t.CsFilesOnDisk
                                        + " on disk" + (t.IsInvisibleToTheHelper ? "  <-- INVISIBLE" : string.Empty)));
}
