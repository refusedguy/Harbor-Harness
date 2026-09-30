// DiffSurfaceNameCollisionRule.cs — GUARD for issue #570.
//
// THE CONVENTION BEING ENFORCED
// ------------------------------
// The line diff has exactly ONE implementation in this repo:
// `src/Harbor.Ui.Framework.Rendering/Widgets/LineDiff.cs` (Myers O(ND), added by
// #694). Everything that shows a diff to a user is a *projection* of that one
// engine, and the set of such projections is the DIFF SURFACE.
//
// Inside the diff surface, no simple type name may be declared by two different
// projects. One name, one implementation, in that perimeter.
//
// WHAT #570 FOUND, AND WHAT WAS STILL TRUE AFTERWARDS
// ---------------------------------------------------
// #570 reported two classes named `DiffViewModel` that each re-derived a diff by
// line index, and — the part that made it a real bug rather than a style note —
// only one of the two normalised CRLF, so a Windows-authored file diffed as
// fully rewritten in one host and as one changed line in the other.
//
// #694 fixed the algorithm, not the name. `LineDiff.SplitLines` now replaces
// "\r\n" for BOTH sides, so the CRLF divergence is gone, and both projections
// call `LineDiff` rather than comparing `left[i]` against `right[i]`. The index
// walk is gone from both.
//
// What survived is the reason the two were ever confusable: the same simple
// name, declared by two assemblies, for two projections. And this is exactly
// the shape of the drift #570 was about, one level up. The bug needed the
// reader to believe "the DiffViewModel" is singular, because the divergence was
// invisible until someone fed a CRLF file to the copy they did not mean. Two
// live types behind one name is the precondition for that: a fix lands in
// whichever one the author had open, the existing guard keeps passing because
// it exercises both, and the defect ships in the other. The end-to-end test
// passing is not evidence here — it is the mechanism that hid the bug.
//
// WHY A RULE AND NOT JUST A RENAME
// --------------------------------
// A rename is one commit. The name that caused the confusion is a good name —
// "the diff view-model" is what a person types before checking which of the
// two projections they mean, which is the whole problem. Nothing stops the
// third copy. The invariant needs teeth, and the teeth are here.
//
// WHY IT IS KEYED ON THE PERIMETER AND NOT ON A NAME
// ---------------------------------------------------
// A rule naming `DiffViewModel` is a rule about a string: rename the class and
// the duplication is legal again, so the rule would be satisfied by the exact
// change that re-creates the hazard. This rule never names a type. It derives
// the perimeter from the ENGINE — every source file that projects `LineDiff` —
// and requires the names inside it to be unique across projects. Renaming
// either projection satisfies it, which is the correct fix, and adding a fourth
// projection that collides with a third trips it, whichever name is involved.
//
// The perimeter is narrow on purpose. A repo-wide "no duplicate type names"
// rule is not this rule: the repo has 25+ same-named types across assemblies
// (`Program`, `HostBuilder`, `ConfigJsonContext`, `ToastKind`, …), most of them
// legitimate, and a rule that condemned them would be a different, much larger
// piece of work wearing this one's name. What makes the diff surface special is
// not that its types are duplicated — it is that they are ALTERNATIVES. Two
// names for one concept is a choice; two names for two concepts is a bug.
//
// The two projections are genuinely different, which is why this is a rename and
// not a deletion (see the file of the same name in the perimeter below).
//
// PERIMETER
// ---------
// `src/` + `apps/`, for declarations. `contrib/` is excluded on purpose: it is
// unmaintained, outside CI, and out of scope by owner decision (AGENTS.md) —
// `contrib/apps/Harbor.App.Wpf` has its own `DiffViewModel` and
// `contrib/apps/Harbor.App.Blazor` binds the desktop one. Neither gap is
// accidental and neither is scanned.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan walked a checkout, read real files, and found the
//      real projections of the engine. "No violations" is trivially satisfied by
//      a scan that read nothing.
//   2. NonVacuity_Scan_DetectsTheCollisionInSyntheticSources — THE POSITIVE
//      CONTROL. The probe is handed the #570 shape item for item (two files, in
//      two different projects, declaring the same name, both calling the engine)
//      and four decoys it must NOT report — including a same-named type in a
//      file that does not project the engine at all, and a name that merely
//      CONTAINS the duplicated one.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One duplicated name in the diff surface (#570).</summary>
/// <param name="Name">The duplicated simple type name.</param>
/// <param name="DeclaringFiles">Every file in the perimeter declaring it, sorted.</param>
internal sealed record DiffNameCollision(string Name, IReadOnlyList<string> DeclaringFiles);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="Collisions">Every finding, sorted — a non-empty list is the #570 failure.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
/// <param name="EngineFilePresent">Whether the shared engine itself was found.</param>
/// <param name="PerimeterFiles">Which files project the engine, sorted.</param>
internal sealed record DiffSurfaceReport(
    IReadOnlyList<DiffNameCollision> Collisions,
    int FilesScanned,
    bool EngineFilePresent,
    IReadOnlyList<string> PerimeterFiles);

