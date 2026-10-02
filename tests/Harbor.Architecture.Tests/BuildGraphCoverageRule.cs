// BuildGraphCoverageRule.cs — GUARD for issue #1005.
//
// WHAT #1005 MEASURED, AND WHERE THE MEASUREMENT WAS WRONG
// ---------------------------------------------------------
// #1005 reported FOUR csproj files that CI never builds, and supported that with
// a grep for each project's path over `*.csproj`:
//
//   apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj
//   src/Harbor.CodeGen/Harbor.CodeGen.csproj
//   src/Harbor.Plugins.Host/Harbor.Plugins.Host.csproj
//   src/Harbor.Transport.Remote/Harbor.Transport.Remote.csproj
//
// THREE of those four greps came back empty, and the emptiness WAS the finding:
// "not in a solution, and nothing references it". It was a separator artefact.
// The real edges are declared with BACKSLASHES — `..\..\apps\Harbor.App.Avalonia\
// Harbor.App.Avalonia.csproj` — and the search used forward slashes. So
// unconditional `ProjectReference`s from three projects that ARE rows of
// `Harbor.slnx` (`tests/Harbor.App.Avalonia.Tests`,
// `tests/Harbor.E2E.App.Avalonia`, `tests/Harbor.Transport.Remote.Tests`) and
// from four more (`Harbor.CodeGen`, consumed by Terminal.Abstractions,
// Tui.CellForge, Tui.AnsiPlain, Tui.NickConsoleEx and Harbor.Tui.CellForge.Tests)
// were invisible to a search that could not spell the path the way MSBuild stores
// it.
//
// Three of the four were therefore never orphans, and ONE was. Measured by
// walking `Harbor.slnx`'s rows and closing over `ProjectReference`:
//
//   92 solution rows, 92/92 present on disk, closure 101
//   src/  52 on disk, 1 not built  ->  src/Harbor.Plugins.Host
//   apps/ 2 on disk,  0 not built
//
// `Harbor.Samples.slnx` is not a counter-example to read comfort from either: it
// names 11 projects that do not exist on disk, which is why no workflow builds
// it. `App.Avalonia` and `CodeGen` sit in it, and that is a coincidence of
// history rather than coverage — what compiles them is the closure above. This
// file therefore judges the CLOSURE and not membership in a row, because row
// membership is neither necessary (three of the four) nor sufficient
// (`Harbor.Samples.slnx`).
//
// WHY THE ONE REAL CASE IS A DEFECT AND NOT A LOOSE END
// ----------------------------------------------------
// `src/Harbor.Plugins.Host` is 561 lines of complete code — a JSON-RPC 2.0 NDJSON
// MCP stdio server over the plugin pipeline — that no build had ever compiled.
// It is not scratch: it is the project the AOT story is built around. Its own
// csproj carries the reason AOT cannot fold it in ("compiles CS-source plugins
// with Roslyn, which is incompatible with NativeAOT"), and four documents
// describe it as a shipped composition root (`CLAUDE.md` lists it beside
// `apps/Harbor.App.Cli`; `docs/ARCHITECTURE.md` gives it the out-of-process
// row; `docs/ROADMAP.md` marks the milestone done; the root `README.md` names
// it). `FullLayerMatrixTests.OutOfScopeAssemblies` classifies it — correctly, for
// a LAYER question, since an `OutputType=Exe` composition root has no layer
// edges to constrain. Nothing anywhere in the tree asserted that the thing it
// classifies is a thing that BUILDS.
//
// The cost is already paid, not hypothetical: the guard added for #957/#968
// (`VestigialExtensionsPackageReferenceRule`) went red on this project asking
// whether a `<PackageReference>` could go, and its author recorded in the PR body
// that CI could not answer while the project was in no solution. An unbuildable
// project makes every csproj-level guard vacuous over itself, because the guard
// can propose a deletion it has no way to test.
//
// WHAT THIS FILE ASSERTS
// ----------------------
// One claim, in both directions, derived rather than tabulated:
//
//   1. every product-tree project on disk is in the build closure of
//      `Harbor.slnx` — the solution every `build` job compiles — and
//   2. every row of `Harbor.slnx` names a project that exists on disk.
//
// Direction 2 is free once the walk exists, and `Harbor.Samples.slnx`'s 11
// phantom rows show the rot is real; a row that names nothing is a promise no
// job keeps.
//
// Deliberately NOT a table of known exceptions. Closed #847 records that a
// declarative "known exceptions" list is suppression of the very defect the guard
// exists for, and #921 removed an allowance nobody read. There is no per-project
// allowance here at all: the claim is computed, and the fix for a red is to put
// the project in the graph or delete it — never to extend a list.
//
// THE PERIMETER, AND WHY IT IS A TREE AND NOT A LIST
// --------------------------------------------------
// The claim covers `src/` and `apps/`: the trees whose code ships, and the ones
// AGENTS.md's structure section enumerates. That is a predicate on the TREE (two
// words), stated once, with a reader who can check it — the same shape as the
// already-reviewed `ScanUniverseProbe.IsProductTree` on the reflection side of
// this boundary. It is not a per-project exemption list, and it is not silent:
// the failure message also prints the unbuilt projects OUTSIDE the perimeter, so
// the boundary is something a reader sees rather than infers from an absence.
//
// What the perimeter leaves out, and why each is a real answer rather than a
// shrug:
//
//   contrib/           Unmaintained and unbuilt by an explicit, documented
//                      decision that predates this issue. Not a product tree by
//                      anyone's claim.
//   analyzers/         NOT the same answer, and this file does not pretend
//                      otherwise. `analyzers/CfeControl` is compiled by nothing:
//                      no slnx row, no `ProjectReference`, no `Directory.Build.*`
//                      reference, and the `CFE0001` rule it defines is NoWarned
//                      in `Directory.Build.props` for tests. So a second unbuilt
//                      project exists, outside this perimeter, and wiring an
//                      analyzer into the build graph is a new axis with its own
//                      decision — not a line to add here. It is named here
//                      because the alternative was leaving it undocumented,
//                      which is how this file's subject started.
//   tools/, samples/   Not product trees. `Harbor.Evals` and `_build` are rows of
//                      `Harbor.slnx` and build; the rest are run manually or by
//                      `contrib-dryrun.yml`.
//
// WHY A CLOSURE AND NOT "IS IT A ROW"
// -----------------------------------
// Because a row list is the wrong instrument twice over: three of #1005's four
// looked unbuilt and were fine, and `Harbor.Samples.slnx` holds two projects that
// are fine and cannot be built at all. Transitive closure is the only reading of
// "does CI compile this" that gets both right, and it needs no knowledge of which
// project happens to be a row today.
//
// NON-VACUITY
// -----------
// The failure mode this file is most exposed to is passing for the wrong reason,
// so it carries three checks and the third is the load-bearing one:
//
//   1. TheWalkFoundSomething — the solution, the disk walk and the EXPANSION are
//      each non-trivial, so "no rows", "no projects" and "no edges" cannot each
//      report as a pass. A rule whose universe empties out prints the same string
//      as a rule that is working.
//   2. NoSolutionRow_IsDangling — direction 2 above, asserted rather than assumed.
//   3. ThePredicateAnswersTheDeclaredQuestion — a positive control over a FIXED,
//      filesystem-free table, feeding the SAME `UnbuiltProjects` and `DanglingRows`
//      functions the live tests use. Its table is built so that a rule which had
//      quietly halved itself fails: it holds a project compiled ONLY as a
//      transitive reference of a row (so a rows-vs-disk rule would wrongly call it
//      unbuilt), two genuinely unbuilt projects, one per product tree (so a rule
//      that stopped reporting yields the empty set), an unbuilt project OUTSIDE
//      the perimeter, a row naming an absent project, and an edge pointing at
//      something not on disk.
//
// Point 3 is the answer to #591, where a tool halved its own rule and returned a
// plausible `0`. A guard that can only be falsified by the tree it grades has no
// such answer.
//
// RELATION TO WHAT IS ALREADY HERE
// --------------------------------
// `ScanUniverseRule` measures the REFLECTION-scan universe (what
// `LoadHarborAssemblies` can see). This file measures the BUILD universe (what
// `dotnet build Harbor.slnx` compiles). Different helper, different file,
// different side of the boundary; neither reads the other. The product-tree
// predicate is deliberately the same ANSWER stated twice, because it is the
// boundary itself and it should have one meaning.
// `RepoPaths` also mentions `Harbor.slnx` — but as a ROOT MARKER (`File.Exists`),
// not as a project list; it globs `src/**/*.csproj` off disk for everything else.
// This is the first thing in the tree to read the rows as rows, so the disk walk
// is its own (see `NeverWalked`) for the same reason that file gives for
// re-deriving its own `AssemblyNameOf`: a shared helper would let a change to one
// walk silently resize the other, which is the perimeter-drift class this file's
// subject is made of.
//
// WHAT IS NOT ASSERTED HERE
// -------------------------
// That a built project is CORRECT. Adding `Harbor.Plugins.Host` to the solution
// makes it compile; it does not make the out-of-process MCP boundary tested, and
// this file does not claim otherwise. The host's own README asserted validation
// by `Harbor.Plugins.Runtime.Tests` and E2E tests, and no test project references
// it — that project references `Harbor.Plugins.Hosting`, a different assembly —
// so the sentence described tests that cannot see the host. The README now says
// what is true: the solution compiles it, and its stdio protocol has no test.
// Wiring a subprocess test for it is a new axis; a guard that measured "is the
// host covered" instead of "does the host build" would be a different issue at a
// different price.