/// <summary>
///     Finds type names declared by more than one project inside the diff surface.
/// </summary>
internal static partial class DiffSurfaceNameCollisionProbe
{
    /// <summary>The one home of the diff algorithm. Repo-relative, forward slashes.</summary>
    internal const string EngineFile = "src/Harbor.Ui.Framework.Rendering/Widgets/LineDiff.cs";

    /// <summary>Repository roots walked for declarations. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] SourceRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly string[] SkippedDirectories =
        [".git", "bin", "obj", "external", ".worktrees", "node_modules", "contrib"];

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static DiffSurfaceReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new DiffSurfaceReport([], 0, false, []);
        }

        var sources = new List<(string Relative, string[] Lines)>();
        foreach (string file in EnumerateFiles(repoRoot, SourceRoots, "*.cs"))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            sources.Add((MakeRelative(repoRoot, file), lines));
        }

        return ScanFiles(sources, File.Exists(Path.Combine(repoRoot, EngineFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    /// <summary>
    ///     Grades already-read files. Exposed so the positive control drives the
    ///     REAL matcher — comment stripping and project resolution included —
    ///     rather than a second implementation of it, which is the only way
    ///     "it can fail" means anything.
    /// </summary>
    internal static DiffSurfaceReport ScanFiles(
        List<(string Relative, string[] Lines)> sources,
        bool engineFilePresent)
    {
        // name -> the projects that declare it. A name in two projects is a finding.
        var byName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var filesByName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var perimeter = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            string[] clean = SourceCommentStripper.StripAll(source.Lines);

            // The perimeter predicate: this file PROJECTS the engine. It must call
            // it, not merely name it in prose — a doc comment saying
            // `<see cref="LineDiff" />` is not a projection, and the XML docs on
            // both #570 copies are full of exactly that. `using` alone is not
            // enough either: both projects import the namespace for other widgets,
            // so the engine's static class is matched on a real member access.
            bool projectsEngine = false;
            foreach (string line in clean)
            {
                if (EngineReference().IsMatch(line))
                {
                    projectsEngine = true;
                    break;
                }
            }

            if (!projectsEngine)
            {
                continue;
            }

            perimeter.Add(source.Relative);
            string project = ProjectOf(source.Relative);

            foreach (string name in DeclaredTypeNames(clean))
            {
                if (!byName.TryGetValue(name, out SortedSet<string>? projects))
                {
                    projects = new SortedSet<string>(StringComparer.Ordinal);
                    byName[name] = projects;
                    filesByName[name] = new SortedSet<string>(StringComparer.Ordinal);
                }

                projects.Add(project);
                filesByName[name].Add(source.Relative);
            }
        }

        var collisions = new List<DiffNameCollision>();
        foreach ((string name, SortedSet<string> projects) in byName)
        {
            if (projects.Count > 1)
            {
                // SortedSet<T> is not an IReadOnlyList<T>, so the declaration is
                // materialised here rather than stored as the live set.
                collisions.Add(new DiffNameCollision(name, [.. filesByName[name]]));
            }
        }

        collisions.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new DiffSurfaceReport(collisions, sources.Count, engineFilePresent, [.. perimeter]);
    }

    /// <summary>
    ///     Every simple type name a file declares. Comments are already stripped
    ///     by the caller, so a name in an XML doc is not a declaration. Nested and
    ///     generic types count — the rule is about a name, not about a nesting
    ///     depth.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredTypeNames(string[] cleanLines)
    {
        var names = new List<string>();
        foreach (string line in cleanLines)
        {
            foreach (Match match in TypeKeyword().Matches(line))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    /// <summary>
    ///     The project a perimeter file belongs to, which is what makes a name
    ///     duplicated. Taken from the path segment under <c>src/</c> or
    ///     <c>apps/</c>; a partial type split across two files of ONE project is
    ///     legal C# and is not reported.
    /// </summary>
    internal static string ProjectOf(string relativeFile)
    {
        string[] segments = relativeFile.Split('/');
        return segments.Length >= 2 ? segments[1] : segments[0];
    }

    /// <summary>
    ///     Every matching file under the named repository-relative roots.
    /// </summary>
    /// <remarks>
    ///     Walked by hand rather than with
    ///     <see cref="SearchOption.AllDirectories" /> so the skipped directories
    ///     are PRUNED instead of filtered after the fact: a full recursive
    ///     enumeration descends into <c>.git/</c> and the <c>external/</c>
    ///     submodule on every run. An unreadable directory is skipped rather than
    ///     thrown, because a guard must not fail the build for a permission it did
    ///     not ask about.
    /// </remarks>
    private static IEnumerable<string> EnumerateFiles(string repoRoot, string[] roots, string pattern)
    {
        foreach (string root in roots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(absolute);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                string[] files;
                try
                {
                    files = Directory.GetFiles(current, pattern);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    yield return file;
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
                    if (IsSkippedDirectory(directory))
                    {
                        continue;
                    }

                    pending.Push(directory);
                }
            }
        }
    }

    /// <summary>Whether a directory's NAME is one this walk never descends into.</summary>
    private static bool IsSkippedDirectory(string absolutePath)
    {
        string name = Path.GetFileName(absolutePath);
        return SkippedDirectories.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    ///     A real member access on the engine's static class. The trailing dot is
    ///     what separates <c>LineDiff.Compute</c> from a type named
    ///     <c>LineDiffRow</c> or a prose mention of the name.
    /// </summary>
    [GeneratedRegex(@"\bLineDiff\s*\.")]
    private static partial Regex EngineReference();

    /// <summary>
    ///     A type declaration's keyword and name, at any modifier depth. Records,
    ///     structs and enums count: the rule is about a name, not about a keyword.
    /// </summary>
    [GeneratedRegex(@"\b(?:class|struct|record|interface|enum)\s+(?<name>\w+)")]
    private static partial Regex TypeKeyword();

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard for issue #570: inside the diff surface — the projections of the one
///     <c>LineDiff</c> engine — no simple type name is declared by two projects.
/// </summary>
public sealed class DiffSurfaceNameCollisionRule
{
    private static readonly Lazy<DiffSurfaceReport> Report = new(
        () => DiffSurfaceNameCollisionProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     Rule 1: no name is declared by two projects inside the diff surface.
    /// </summary>
    /// <remarks>
    ///     #570's CRLF divergence is already fixed by #694 — <c>LineDiff</c>
    ///     normalises line endings for both sides, so the two copies can no longer
    ///     disagree about a Windows file. The name is what outlived it, and the
    ///     name is what lets the next divergence hide: a fix lands in one copy,
    ///     the guard keeps passing because it drives both, and the user sees the
    ///     other one.
    /// </remarks>
    [Test]
    public async Task DiffSurface_HasNoNameDeclaredByTwoProjects()
    {
        var collisions = Report.Value.Collisions;

        await Assert.That(collisions).IsEmpty()
            .Because(
                "the line diff has one implementation (Rendering.Widgets.LineDiff, #694) and every view of "
                + "it is a projection. Inside that surface, two types behind one name is what made #570 "
                + "possible: the reader assumed \"the DiffViewModel\" was singular, so the CRLF fix could "
                + "land in one copy while the other kept diverging, and the end-to-end test still passed "
                + "because it exercised both. Found: " + Describe(collisions));
    }

    // =====================================================================
    // 2. The half that makes the rule safe to satisfy.
    // =====================================================================

    /// <summary>
    ///     Rule 2: the shared engine is still there, and the surface is still more
    ///     than one projection. Without this, rule 1 is satisfied by deleting the
    ///     diff view-models — turning a de-duplication into an outage that passes
    ///     every gate.
    /// </summary>
    [Test]
    public async Task SharedDiffEngine_IsStillTheOneHome()
    {
        var report = Report.Value;

        await Assert.That(report.EngineFilePresent).IsTrue()
            .Because(
                DiffSurfaceNameCollisionProbe.EngineFile + " is the ONE home of the diff algorithm. If it "
                + "is genuinely gone, the diff moved somewhere else, and THAT change — not #570 — is what "
                + "the projections and the docs have to be updated for. Without it, \"no duplicate names\" "
                + "is satisfied by having no diff at all.");

        await Assert.That(report.PerimeterFiles.Count).IsGreaterThan(1)
            .Because(
                "the diff surface is the set of files that project LineDiff, and there must still be more "
                + "than one of them. Rule 1 is about the names INSIDE that set, so a surface collapsed to "
                + "a single projection satisfies it by deleting a feature the product still needs — the "
                + "side-by-side view bound by Avalonia's DiffView.axaml and the store-fed base view-model "
                + "are different contracts, not copies. Projecting files: "
                + Describe(report.PerimeterFiles));
    }

    // =====================================================================
    // 3. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really found the projections it
    ///     grades. A rule satisfied by looking at nothing is the failure this repo
    ///     has already paid for twice.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero files and every "
                   + "rule above is satisfied by having nothing to look at");

        var report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter are "
                   + "wrong and the rule is vacuously green");

        await Assert.That(report.PerimeterFiles.Count).IsGreaterThan(1)
            .Because(
                "the scan must find the real projections of LineDiff before the rule may claim their names "
                + "are unique. A scan that cannot find what should be there is a scan whose silence means "
                + "nothing — the perimeter predicate (a real member access on the engine, not a doc "
                + "mention) is what decides it. Found: " + Describe(report.PerimeterFiles));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed the #570 shape item for item
    ///     — two files, in two different projects, declaring the same name, both
    ///     calling the engine — and four decoys it must NOT report.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsTheCollisionInSyntheticSources()
    {
        List<(string Relative, string[] Lines)> colliding = new()
        {
            // (1) The side-by-side projection, calling the engine.
            ("src/Harbor.Ui.Framework.ViewModels/ViewModels/DiffViewModel.cs",
            [
                "using Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "namespace Harbor.Ui.Framework.ViewModels;",
                string.Empty,
                "public sealed partial class DiffViewModel : ObservableObject",
                "{",
                "    public void Compute() => Rows.Add(LineDiff.ComputeSideBySide(LeftText, RightText)[0]);",
                "}",
            ]),
            // (2) The same name, in a DIFFERENT project, also calling the engine.
            // This pair is #570 verbatim, and it is the whole finding: one name,
            // two assemblies, two independent things to fix.
            ("src/Harbor.Desktop.Abstractions/ViewModels/DiffViewModel.cs",
            [
                "using Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public sealed partial class DiffViewModel : ObservableObject",
                "{",
                "    public void ComputeDiff() => DiffText = LineDiff.ToUnifiedText(LineDiff.Compute(Before, After));",
                "}",
            ]),
        };

        DiffSurfaceReport report = DiffSurfaceNameCollisionProbe.ScanFiles(colliding, engineFilePresent: true);

        await Assert.That(string.Join(" | ", report.Collisions.Select(c => c.Name))).IsEqualTo("DiffViewModel")
            .Because(
                "this is the #570 shape: two projects, one name, both projecting the engine. Exactly one "
                + "collision is expected — a matcher that reported the engine's own name, or a partial "
                + "companion, would be reporting something else. Reported: " + Describe(report.Collisions));

        await Assert.That(report.Collisions[0].DeclaringFiles.Count).IsEqualTo(2)
            .Because("the finding must name BOTH files, or a reader cannot tell which two copies collided");

        await Assert.That(report.PerimeterFiles.Count).IsEqualTo(2)
            .Because("both synthetic files project the engine, so both are inside the surface; a perimeter "
                   + "predicate that missed one would make the rule vacuous for real code too");

        // --- The decoys. Each rides through the SAME probe, so a matcher that
        // --- stopped matching shows up as an extra finding rather than a silent pass.
        List<(string Relative, string[] Lines)> decoys = new()
        {
            // (3) A same-named type in a file that does NOT project the engine.
            // Duplication outside the diff surface is a different question, and
            // this rule must not answer it.
            ("src/Harbor.Desktop.Shared/ViewModels/DiffViewModel.cs",
            [
                "namespace Harbor.Desktop.Shared.ViewModels;",
                string.Empty,
                "public sealed class DiffViewModel",
                "{",
                "}",
            ]),
            // (4) A partial type split across two files of ONE project. Legal C#,
            // one implementation, not a collision.
            ("src/Harbor.Desktop.Abstractions/ViewModels/DiffViewModel.Rows.cs",
            [
                "using Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public sealed partial class DiffViewModel",
                "{",
                "    public int Count => LineDiff.Compute(Before, After).Count;",
                "}",
            ]),
            // (5) A name that merely CONTAINS the duplicated one. Different type.
            ("src/Harbor.Ui.Framework.ViewModels/ViewModels/DiffViewModelCache.cs",
            [
                "using Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "namespace Harbor.Ui.Framework.ViewModels;",
                string.Empty,
                "public sealed class DiffViewModelCache",
                "{",
                "    public int Size => LineDiff.Compute(Before, After).Count;",
                "}",
            ]),
            // (6) A file that only NAMES the engine in its XML docs. The #570
            // copies are full of `<see cref="LineDiff" />`, so a perimeter keyed
            // on the bare word would sweep in prose.
            ("src/Harbor.Desktop.Abstractions/ViewModels/ProseOnly.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "/// <summary>Reads <see cref=\"LineDiff\" /> and LineDiff.Compute for the diff.</summary>",
                "public sealed class ProseOnly",
                "{",
                "    // TODO: consider LineDiff.ToUnifiedText here one day.",
                "}",
            ]),
        };

        foreach (var decoy in decoys)
        {
            DiffSurfaceReport decoyReport = DiffSurfaceNameCollisionProbe.ScanFiles([decoy], engineFilePresent: true);
            await Assert.That(decoyReport.Collisions).IsEmpty()
                .Because(
                    decoy.Relative + " must not be reported. A file outside the diff surface is out of scope, "
                    + "a partial type in one project is one implementation, a longer name is a different "
                    + "type, and prose mentioning the engine is not a projection. Reported: "
                    + Describe(decoyReport.Collisions));
        }
    }

    private static string Describe(IReadOnlyList<DiffNameCollision> collisions) =>
        collisions.Count == 0
            ? "(nothing)"
            : string.Join(" | ", collisions.Select(c => c.Name + " in " + string.Join(" + ", c.DeclaringFiles)));

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "(nothing)" : string.Join(" | ", paths.OrderBy(p => p, StringComparer.Ordinal));
}