namespace Harbor.Architecture.Tests;

using System.Xml.Linq;

/// <summary>
///     Walks the build graph the way CI does — the solution's rows closed over
///     <c>&lt;ProjectReference&gt;</c> — and answers, per project on disk, whether
///     any job compiles it.
/// </summary>
internal static class BuildGraphProbe
{
    /// <summary>
    ///     Directories the disk walk never descends into. Build output, VCS metadata
    ///     and sibling checkouts are not the repository; counting them would make the
    ///     answer a number about a tree that is not the subject.
    /// </summary>
    /// <remarks>
    ///     Duplicates <c>ScanUniverseProbe.NeverWalked</c> on purpose — see the file
    ///     header.
    /// </remarks>
    internal static readonly string[] NeverWalked = [".git", ".worktrees", "bin", "obj", "node_modules"];

    /// <summary>
    ///     The trees whose projects ship. A project outside them is still walked and
    ///     still reported; it is simply not the subject of the claim.
    /// </summary>
    internal static readonly string[] ProductTrees = ["apps", "src"];

    /// <summary>Is this top-level tree a product tree? The one question the perimeter asks.</summary>
    internal static bool IsProductTree(string tree) => ProductTrees.Contains(tree, StringComparer.Ordinal);

    /// <summary>Repo-relative, forward-slashed form of an absolute path.</summary>
    internal static string Relative(string root, string absolutePath) =>
        Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

    /// <summary>
    ///     The top-level tree an <em>absolute</em> path lives under, relative to
    ///     <paramref name="root" />.
    /// </summary>
    /// <remarks>
    ///     Taking the path relative to the root is not incidental. An absolute path
    ///     has an empty first segment, so asking this question of the raw path
    ///     concludes that no project is product — and the claim then holds vacuously.
    ///     The sibling file records that as a real bug in its first draft, which is
    ///     why the rule is stated once, here, and reused by the control below.
    /// </remarks>
    internal static string TreeOf(string root, string absolutePath)
    {
        string relative = Relative(root, absolutePath);
        int slash = relative.IndexOf('/');
        return slash < 0 ? relative : relative[..slash];
    }

    /// <summary>Every <c>*.csproj</c> in the repository, as absolute paths, sorted.</summary>
    internal static IReadOnlyList<string> DiskCsprojs(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            try
            {
                found.AddRange(Directory.GetFiles(current, "*.csproj"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

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
                if (!NeverWalked.Contains(Path.GetFileName(directory), StringComparer.Ordinal))
                {
                    pending.Push(directory);
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>The projects a solution file names, as absolute paths — its rows, read as rows.</summary>
    /// <remarks>
    ///     <para>
    ///         Row paths are solution-relative and therefore always forward-slashed,
    ///         which is the asymmetry that hid three of #1005's four projects from a
    ///         grep: <c>&lt;ProjectReference Include&gt;</c> values in this tree are
    ///         backslashed, and a search that could not spell those paths found
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         Both spellings are accepted, because MSBuild accepts both on every
    ///         platform and a rule that accepted one would be a rule the build's own
    ///         leniency could outrun.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<string> SolutionRows(string root, string solutionFile)
    {
        try
        {
            var rows = new List<string>();
            foreach (XElement element in XDocument.Load(solutionFile, LoadOptions.None).Descendants())
            {
                if (!string.Equals(element.Name.LocalName, "Project", StringComparison.Ordinal))
                {
                    continue;
                }

                string? include = element.Attributes()
                    .FirstOrDefault(a => string.Equals(a.Name.LocalName, "Path", StringComparison.Ordinal))
                    ?.Value;

                if (!string.IsNullOrWhiteSpace(include))
                {
                    rows.Add(Resolve(root, include!));
                }
            }

            return rows;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    ///     The <c>&lt;ProjectReference&gt;</c> targets a csproj declares, resolved
    ///     against that csproj's own directory.
    /// </summary>
    /// <remarks>
    ///     Every edge, unconditionally; a <c>Condition</c> is not consulted. That is a
    ///     deliberate over-approximation, and it over-approximates in the direction
    ///     that can hide something, so it is named here rather than left implicit: an
    ///     edge no CI job takes still proves the referring project is a row's
    ///     neighbour, but a project reachable ONLY through an edge no job enables
    ///     would be reported as compiled when it is not. The live tree's only such
    ///     edges are `Harbor.Hosting`'s six `HarborWithSpectreTui` references into
    ///     `contrib/`, which is outside the perimeter.
    /// </remarks>
    internal static IReadOnlyList<string> DeclaredReferences(string csprojPath)
    {
        try
        {
            string projectDir = Path.GetDirectoryName(csprojPath)!;
            var result = new List<string>();

            foreach (XElement element in XDocument.Load(csprojPath, LoadOptions.None).Descendants())
            {
                if (!string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
                {
                    continue;
                }

                string? include = element.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                {
                    result.Add(Resolve(projectDir, include!));
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    ///     Projects on disk that <c>dotnet build</c> of the solution does not compile,
    ///     as repo-relative paths, sorted.
    /// </summary>
    /// <param name="root">Repository root; used only to make the answer readable.</param>
    /// <param name="diskProjects">Absolute paths of every csproj on disk.</param>
    /// <param name="solutionRows">Absolute paths the solution names.</param>
    /// <param name="declaredEdges">
    ///     csproj path → the <c>ProjectReference</c> targets it declares. A key that is
    ///     absent contributes no edges, which is what makes a row naming a deleted
    ///     project stop the walk rather than invent members of the closure.
    /// </param>
    /// <param name="treeFilter">
    ///     Which top-level trees to report. Defaults to the product perimeter; the
    ///     live test also asks for the complement, so that what the perimeter excludes
    ///     is printed rather than absent.
    /// </param>
    internal static IReadOnlyList<string> UnbuiltProjects(
        string root,
        IEnumerable<string> diskProjects,
        IEnumerable<string> solutionRows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredEdges,
        Func<string, bool>? treeFilter = null)
    {
        var filter = treeFilter ?? IsProductTree;
        var compiled = Closure(solutionRows, declaredEdges);
        var unbuilt = new List<string>();

        foreach (string project in diskProjects)
        {
            if (filter(TreeOf(root, project)) && !compiled.Contains(project))
            {
                unbuilt.Add(Relative(root, project));
            }
        }

        unbuilt.Sort(StringComparer.Ordinal);
        return unbuilt;
    }

    /// <summary>
    ///     Solution rows that name no project on disk, as repo-relative paths, sorted.
    ///     The mirror image of <see cref="UnbuiltProjects" />: a promise no job keeps.
    /// </summary>
    internal static IReadOnlyList<string> DanglingRows(
        string root,
        IEnumerable<string> diskProjects,
        IEnumerable<string> solutionRows)
    {
        var onDisk = new HashSet<string>(diskProjects, StringComparer.Ordinal);
        var dangling = new List<string>();

        foreach (string row in solutionRows)
        {
            if (!onDisk.Contains(row))
            {
                dangling.Add(Relative(root, row));
            }
        }

        dangling.Sort(StringComparer.Ordinal);
        return dangling;
    }

    /// <summary>
    ///     The transitive <c>ProjectReference</c> closure of <paramref name="seed" />.
    /// </summary>
    /// <remarks>
    ///     A stack, not recursion: the tree is wide, and the closure is computed once
    ///     per test run behind a <see cref="Lazy{T}" />.
    /// </remarks>
    internal static HashSet<string> Closure(
        IEnumerable<string> seed,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredEdges)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(seed);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!reached.Add(current))
            {
                continue;
            }

            if (!declaredEdges.TryGetValue(current, out IReadOnlyList<string>? next))
            {
                continue;
            }

            foreach (string target in next)
            {
                pending.Push(target);
            }
        }

        return reached;
    }

    private static string Resolve(string baseDirectory, string include) =>
        Path.GetFullPath(Path.Combine(baseDirectory, include.Replace('\\', '/')));
}

/// <summary>
///     Asserts that the build graph CI compiles covers every product project in the
///     tree. See the file header for the measurement, the perimeter, and the
///     non-vacuity argument.
/// </summary>
public sealed class BuildGraphCoverageRule
{
    private static readonly Lazy<GraphReport> Report = new(Measure);

    /// <summary>What the two walks found, once.</summary>
    private sealed record GraphReport(
        int SolutionRowCount,
        int DiskCsprojCount,
        int ProductProjectsInClosure,
        int ProductSolutionRows,
        IReadOnlyList<string> UnbuiltProductProjects,
        IReadOnlyList<string> UnbuiltOutsidePerimeter,
        IReadOnlyList<string> DanglingRows);

    private static GraphReport Measure()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return new GraphReport(0, 0, 0, 0, [], [], []);
        }

        IReadOnlyList<string> disk = BuildGraphProbe.DiskCsprojs(root);
        IReadOnlyList<string> rows = BuildGraphProbe.SolutionRows(root, Path.Combine(root, "Harbor.slnx"));

        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string project in disk)
        {
            edges[project] = BuildGraphProbe.DeclaredReferences(project);
        }

        IReadOnlyList<string> unbuilt = BuildGraphProbe.UnbuiltProjects(root, disk, rows, edges);

        return new GraphReport(
            rows.Count,
            disk.Count,
            disk.Count(p => BuildGraphProbe.IsProductTree(BuildGraphProbe.TreeOf(root, p))) - unbuilt.Count,
            rows.Count(r => BuildGraphProbe.IsProductTree(BuildGraphProbe.TreeOf(root, r))),
            unbuilt,
            BuildGraphProbe.UnbuiltProjects(root, disk, rows, edges, tree => !BuildGraphProbe.IsProductTree(tree)),
            BuildGraphProbe.DanglingRows(root, disk, rows));
    }

    // =====================================================================
    // 1. The claim.
    // =====================================================================

    /// <summary>
    ///     Every product-tree project on disk must be in the build closure of
    ///     <c>Harbor.slnx</c>.
    /// </summary>
    [Test]
    public async Task EveryProductProject_IsCompiledByTheSolution()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule walks a checkout; outside one the closure is empty and every "
                   + "assertion below would hold for the wrong reason — see TheWalkFoundSomething");

        var report = Report.Value;
        IReadOnlyList<string> unbuilt = report.UnbuiltProductProjects;

        await Assert.That(string.Join(" | ", unbuilt))
            .IsEqualTo(string.Empty)
            .Because(
                "these projects ship code and no build compiles them. A csproj in no solution and no "
                + "ProjectReference is not merely untested, it is UNCHECKED: every csproj-level guard "
                + "can propose a change to it and no job can say whether the change worked. That is not "
                + "hypothetical — the guard added for #968 asked whether a PackageReference in "
                + "src/Harbor.Plugins.Host could be deleted and CI could not answer, because the "
                + "project is in no solution. Row membership is not the test and cannot be: "
                + "apps/Harbor.App.Avalonia, src/Harbor.CodeGen and src/Harbor.Transport.Remote have "
                + "no row yet are compiled through ProjectReferences from rows (the first two spell "
                + "those edges with BACKSLASHES, which is why #1005's forward-slash grep reported them "
                + "as unreferenced), and Harbor.Samples.slnx holds two projects that are compiled and "
                + "eleven that do not exist. The answer this rule accepts is the transitive closure, "
                + "which gets both right. Unbuilt (" + unbuilt.Count + "): " + Describe(unbuilt)
                + " | walked " + report.DiskCsprojCount + " csproj, " + report.SolutionRowCount
                + " solution rows." + OutsidePerimeterNote(report));
    }

    /// <summary>Every row of <c>Harbor.slnx</c> must name a project that exists.</summary>
    [Test]
    public async Task NoSolutionRow_IsDangling()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("a solution with no rows resolves every reference to nothing");

        var report = Report.Value;
        IReadOnlyList<string> dangling = report.DanglingRows;

        await Assert.That(string.Join(" | ", dangling))
            .IsEqualTo(string.Empty)
            .Because(
                "a row naming a project that is not on disk is a promise no job keeps, and it is the "
                + "same rot as an unbuilt project seen from the other side. Harbor.Samples.slnx has 11 "
                + "such rows today, which is why no workflow builds it and why its projects cannot be "
                + "used as evidence that anything is covered. A row added before the project is "
                + "committed, or left behind by a rename, is invisible to every gate in the tree. "
                + "Dangling (" + dangling.Count + "): " + Describe(dangling));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The walk found a solution, a tree, and TRANSITIVE EDGES — so "no rows",
    ///     "no projects" and "no edges" cannot each report as a pass.
    /// </summary>
    [Test]
    public async Task TheWalkFoundSomething()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a checkout the walk has nothing to find, and an empty finding is what a "
                   + "broken walk looks like from here");

        var report = Report.Value;

        await Assert.That(report.SolutionRowCount).IsGreaterThan(0)
            .Because("no solution row parsed — the .slnx moved, the Project element is spelled "
                   + "differently, or the file failed to load. Every assertion in this file would then "
                   + "hold over an empty closure, which is indistinguishable from a working rule");

        await Assert.That(report.DiskCsprojCount).IsGreaterThan(0)
            .Because("the disk walk found no csproj, so the unbuilt set is empty for want of a subject "
                   + "rather than for want of a build");

        // The load-bearing one. If the closure held nothing but the rows, the rule
        // would report every project reached through a ProjectReference as unbuilt
        // — and it would be right about the three #1005 found and wrong about all
        // fifty-odd others.
        await Assert.That(report.ProductProjectsInClosure).IsGreaterThan(report.ProductSolutionRows)
            .Because(
                "the closure is no larger than the row list, so no ProjectReference edge was followed. "
                + "That is the half of this rule which makes row membership the wrong test, and the "
                + "failure mode is silent: unbuilt would be reported as a set of projects that are merely "
                + "absent from the .slnx, every one of which is in fact compiled. Either the "
                + "ProjectReference parse regressed to finding no elements, or the graph was flattened "
                + "into explicit rows — a reviewed change, which should say so here rather than be "
                + "absorbed silently. Product projects compiled: "
                + report.ProductProjectsInClosure + " | product rows: " + report.ProductSolutionRows);
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     The predicate answers the question this file declares, on a fixed
    ///     filesystem-free table.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A guard that can only be falsified by the tree it grades has no answer
    ///         when the tree is quiet. Closed #591 is the precedent: a tool halved its
    ///         own rule and returned a plausible `0`, and the plausible zero was the
    ///         whole failure. The table below therefore plants a hit, and plants
    ///         several DISTINCT ones so that any single halving is caught by a
    ///         different row.
    ///     </para>
    ///     <list type="bullet">
    ///     <item><c>Harbor.Row.Deep</c> is compiled only as a transitive reference of a row, so a rule
    ///     comparing rows to disk would wrongly report it.</item>
    ///     <item><c>Harbor.Orphan</c> and <c>Harbor.App.Orphan</c> are unbuilt, one per product tree, so a
    ///     rule that stopped reporting produces the empty set and fails here rather
    ///     than passing on the live tree.</item>
    ///     <item><c>Harbor.OrphanTool</c> is unbuilt and OUTSIDE the perimeter, so the perimeter is
    ///     exercised as a stated boundary instead of assumed.</item>
    ///     <item><c>Harbor.Missing</c> is a row with no project on disk, exercising direction 2.</item>
    ///     <item><c>Harbor.Deleted</c> is an edge pointing outside the disk walk, so an edge to
    ///     nothing cannot be mistaken for a member of the closure.</item>
    ///     </list>
    /// </remarks>
    [Test]
    public async Task ThePredicateAnswersTheDeclaredQuestion()
    {
        const string root = "/synthetic";

        string[] disk =
        [
            "/synthetic/apps/Harbor.App.Orphan/Harbor.App.Orphan.csproj",
            "/synthetic/contrib/Harbor.Contrib/Harbor.Contrib.csproj",
            "/synthetic/src/Harbor.Orphan/Harbor.Orphan.csproj",
            "/synthetic/src/Harbor.Row/Harbor.Row.csproj",
            "/synthetic/src/Harbor.Row.Deep/Harbor.Row.Deep.csproj",
            "/synthetic/tools/Harbor.OrphanTool/Harbor.OrphanTool.csproj",
            "/synthetic/tools/Harbor.Tool/Harbor.Tool.csproj",
        ];

        string[] rows =
        [
            "/synthetic/contrib/Harbor.Contrib/Harbor.Contrib.csproj",
            "/synthetic/src/Harbor.Missing/Harbor.Missing.csproj",
            "/synthetic/src/Harbor.Row/Harbor.Row.csproj",
            "/synthetic/tools/Harbor.Tool/Harbor.Tool.csproj",
        ];

        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["/synthetic/src/Harbor.Row/Harbor.Row.csproj"] =
            [
                "/synthetic/src/Harbor.Row.Deep/Harbor.Row.Deep.csproj",
                "/synthetic/src/Harbor.Deleted/Harbor.Deleted.csproj",
            ],
        };

        string[] unbuilt = [.. BuildGraphProbe.UnbuiltProjects(root, disk, rows, edges)];
        string[] dangling = [.. BuildGraphProbe.DanglingRows(root, disk, rows)];

        await Assert.That(string.Join(" | ", unbuilt))
            .IsEqualTo("apps/Harbor.App.Orphan/Harbor.App.Orphan.csproj | src/Harbor.Orphan/Harbor.Orphan.csproj")
            .Because(
                "these two are the projects the table plants as unbuilt, and they are the WHOLE expected "
                + "answer. The result is empty when the walk had nothing to report; it is missing "
                + "Harbor.Row.Deep when ProjectReference expansion was not followed (that project is a "
                + "row's neighbour and IS compiled); it carries Harbor.OrphanTool when the perimeter was "
                + "widened to every tree; and it carries Harbor.Contrib or Harbor.Tool when row "
                + "membership was mistaken for being compiled — both are rows, and a row is a promise, "
                + "not a build. Live: " + (unbuilt.Length == 0 ? "(none)" : string.Join(" | ", unbuilt)));

        await Assert.That(string.Join(" | ", dangling))
            .IsEqualTo("src/Harbor.Missing/Harbor.Missing.csproj")
            .Because(
                "one row in the table names a project that is not on disk, and it must be the only thing "
                + "reported: the rows that do resolve must stay silent, or the check is a list of every "
                + "row in the solution. Live: " + (dangling.Length == 0 ? "(none)" : string.Join(" | ", dangling)));
    }

    // =====================================================================
    // 4. Helpers.
    // =====================================================================

    private static string Describe(IReadOnlyList<string> items) =>
        items.Count == 0
            ? "(none)"
            : string.Join(" | ", items.Take(12)) + (items.Count > 12 ? $" | ... and {items.Count - 12} more" : string.Empty);

    /// <summary>
    ///     Prints what the perimeter leaves out, so the boundary is something a reader
    ///     of the log sees rather than something they infer from an absence.
    /// </summary>
    /// <remarks>
    ///     The per-tree counts are never truncated even when the list is: the counts are
    ///     what tell a reader WHICH tree the excluded project is in, and a list cut at
    ///     twelve would have hidden the two non-<c>contrib</c> entries on the day this
    ///     was written while showing twenty unmaintained ones.
    /// </remarks>
    private static string OutsidePerimeterNote(GraphReport report)
    {
        if (report.UnbuiltOutsidePerimeter.Count == 0)
        {
            return "";
        }

        string byTree = string.Join(
            ", ",
            report.UnbuiltOutsidePerimeter
                .GroupBy(p => p[..p.IndexOf('/')], StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + "=" + g.Count()));

        return " | OUTSIDE the src/+apps/ perimeter this rule does not claim (see the file header), "
               + "unbuilt: " + Describe(report.UnbuiltOutsidePerimeter) + " (by tree: " + byTree + ")";
    }
}
