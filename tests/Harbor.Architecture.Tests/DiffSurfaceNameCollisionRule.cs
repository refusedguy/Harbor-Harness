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
// SECOND PERIMETER — THE VOCABULARY LAYER (#803)
// ----------------------------------------------
// Rule 1 above is blind to a whole class of the same defect, and #803 is the
// proof. It found two enums named `DiffLineKind` in two assemblies with
// INCOMPATIBLE members — `Add`/`Delete` against `Added`/`Removed` — so
// `DiffLineKind.Add` compiles in one and does not exist in the other, and the
// compiler points at the wrong file. Neither file projects `LineDiff`, so
// neither is inside the projection perimeter and rule 1 stayed silent. That
// silence was correct scoping and still lost the pair.
//
// What was missing is that a diff surface has TWO layers, and rule 1 graded
// only one of them:
//
//   projections  files that CALL the engine — a view of a diff, and rule 1's
//                perimeter. Every one of them answers "what changed?".
//   vocabulary   files that SPEAK the diff — a kind enum and the type that
//                carries it. The two `DiffLineKind`s are both here, and
//                neither is a projection of anything.
//
// The vocabulary layer is derived, not named, and it is anchored in the
// PROJECT that owns the engine — never in a type name and never in a file
// list. The anchor holds a recognisable pattern: a kind enum declared next to a
// type that carries it, which is what a row vocabulary is. The engine's own
// project declares four of them (`LineDiffRowKind`/`LineDiffRow`,
// `SideBySideRowKind`/`SideBySideDiffRow`, `DiffLineKind`/`DiffLine`,
// `WordSegKind`/`WordSeg`). Those names, read out of the source at scan time,
// are the vocabulary. Then: NO OTHER PROJECT MAY DECLARE ONE OF THEM.
//
// Two properties make this more than a rule about a string, and both are the
// point:
//   * Renaming the engine's enum re-derives the set, so the rule is not
//     satisfied by the same edit that re-creates the hazard — the objection
//     this file's own header raises against naming `DiffViewModel`.
//   * The fossil coming BACK trips it, which a "these two names must differ"
//     rule would not: #803's fix is a rename on one side, and the copy that was
//     renamed can be re-declared under its old name later with nothing to stop
//     it.
//
// THIRD PERIMETER — THE PRODUCERS (#570's pair, one level down)
// ----------------------------------------------------------
// Rules 1 and 2 are both blind to the pair that is still standing on `dev`, and
// they are blind for the same reason rule 1 was blind to #803: the perimeter is
// "this file CALLS the engine", and the two `DiffPreview` classes do not.
//
//   src/Harbor.Ui.Framework.State/Diff/DiffPreview.cs            HARBOR.Ui.Framework.State
//   src/Harbor.Tui.CellForge.Engine/Rendering/DiffPreview.cs    Harbor.Tui.CellForge.Rendering
//
// Two classes, one simple name, two assemblies, and a third copy of the same
// index-alignment walk — byte-for-byte the same `GenerateContextDiff`, the same
// prefix/suffix scan, the same six constants, verified by normalising both
// bodies and comparing. That is #570 verbatim: the reader who greps
// `DiffPreview` gets two hits and has to know which one the question was about.
//
// It is #570 a second time because #570's own fix did not close this perimeter.
// #679 gave the diff engine ONE home and both `DiffViewModel`s a projection of
// it — but these two were never projections of anything. They each carry their
// OWN `SplitLines` and their OWN alignment, so "everything that shows a diff is
// a projection of LineDiff", the premise both existing rules rest on, is false
// for them. A perimeter keyed on calling the engine cannot see a file that
// re-implements the engine, and a re-implementation is exactly what a duplicate
// is.
//
// What is new here is that the pair does NOT diverge (measured, below), which is
// what makes it deletable rather than a visible bug — but it is dead weight
// either way, and it is inside the surface whose invariant is one name per
// concept.
//
// THE DERIVATION — BY SHAPE, AND THE SHAPE IS THE FORMAT
// ------------------------------------------------------
// A producer is a file that WRITES the context-diff block: the `"  "`/`"- "`/
// `"+ "` row prefixes `LineDiff.ToUnifiedText` speaks and `TryParseContextBlock`
// reads. Both `DiffPreview` copies and `EditTool.GenerateContextDiff` match on
// exactly those three literals, and nothing else in `src/`+`apps/` does —
// `QuestionFormView` emits `"  "` alone (an indented prompt, not a row), and the
// CellForge renderers split lines without emitting row prefixes at all. So the
// perimeter is derived, never named, and a producer that is renamed or moved
// stays inside it.
//
// It cannot be satisfied by the same edit that re-creates the hazard: the three
// literals ARE the format, so deleting the format deletes the perimeter, and a
// copy that still writes the format is still in it.
//
// EditTool is inside this perimeter and MUST be tolerated there. It cannot call
// `LineDiff` — `Harbor.Tools.Builtin` references only Abstractions and
// Extensions, so the layering matrix forbids the dependency and its local walk
// is forced, not chosen. What the rule asks of a producer is a unique NAME, not
// a call into the engine, so a forced producer with a unique name is legal and
// stays. Only a name declared by two producers is a finding.
//
// WHY THE ANCHOR IS THE ENGINE'S PROJECT AND NOT THE WHOLE TREE
// -------------------------------------------------------------
// "No project may declare a kind-and-carrier pair twice" is a rule about the
// entire repository, and this repo has 25+ same-named types across assemblies,
// most of them legitimate — `ToastKind` is one name in `Ui.Framework.Services`
// and another in `Desktop.Abstractions`, and both earn it. A rule that
// condemned them would be a different and much larger piece of work wearing
// this one's name, exactly as this file's header already says about a
// repo-wide duplicate-name rule. The diff earns a narrower anchor than that,
// and this file is the diff's guard: a name only has to be unique where two
// vocabularies for one concept would actually be confused, which is the diff.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan walked a checkout, read real files, and found the
//      real projections of the engine. "No violations" is trivially satisfied by
//      a scan that read nothing.
//   2. NonVacuity_Scan_DetectsTheCollisionInSyntheticSources — THE POSITIVE
//      CONTROL for rule 1. The probe is handed the #570 shape item for item
//      (two files, in two different projects, declaring the same name, both
//      calling the engine) and four decoys it must NOT report — including a
//      same-named type in a file that does not project the engine at all, and
//      a name that merely CONTAINS the duplicated one.
//   3. DiffVocabulary_IsDerivedFromTheEngineItself — the vocabulary is
//      non-empty and every name in it is declared in the engine's own project,
//      so rule 2 cannot be satisfied by deriving nothing.
//   4. NonVacuity_Scan_DetectsTheForeignVocabularyInSyntheticSources — THE
//      POSITIVE CONTROL for rule 2, and the #803 pair: a kind+carrier pair in
//      the engine's project, the same kind name re-declared in a second
//      project, and four decoys it must NOT report.
//   5. NonVacuity_Scan_DetectsTheProducerCollisionInSyntheticSources — THE
//      POSITIVE CONTROL for rule 4, and the #570 pair that is still standing:
//      two producers of the context-diff block in two projects behind one name,
//      neither calling the engine, plus decoys — including a forced producer
//      that cannot call the engine by layering, which must NOT be reported.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One duplicated name in the diff surface (#570).</summary>
/// <param name="Name">The duplicated simple type name.</param>
/// <param name="DeclaringFiles">Every file in the perimeter declaring it, sorted.</param>
internal sealed record DiffNameCollision(string Name, IReadOnlyList<string> DeclaringFiles);

/// <summary>A diff-vocabulary name declared outside the project that owns the engine (#803).</summary>
/// <param name="Name">The vocabulary name re-declared away from the engine's project.</param>
/// <param name="DeclaringFile">Where the foreign declaration is, repo-relative.</param>
/// <param name="DeclaringProject">The project that declares it, so the message names an assembly.</param>
/// <param name="OwningFile">The file in the engine's project that owns the vocabulary.</param>
internal sealed record ForeignDiffVocabulary(
    string Name,
    string DeclaringFile,
    string DeclaringProject,
    string OwningFile);

/// <summary>Everything the rules need from one repository scan.</summary>
/// <param name="Collisions">Every rule-1 finding, sorted — a non-empty list is the #570 failure.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
/// <param name="EngineFilePresent">Whether the shared engine itself was found.</param>
/// <param name="PerimeterFiles">Which files project the engine, sorted.</param>
/// <param name="VocabularyNames">
///     The diff vocabulary, read out of the engine's project at scan time: every kind enum that
///     has a carrier type beside it, plus the carriers. A non-empty list is what makes rule 2
///     mean anything.
/// </param>
/// <param name="VocabularyOwners">Which file in the engine's project owns each vocabulary name.</param>
/// <param name="ForeignVocabulary">Every rule-2 finding, sorted — a non-empty list is the #803 failure.</param>
internal sealed record DiffSurfaceReport(
    IReadOnlyList<DiffNameCollision> Collisions,
    int FilesScanned,
    bool EngineFilePresent,
    IReadOnlyList<string> PerimeterFiles,
    IReadOnlyList<string> VocabularyNames,
    IReadOnlyDictionary<string, string> VocabularyOwners,
    IReadOnlyList<ForeignDiffVocabulary> ForeignVocabulary);

/// <summary>
///     Finds type names declared by more than one project inside the diff surface.
/// </summary>
internal static partial class DiffSurfaceNameCollisionProbe
{
    /// <summary>The one home of the diff algorithm. Repo-relative, forward slashes.</summary>
    internal const string EngineFile = "src/Harbor.Ui.Framework.Rendering/Widgets/LineDiff.cs";

    /// <summary>Repository roots walked for declarations. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] SourceRoots = ["src", "apps"];

    /// <summary>
    ///     Directory names never descended into during the scan.
    /// </summary>
    /// <remarks>
    ///     Internal, and the walk below takes a replacement set, because a second rule
    ///     (<c>ContribBoundaryNameRule</c>, #843) needs the SAME prune-don't-filter walk over a
    ///     DIFFERENT root list: it measures the <c>contrib/</c> side of the CI boundary, which is
    ///     exactly what this list prunes. A copied walk is a walk that drifts from the skip list
    ///     above it — the reason <c>SourceScan.cs</c> exists — so the walk is shared and only the
    ///     skip set varies.
    /// </remarks>
    internal static readonly string[] SkippedDirectories =
        [".git", "bin", "obj", "external", ".worktrees", "node_modules", "contrib"];

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static DiffSurfaceReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new DiffSurfaceReport([], 0, false, [], [], new SortedDictionary<string, string>(StringComparer.Ordinal), []);
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

        string enginePath = Path.Combine(repoRoot, EngineFile.Replace('/', Path.DirectorySeparatorChar));
        return ScanFiles(sources, File.Exists(enginePath), enginePath);
    }

    /// <summary>
    ///     Grades already-read files. Exposed so the positive control drives the
    ///     REAL matcher — comment stripping and project resolution included —
    ///     rather than a second implementation of it, which is the only way
    ///     "it can fail" means anything.
    /// </summary>
    internal static DiffSurfaceReport ScanFiles(
        List<(string Relative, string[] Lines)> sources,
        bool engineFilePresent,
        string? engineFilePath = null)
    {
        // name -> the projects that declare it. A name in two projects is a finding.
        var byName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var filesByName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var perimeter = new SortedSet<string>(StringComparer.Ordinal);

        // Which project owns the diff engine. Read from the engine file's own PATH rather
        // than from a constant naming the project directory, so the anchor moves with the
        // engine: relocating LineDiff.cs relocates the vocabulary layer with it.
        string engineProject = ProjectOf(engineFilePath is not null ? MakeRelativeFrom(engineFilePath) : EngineFile);

        // The vocabulary layer, derived from the engine's project in the SAME pass, and the
        // rule-3 findings, which can only be graded once the whole tree has been read: a name
        // is foreign only after the file that owns it has been seen.
        var vocabularyOwners = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var foreign = new List<ForeignDiffVocabulary>();

        foreach (var source in sources)
        {
            string[] clean = SourceCommentStripper.StripAll(source.Lines);
            string project = ProjectOf(source.Relative);

            // Rule 2 derivation. A kind enum WITH a carrier type beside it, declared in the
            // engine's project, is that project's vocabulary: the engine speaks it, and no
            // other project gets to re-declare the same name for a different shape.
            if (string.Equals(project, engineProject, StringComparison.Ordinal))
            {
                foreach ((string kind, IReadOnlyList<string> carriers) in VocabularyPairs(clean))
                {
                    vocabularyOwners[kind] = source.Relative;
                    foreach (string carrier in carriers)
                    {
                        vocabularyOwners[carrier] = source.Relative;
                    }
                }
            }

            // The perimeter predicate: this file is part of the diff surface if it EITHER
            // projects the engine OR produces the context-diff block by hand.
            //
            // It must call the engine to project it, not merely name it in prose — a doc comment
            // saying `<see cref="LineDiff" />` is not a projection, and the XML docs on both
            // #570 copies are full of exactly that. `using` alone is not enough either: both
            // projects import the namespace for other widgets, so the engine's static class is
            // matched on a real member access.
            //
            // Producing the block is the second door, and it exists because the two
            // `DiffPreview` classes take it: they re-implement the alignment instead of
            // calling the engine, so the first predicate alone cannot see them, and a duplicate
            // that re-implements is exactly what a duplicate is. The shape is the FORMAT —
            // the three row prefixes — so a producer is recognised by what it writes, and
            // renaming or moving it does not take it out of the perimeter.
            bool projectsEngine = false;
            foreach (string line in clean)
            {
                if (EngineReference().IsMatch(line))
                {
                    projectsEngine = true;
                    break;
                }
            }

            bool producesBlock = EmitsContextDiffBlock(clean);

            if (!projectsEngine && !producesBlock)
            {
                continue;
            }

            perimeter.Add(source.Relative);

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

        // Rule 3 grading.
        if (engineFilePresent)
        {
            foreach (var source in sources)
            {
                string project = ProjectOf(source.Relative);
                if (string.Equals(project, engineProject, StringComparison.Ordinal))
                {
                    continue;
                }

                string[] clean = SourceCommentStripper.StripAll(source.Lines);

                // The SAME shape test as the engine's side, and this symmetry is the point.
                // A foreign file earns a finding only for a KIND enum it declares beside a
                // type that carries it — the vocabulary shape — not for merely mentioning
                // an owned name. The first version of this rule graded names on the foreign
                // side and shapes on the engine's, and the asymmetry reported a real type
                // that is not in the conversation: apps/Harbor.App.Avalonia's HdsDiffCompact
                // declares `record DiffLine(string Text, IBrush Brush)`, a DISPLAY row that
                // carries no kind at all, and the rule called it a second vocabulary because
                // it shares a word with the engine's parsed row. A guard that fires on
                // unrelated types is a guard the team learns to disable.
                foreach ((string kind, _) in VocabularyPairs(clean))
                {
                    if (vocabularyOwners.TryGetValue(kind, out string? owner))
                    {
                        foreign.Add(new ForeignDiffVocabulary(kind, source.Relative, project, owner));
                    }
                }
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

        // One finding per (name, file) so a project that splits a type across its own partial
        // files is not reported twice, and so the message stays a list of declarations.
        foreign.Sort(static (a, b) =>
        {
            int byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.DeclaringFile, b.DeclaringFile);
        });

        List<string> vocabulary = [.. vocabularyOwners.Keys];
        return new DiffSurfaceReport(
            collisions,
            sources.Count,
            engineFilePresent,
            [.. perimeter],
            vocabulary,
            vocabularyOwners,
            foreign);
    }

    /// <summary>
    ///     The kind-enum-and-carrier pairs a file declares: the diff vocabulary pattern, found
    ///     by SHAPE rather than by name.
    /// </summary>
    /// <remarks>
    ///     A vocabulary is a kind enum plus the type that carries it — <c>LineDiffRowKind</c>
    ///     beside <c>LineDiffRow</c>. Requiring the carrier is what keeps the derivation from
    ///     swallowing every enum in the project: <c>LineKind</c> in the markdown parser is
    ///     matched only in <c>case</c> labels and has no carrier, so it is not a vocabulary,
    ///     and neither is a flag enum that merely happens to be spelled <c>…Kind</c>.
    ///     <para>
    ///         The carrier must be a non-enum type declared in the SAME FILE. That is a
    ///         deliberately tight test — a kind enum consumed across a project is a shared
    ///         contract, not a second vocabulary — and it is why this derivation is safe to
    ///         run over the engine's whole project rather than one file.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<(string Kind, IReadOnlyList<string> Carriers)> VocabularyPairs(string[] clean)
    {
        var pairs = new List<(string, IReadOnlyList<string>)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < clean.Length; i++)
        {
            Match declaration = EnumDeclaration().Match(clean[i]);
            if (!declaration.Success)
            {
                continue;
            }

            string kind = declaration.Groups["name"].Value;
            if (!seen.Add(kind))
            {
                continue;
            }

            var carriers = new List<string>();
            foreach (int j in AdjacentDeclarationLines(clean, i))
            {
                Match carrier = TypeDeclaration().Match(clean[j]);
                if (!carrier.Success
                    || IsEnumDeclaration(clean[j], carrier.Index))
                {
                    continue;
                }

                string name = carrier.Groups["name"].Value;
                if (name == kind || carriers.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                // The carrier must MENTION the kind inside its own declaration, which is the
                // whole claim: a type that does not carry a value of the kind is not part of
                // its vocabulary.
                if (DeclarationMentions(clean, j, kind))
                {
                    carriers.Add(name);
                }
            }

            if (carriers.Count > 0)
            {
                pairs.Add((kind, carriers));
            }
        }

        return pairs;
    }

    /// <summary>
    ///     The line indices a type declared BESIDE the enum on <paramref name="enumLine" />
    ///     could be: the declarations immediately above it, and the first one below its closing
    ///     brace.
    /// </summary>
    /// <remarks>
    ///     Adjacency is the whole test, and it is what makes this derivation safe to run over a
    ///     project rather than a file. A vocabulary is written as a pair —
    ///     <c>enum DiffLineKind</c> then <c>record struct DiffLine(DiffLineKind Kind, …)</c> —
    ///     and a type further down the file is a different concern that merely happens to
    ///     mention the enum. Reading the whole file instead would make every later class that
    ///     uses the kind a "carrier", which in <c>LineDiff.cs</c> would sweep in the static
    ///     engine class itself: a name derived from a derivation bug is worse than no name at
    ///     all, because the guard then holds the wrong set.
    /// </remarks>
    private static IEnumerable<int> AdjacentDeclarationLines(string[] clean, int enumLine)
    {
        // Above: walk back over the enum's own header to the declaration above it.
        int start = enumLine - 1;
        while (start >= 0 && !TypeDeclaration().IsMatch(clean[start]) && start > enumLine - 12)
        {
            start--;
        }

        if (start >= 0 && start != enumLine && TypeDeclaration().IsMatch(clean[start]))
        {
            yield return start;
        }

        // Below: the enum's body is a run of members, so the first declaration after its
        // closing brace is the pair partner.
        for (int i = enumLine + 1; i < clean.Length; i++)
        {
            string line = clean[i];
            if (line.StartsWith('}') || line == "}")
            {
                for (int j = i + 1; j < clean.Length; j++)
                {
                    if (TypeDeclaration().IsMatch(clean[j]))
                    {
                        yield return j;
                        yield break;
                    }
                }

                yield break;
            }

            // An enum with no body — a single-line declaration — is over at the `}` on the
            // same line, which the loop above has already handled, or at the terminator.
            if (line.TrimEnd().EndsWith('}'))
            {
                for (int j = i + 1; j < clean.Length; j++)
                {
                    if (TypeDeclaration().IsMatch(clean[j]))
                    {
                        yield return j;
                        yield break;
                    }
                }

                yield break;
            }
        }
    }

    /// <summary>How far past its declaration line a type is followed while being graded.</summary>
    private const int MaxDeclarationLines = 48;

    /// <summary>
    ///     Whether the type declared on <paramref name="declarationLine" /> mentions
    ///     <paramref name="name" /> inside its own declaration span.
    /// </summary>
    /// <remarks>
    ///     The span is the body where the declaration opens a brace, and the parameter list
    ///     where it does not — which is the shape a positional record struct has, and every
    ///     row type in the engine's project is one. Both stop at the terminator, so a member
    ///     in a LATER type in the same file is not read as part of this one.
    /// </remarks>
    private static bool DeclarationMentions(string[] clean, int declarationLine, string name)
    {
        var span = new StringBuilder();
        int depth = 0;
        bool opened = false;
        int end = Math.Min(clean.Length, declarationLine + MaxDeclarationLines);

        for (int i = declarationLine; i < end; i++)
        {
            span.Append(clean[i]).Append('\n');

            foreach (char c in clean[i])
            {
                if (c == '{')
                {
                    opened = true;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            if (opened && depth <= 0)
            {
                break; // the body closed: this declaration is over
            }

            if (!opened
                && i > declarationLine
                && clean[i].Contains(';', StringComparison.Ordinal))
            {
                break; // a parameter list that was terminated on its own line is over
            }
        }

        return Regex.IsMatch(
            span.ToString(),
            $@"\b{Regex.Escape(name)}\b",
            RegexOptions.CultureInvariant);
    }

    /// <summary>
    ///     A repo-relative form of an absolute path, for the engine file. Falls back to the
    ///     constant when the path does not sit under a recognisable root.
    /// </summary>
    private static string MakeRelativeFrom(string absolutePath)
    {
        // Normalise FIRST, then look for "/src/" in one spelling. Matching on
        // Path.DirectorySeparatorChar alone means a path written with the other
        // separator silently misses, and a silent miss here is not a wrong
        // answer — it is an EMPTY derived layer, which reads exactly like
        // "this tree has no diff vocabulary". The Windows job is where the
        // control's hard-coded "/checkout/src/..." hit that.
        string normalised = absolutePath.Replace('\\', '/');

        const string Marker = "/src/";
        int at = normalised.IndexOf(Marker, StringComparison.Ordinal);

        // The slice starts AT the separator, so it must skip past it: a leading '/'
        // splits into an empty first segment, and ProjectOf would then read the ROOT
        // ("src") as the project — which matches no file, and makes the whole derived
        // layer empty for a reason that has nothing to do with the tree.
        return at < 0 ? normalised : normalised[(at + 1)..];
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
    /// <param name="repoRoot">Checkout to walk. A missing tree yields no files.</param>
    /// <param name="roots">Repository-relative directories to walk.</param>
    /// <param name="pattern">File glob, applied per directory.</param>
    /// <param name="skipped">
    ///     Directory names to prune. Defaults to <see cref="SkippedDirectories" />; a caller that
    ///     must descend into one of them (<c>contrib/</c>) passes its own set. The skips are
    ///     PRUNED, not filtered, which is the whole point of the hand-rolled walk.
    /// </param>
    internal static IEnumerable<string> EnumerateFiles(
        string repoRoot,
        string[] roots,
        string pattern,
        string[]? skipped = null)
    {
        string[] skipSet = skipped ?? SkippedDirectories;
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
                    if (IsSkippedDirectory(directory, skipSet))
                    {
                        continue;
                    }

                    pending.Push(directory);
                }
            }
        }
    }

    /// <summary>Whether a directory's NAME is one this walk never descends into.</summary>
    private static bool IsSkippedDirectory(string absolutePath, string[] skipSet)
    {
        string name = Path.GetFileName(absolutePath);
        return skipSet.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    ///     A real member access on the engine's static class. The trailing dot is
    ///     what separates <c>LineDiff.Compute</c> from a type named
    ///     <c>LineDiffRow</c> or a prose mention of the name.
    /// </summary>
    [GeneratedRegex(@"\bLineDiff\s*\.")]
    private static partial Regex EngineReference();

    /// <summary>
    ///     One row prefix of the context-diff block, matched on its own so a file can be
    ///     checked for ALL THREE rather than any one.
    /// </summary>
    /// <remarks>
    ///     The three prefixes are the format itself, which is why this is a shape and not a
    ///     name: <c>Append("  ")</c>, <c>Append("- ")</c> and <c>Append("+ ")</c> are what
    ///     <c>LineDiff.ToUnifiedText</c> writes and <c>TryParseContextBlock</c> reads, and a
    ///     producer is a file that writes them. Matching any ONE of them would sweep in every
    ///     widget that indents a line, so <see cref="EmitsContextDiffBlock" /> requires all
    ///     three before it believes a file.
    /// </remarks>
    [GeneratedRegex(@"Append\(\s*""(?:  |- |\+ )""\s*\)")]
    private static partial Regex ContextRowPrefix();

    /// <summary>
    ///     Whether a file writes the whole context-diff block — all three row prefixes, in any
    ///     order and on any lines.
    /// </summary>
    /// <remarks>
    ///     Deliberately not "at least one prefix". <c>QuestionFormView</c> emits <c>"  "</c> for
    ///     an indented prompt and the CellForge renderers split diff text without writing row
    ///     prefixes at all; neither is a producer, and a predicate loose enough to include them
    ///     would be a perimeter wide enough to condemn unrelated widgets — the failure this
    ///     file's own header already records for rule 3.
    /// </remarks>
    private static bool EmitsContextDiffBlock(string[] clean)
    {
        bool context = false;
        bool removed = false;
        bool added = false;

        foreach (string line in clean)
        {
            if (!context)
            {
                context = ContextRowPrefix().IsMatch(line) && line.Contains("\"  \"", StringComparison.Ordinal);
            }

            if (!removed)
            {
                removed = line.Contains("\"- \"", StringComparison.Ordinal);
            }

            if (!added)
            {
                added = line.Contains("\"+ \"", StringComparison.Ordinal);
            }

            if (context && removed && added)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     A type declaration's keyword and name, at any modifier depth. Records,
    ///     structs and enums count: the rule is about a name, not about a keyword.
    /// </summary>
    [GeneratedRegex(@"\b(?:class|struct|record|interface|enum)\s+(?<name>\w+)")]
    private static partial Regex TypeKeyword();

    /// <summary>
    ///     A type declaration with its keyword kept, because the vocabulary derivation needs
    ///     to tell an enum from the class beside it.
    /// </summary>
    /// <remarks>
    ///     <c>record struct</c> and <c>record class</c> are TWO keywords, and getting that
    ///     wrong is not cosmetic: a naive alternation matches <c>record</c> first and captures
    ///     <c>struct</c> as the type's NAME, so the derivation would hold a vocabulary called
    ///     "struct" and miss every row type in the tree. The optional second keyword is
    ///     consumed as part of the declaration rather than as its name.
    /// </remarks>
    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record)\s+(?:(?:struct|class)\s+)?(?<name>\w+)")]
    private static partial Regex TypeDeclaration();

    /// <summary>An enum declaration — the left half of a vocabulary pair.</summary>
    [GeneratedRegex(@"\benum\s+(?<name>\w+)")]
    private static partial Regex EnumDeclaration();

    /// <summary>
    ///     Whether the declaration matched at <paramref name="at" /> is an enum. Asked
    ///     separately because <see cref="TypeDeclaration" /> deliberately drops the keyword
    ///     to swallow <c>record struct</c>, so the match text cannot be inspected for it.
    /// </summary>
    private static bool IsEnumDeclaration(string line, int at) =>
        line.AsSpan(0, at).TrimEnd().EndsWith("enum", StringComparison.Ordinal);

    /// <summary>
    ///     Repo-relative, forward-slashed form of a walked file. Internal because
    ///     <c>ContribBoundaryNameRule</c> walks the same tree with a different skip set and has to
    ///     label what it reads the same way, or the two rules would report the same file under two
    ///     spellings.
    /// </summary>
    internal static string MakeRelative(string repoRoot, string path) =>
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
    ///     <para>
    ///         The perimeter has two doors — a file that CALLS the engine, and a file that
    ///         WRITES the diff block by hand — because the pair that was still standing when
    ///         this rule was written did not call the engine. See the header: the second door
    ///         is <c>#570</c> one level down, and it is the door the first two rules could not see
    ///         through.
    ///     </para>
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
                + "because it exercised both. A producer that re-implements the alignment instead of "
                + "calling the engine is the same hazard one level down. Found: "
                + Describe(collisions));
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
    // 2b. Rule 1's blind spot, closed — the vocabulary layer (#803).
    // =====================================================================

    /// <summary>
    ///     Rule 3 (#803): the diff vocabulary belongs to the project that owns the engine, and
    ///     no other project may declare one of its names.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #803 is the pair rule 1 could not see. Two enums named <c>DiffLineKind</c> in
    ///         two assemblies, with <b>incompatible members</b> — <c>Add</c>/<c>Delete</c>
    ///         against <c>Added</c>/<c>Removed</c> — so <c>DiffLineKind.Add</c> compiles in one
    ///         assembly and does not exist in the other, and the error names the wrong file.
    ///     </para>
    ///     <para>
    ///         Rule 1 stayed silent because neither file projects <c>LineDiff</c>: they are not
    ///         views of a diff, they are the words a diff is spoken in. A projection perimeter
    ///         cannot see a vocabulary, so the vocabulary gets its own rule over its own
    ///         derived layer — the kind enums and their carriers, read out of the engine's own
    ///         project at scan time.
    ///     </para>
    ///     <para>
    ///         BOTH sides of the comparison are graded by SHAPE, and that symmetry is not
    ///         tidiness. Grading the engine's side by shape and the foreign side by bare name
    ///         reports every type that happens to share a word with the engine's vocabulary,
    ///         and the tree has one: <c>apps/Harbor.App.Avalonia</c>'s
    ///         <c>HdsDiffCompact</c> declares <c>record DiffLine(string Text, IBrush Brush)</c>
    ///         — a display row for a compact summary widget, with no kind in it. It is not a
    ///         second vocabulary, and a rule that says it is gets switched off.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task DiffVocabulary_IsOwnedByTheEngineProject()
    {
        var foreign = Report.Value.ForeignVocabulary;

        await Assert.That(foreign).IsEmpty()
            .Because(
                "the diff vocabulary — the kind enum and the type that carries it — is the engine's to "
                + "name, and the engine's project already names two vocabularies that are genuinely "
                + "different concerns: LineDiffRowKind (a computed row) and DiffLineKind (a line of a "
                + "unified diff document, hunk and file headers included). A second project declaring "
                + "one of those names is declaring a SECOND vocabulary for one concept, which is the "
                + "trap #803 reports: the name resolves, the members do not, and the compiler points "
                + "at the other file. Found: " + Describe(foreign));
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

        await Assert.That(report.VocabularyNames).IsNotEmpty()
            .Because(
                "rule 3 is graded against a vocabulary derived from the project that owns "
                + DiffSurfaceNameCollisionProbe.EngineFile + ". A derivation that finds nothing makes every "
                + "foreign declaration undetectable, so \"nothing is foreign\" would be vacuous. Derived: "
                + Describe(report.VocabularyNames));

        await Assert.That(report.VocabularyNames.OrderBy(n => n, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(report.VocabularyOwners.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray())
            .Because(
                "the vocabulary names and the vocabulary owners are the same set by construction, and a "
                + "mismatch means the grading step is reading a different list than the derivation "
                + "produced — which is a guard that reports against names it never derived");
    }

    /// <summary>
    ///     THE ENGINE'S OWN FILE IS IN THE VOCABULARY LAYER, WITH ITS CARRIERS.
    /// </summary>
    /// <remarks>
    ///     The derivation keys on a SHAPE — a kind enum declared beside a type that carries it
    ///     — and a shape is only as good as the evidence that it fires on the real thing. This
    ///     pins that evidence to the engine's own declarations, so a matcher that quietly
    ///     stopped finding carriers would be caught here rather than by a rule that reports
    ///     nothing forever. It also pins the claim in rule 3's message: the two vocabularies
    ///     the engine's project really does own, one for a computed row and one for a line of
    ///     a unified diff document.
    /// </remarks>
    [Test]
    public async Task DiffVocabulary_IsDerivedFromTheEngineFileItself()
    {
        var report = Report.Value;
        var engine = DiffSurfaceNameCollisionProbe.EngineFile;
        var engineProject = DiffSurfaceNameCollisionProbe.ProjectOf(engine);

        await Assert.That(report.VocabularyOwners.TryGetValue("LineDiffRowKind", out string? kindOwner))
            .IsTrue()
            .Because(
                "LineDiffRowKind sits beside LineDiffRow in " + engine + ", which is the kind-and-carrier "
                + "shape the vocabulary derivation looks for. If the engine's own vocabulary is not in "
                + "the derived set, the shape does not fire on the case it was written for and rule 3 is "
                + "grading an empty set. Derived: " + Describe(report.VocabularyNames));

        await Assert.That(kindOwner).IsEqualTo(engine)
            .Because("the engine's vocabulary is owned by the engine's own file, which is what makes the "
                   + "project it belongs to the owner of the vocabulary");

        await Assert.That(report.VocabularyOwners.TryGetValue("LineDiffRow", out _))
            .IsTrue()
            .Because(
                "the carrier is half the pattern: LineDiffRow is the type that carries a LineDiffRowKind, "
                + "and a vocabulary that names only its enum would leave a second project's own row type "
                + "free to reuse the same name");

        // Every owner must sit in the engine's project. Graded as a bool list because the
        // assertion is about EVERY entry, and a predicate over the values says that directly
        // where a per-entry loop would only say it one name at a time.
        await Assert.That(report.VocabularyOwners.Values
                .Select(v => DiffSurfaceNameCollisionProbe.ProjectOf(v) == engineProject)
                .ToArray())
            .IsEquivalentTo(report.VocabularyOwners.Values.Select(_ => true).ToArray())
            .Because(
                "every vocabulary name is derived from the engine's project and owned by a file in it. A "
                + "name owned by some other project would mean the layer leaked outwards, and the owner "
                + "of a vocabulary is precisely what rule 3 exists to keep singular. Owners: "
                + string.Join(" | ", report.VocabularyOwners.Select(o => o.Key + " -> " + o.Value)));

        await Assert.That(report.VocabularyNames.Contains("DiffLineKind", StringComparer.Ordinal))
            .IsTrue()
            .Because(
                "DiffLineKind beside DiffLine in src/Harbor.Ui.Framework.Rendering/Widgets/DiffBlock.cs is "
                + "the #803 name on the engine's side — the unified-diff DOCUMENT vocabulary, which carries "
                + "HunkHeader and FileHeader and therefore cannot be merged with a computed row. It is the "
                + "name rule 3 protects, so the derivation has to find it. Derived: "
                + Describe(report.VocabularyNames));
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
            // (1) The side-by-side projection, calling the engine — under its
            // PRE-#570 name on purpose. The tree no longer looks like this (the
            // fix renamed it to SideBySideDiffViewModel), so the control uses the
            // shape #570 reported to prove the matcher still catches it after the
            // rename. A control written against the post-fix names would pass
            // against a matcher that had learned the new name instead of the
            // rule, which is the one thing this test must not do.
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

    /// <summary>
    ///     THE POSITIVE CONTROL FOR RULE 3. The probe is handed the #803 pair — a kind+carrier
    ///     vocabulary in the engine's project, and the same kind name re-declared in a second
    ///     project — plus five decoys it must NOT report.
    /// </summary>
    /// <remarks>
    ///     A control written against the names this rule forbids would pass against a matcher
    ///     that had learned those names instead of the shape, which is the one thing it must not
    ///     do. So every source below is synthetic and the vocabulary is called
    ///     <c>SyntheticRowKind</c>, which appears nowhere in the tree.
    /// </remarks>
    [Test]
    public async Task NonVacuity_Scan_DetectsTheForeignVocabularyInSyntheticSources()
    {
        List<(string Relative, string[] Lines)> pair = new()
        {
            // (1) The engine's own project, declaring a kind beside its carrier — the shape the
            // derivation looks for, and the anchor the whole layer hangs off.
            ("src/Harbor.Ui.Framework.Rendering/Widgets/SyntheticDiff.cs",
            [
                "namespace Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "public enum SyntheticRowKind : byte",
                "{",
                "    Same,",
                "    Added,",
                "    Removed,",
                "}",
                string.Empty,
                "public readonly record struct SyntheticRow(SyntheticRowKind Kind, int OldNo, string Text);",
            ]),
            // (2) #803 verbatim: the same kind name, in a different project, with a member set
            // that does not match. Nothing here calls the engine, so rule 1 cannot see it — which
            // is exactly why rule 3 exists.
            ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticData.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public enum SyntheticRowKind",
                "{",
                "    Context,",
                "    Added,",
                "    Removed",
                "}",
                string.Empty,
                "public class SyntheticRowVm",
                "{",
                "    public SyntheticRowKind Kind { get; init; }",
                "}",
            ]),
        };

        // The ABSOLUTE engine path, exactly as Scan passes it, so the control exercises the
        // same anchor resolution production does. A control that omits it falls back to the
        // constant and never touches MakeRelativeFrom — which is how a real bug in the path
        // handling stayed green in this test while the whole derived layer came back empty.
        //
        // Built with Path.Combine rather than written as a literal: MakeRelativeFrom looks
        // for the platform's own separator, so a hard-coded "/checkout/src/..." matches on
        // Linux and misses on Windows. The Windows job is where that showed up — the
        // derivation came back empty there and only there.
        DiffSurfaceReport report = DiffSurfaceNameCollisionProbe.ScanFiles(
            pair,
            engineFilePresent: true,
            engineFilePath: AbsoluteEnginePath("SyntheticDiff.cs"));

        await Assert.That(report.VocabularyNames).IsEquivalentTo(["SyntheticRowKind", "SyntheticRow"])
            .Because(
                "the derivation is by shape, and this is the shape: a kind enum with a type that carries "
                + "it. Getting the enum without the carrier, or the carrier without the enum, means the "
                + "derivation answers a different question than the one rule 3 asks. Derived: "
                + Describe(report.VocabularyNames));

        await Assert.That(string.Join(" | ", report.ForeignVocabulary.Select(f => f.Name)))
            .IsEqualTo("SyntheticRowKind")
            .Because(
                "this is #803: the engine's project owns the name and a second project declares it. Exactly "
                + "one finding is expected — reporting the carrier too would mean a second project's own "
                + "row type was being held to a name it never borrowed. Reported: " + Describe(report.ForeignVocabulary));

        ForeignDiffVocabulary finding = report.ForeignVocabulary[0];
        await Assert.That(finding.DeclaringProject).IsEqualTo("Harbor.Desktop.Abstractions")
            .Because("the finding must name the ASSEMBLY that re-declared the vocabulary, since a type "
                   + "name without an assembly is the whole ambiguity #803 is about");

        await Assert.That(finding.DeclaringFile).IsEqualTo(pair[1].Relative)
            .Because("the finding must point at the file, so a reader can open the declaration that has to "
                   + "change rather than guess which of the two copies is meant");

        await Assert.That(finding.OwningFile).IsEqualTo(pair[0].Relative)
            .Because("the finding must also say who OWNS the name, which is what tells a reader the fix "
                   + "is on the foreign side and the vocabulary is not up for renegotiation");

        // --- The decoys, each through the SAME probe.
        List<(string Relative, string[] Lines)> decoys = new()
        {
            // (3) A kind enum with NO carrier beside it, in the engine's project. A flag enum is
            // not a vocabulary, and if this entered the set it would make every later name
            // un-ownable by a second project for no reason.
            ("src/Harbor.Ui.Framework.Rendering/Input/SyntheticFlags.cs",
            [
                "namespace Harbor.Ui.Framework.Rendering.Input;",
                string.Empty,
                "public enum SyntheticFlag : byte",
                "{",
                "    None,",
                "    Left,",
                "    Right",
                "}",
            ]),
            // (4) A name that merely CONTAINS the owned one. `SyntheticRowKind` inside
            // `SyntheticRowKindExtra` is a different type, and holding it to the owned name
            // would make the rule unfixable without inventing names.
            ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticExtra.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public enum SyntheticRowKindExtra",
                "{",
                "    Context,",
                "    Added",
                "}",
                string.Empty,
                "public sealed class SyntheticRowKindHolder",
                "{",
                "    public SyntheticRowKindExtra Kind { get; init; }",
                "}",
            ]),
            // (5) The owned name declared AGAIN inside the engine's own project, in a second
            // file. One project, one implementation — a partial or a companion type in the
            // owner is not a second vocabulary.
            ("src/Harbor.Ui.Framework.Rendering/Widgets/SyntheticDiff.Parts.cs",
            [
                "namespace Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "public readonly record struct SyntheticRow",
                "{",
                "    public string Label => Kind.ToString();",
                "}",
            ]),
            // (6) A foreign project declaring a kind+carrier pair of its OWN, with a name
            // nobody owns. A project is allowed its own vocabulary; the rule is about taking
            // one that is already spoken, not about having one.
            ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticOwn.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public enum SyntheticBubbleKind",
                "{",
                "    Info,",
                "    Warn",
                "}",
                string.Empty,
                "public sealed class SyntheticBubble(SyntheticBubbleKind Kind, string Text);",
            ]),
            // (7) Prose that NAMES the owned vocabulary without declaring it. The XML docs in
            // this very file are full of `<see cref="SyntheticRowKind" />`, so a matcher that
            // keyed on the bare word would report the documentation as the defect.
            ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticProse.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "/// <summary>Reads <see cref=\"SyntheticRowKind\" /> to pick a brush.</summary>",
                "public sealed class SyntheticProse",
                "{",
                "    // TODO: reuse SyntheticRowKind here one day.",
                "}",
            ]),
            // (8) A type that merely SHARES AN OWNED NAME, carrying no kind at all. This is
            // apps/Harbor.App.Avalonia/Views/Controls/HdsDiffCompact.axaml.cs verbatim in
            // shape: `public sealed record DiffLine(string Text, IBrush Brush)` is a DISPLAY
            // row for a two-line summary widget, while the engine's DiffLine is a PARSED
            // row with a DiffLineKind and two line numbers. One word, two unrelated types.
            // The first version of this rule reported it, which is why the foreign side now
            // gets the same kind-and-carrier test the engine's side does.
            ("apps/Harbor.App.Avalonia/Views/Controls/SyntheticCompact.cs",
            [
                "namespace Harbor.App.Avalonia.Views.Controls;",
                string.Empty,
                "public sealed record SyntheticRow(string Text, IBrush Brush);",
            ]),
        };

        foreach (var decoy in decoys)
        {
            DiffSurfaceReport decoyReport = DiffSurfaceNameCollisionProbe.ScanFiles(
                [pair[0], decoy],
                engineFilePresent: true,
                engineFilePath: AbsoluteEnginePath("SyntheticDiff.cs"));

            await Assert.That(decoyReport.ForeignVocabulary).IsEmpty()
                .Because(
                    decoy.Relative + " must not be reported. A kind enum with no carrier is not a "
                    + "vocabulary, a longer name is a different type, the owner's own second file is one "
                    + "implementation, a project may hold a vocabulary nobody else claims, and prose "
                    + "naming the vocabulary is not a declaration. Reported: "
                    + Describe(decoyReport.ForeignVocabulary));
        }
    }

    /// <summary>
    ///     THE POSITIVE CONTROL FOR RULE 4. The probe is handed the #570 pair that is still
    ///     standing — two producers of the context-diff block, in two projects, behind one name,
    ///     NEITHER calling the engine — plus four decoys it must NOT report.
    /// </summary>
    /// <remarks>
    ///     A control written against the names this rule forbids would pass against a matcher
    ///     that had learned those names instead of the shape. So every source below is
    ///     synthetic and the name is <c>SyntheticPreview</c>, which appears nowhere in the tree.
    /// </remarks>
    [Test]
    public async Task NonVacuity_Scan_DetectsTheProducerCollisionInSyntheticSources()
    {
        List<(string Relative, string[] Lines)> pair = new()
        {
            // (1) A producer: it writes the three row prefixes by hand, in its own project, and
            // it calls NOTHING. This is the shape the engine-call predicate misses by
            // construction — which is why it is the whole point of the control.
            ("src/Harbor.Ui.Framework.State/Diff/SyntheticPreview.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public static class SyntheticPreview",
                "{",
                "    public static string Build(string[] oldLines, string[] newLines)",
                "    {",
                "        var sb = new System.Text.StringBuilder();",
                "        sb.Append(\"  \").AppendLine(oldLines[0]);",
                "        sb.Append(\"- \").AppendLine(oldLines[1]);",
                "        sb.Append(\"+ \").AppendLine(newLines[1]);",
                "        return sb.ToString();",
                "    }",
                "}",
            ]),
            // (2) The same name, a DIFFERENT project, producing the block the same way. Two
            // assemblies, one simple name, no engine call anywhere: exactly the pair that
            // survives on dev and that rules 1-3 cannot see.
            ("src/Harbor.Tui.CellForge.Engine/Rendering/SyntheticPreview.cs",
            [
                "namespace Harbor.Tui.CellForge.Rendering;",
                string.Empty,
                "public static class SyntheticPreview",
                "{",
                "    public static string Build(string[] oldLines, string[] newLines)",
                "    {",
                "        var sb = new System.Text.StringBuilder();",
                "        sb.Append(\"  \").AppendLine(oldLines[0]);",
                "        sb.Append(\"- \").AppendLine(oldLines[1]);",
                "        sb.Append(\"+ \").AppendLine(newLines[1]);",
                "        return sb.ToString();",
                "    }",
                "}",
            ]),
        };

        DiffSurfaceReport report = DiffSurfaceNameCollisionProbe.ScanFiles(pair, engineFilePresent: true);

        await Assert.That(string.Join(" | ", report.Collisions.Select(c => c.Name)))
            .IsEqualTo("SyntheticPreview")
            .Because(
                "this is the #570 pair one level down: two producers of the context-diff block, in two "
                + "projects, behind one name, and NEITHER projects LineDiff — so a perimeter keyed on "
                + "calling the engine returns an empty set here and the duplication is invisible. "
                + "Exactly one collision is expected. Reported: " + Describe(report.Collisions));

        await Assert.That(report.Collisions[0].DeclaringFiles.Count).IsEqualTo(2)
            .Because("the finding must name BOTH producers, or a reader cannot tell which pair collided");

        await Assert.That(report.PerimeterFiles.Count).IsEqualTo(2)
            .Because("both synthetic files write the block, so both are inside the surface; a perimeter "
                   + "that admitted one and not the other would be satisfied by deleting half a pair");

        // --- The decoys, each through the SAME probe, so a matcher that stopped matching shows
        // --- up as an extra finding rather than as a silent pass.
        List<(string Relative, string[] Lines)> decoys = new()
        {
            // (3) THE FORCED PRODUCER, verbatim in shape. This is EditTool: it writes the block,
            // it calls nothing, and it CANNOT call the engine because Harbor.Tools.Builtin
            // references only Abstractions and Extensions. It has a unique name, so it is legal
            // and must not be reported — the rule asks a producer for a unique NAME, not for a
            // call into the engine. A perimeter that condemned this would make the rule
            // unfixable without a layering change, which is a different decision entirely.
            ("src/Harbor.Tools.Builtin/Tools/Synthetic/SyntheticEdit.cs",
            [
                "namespace Harbor.Tools.Builtin.Tools.Synthetic;",
                string.Empty,
                "public sealed class SyntheticEdit",
                "{",
                "    private static string GenerateContextDiff(string a, string b, int max)",
                "    {",
                "        var sb = new System.Text.StringBuilder();",
                "        sb.Append(\"  \").AppendLine(a);",
                "        sb.Append(\"- \").AppendLine(a);",
                "        sb.Append(\"+ \").AppendLine(b);",
                "        return sb.ToString();",
                "    }",
                "}",
            ]),
            // (4) A file that emits ONE prefix. This is QuestionFormView's shape: an indented
            // prompt is written as `"  "` and is not a diff row. Half the widgets in this repo
            // indent something.
            ("src/Harbor.Ui.Framework.Rendering/Widgets/SyntheticIndented.cs",
            [
                "namespace Harbor.Ui.Framework.Rendering.Widgets;",
                string.Empty,
                "public sealed class SyntheticIndented",
                "{",
                "    public string Prompt => sb.Append(\"  \").AppendLine(text).ToString();",
                "}",
            ]),
            // (5) A same-named type in a file that is NEITHER a projection NOR a producer. A
            // duplicate name outside the diff surface is a different question and this rule must
            // not answer it.
            ("src/Harbor.Desktop.Shared/ViewModels/SyntheticPreview.cs",
            [
                "namespace Harbor.Desktop.Shared.ViewModels;",
                string.Empty,
                "public sealed class SyntheticPreview",
                "{",
                "}",
            ]),
            // (6) A producer whose name merely CONTAINS the duplicated one. A different type, and
            // holding it to the other name would make the rule unfixable without inventing names.
            ("src/Harbor.Ui.Framework.State/Diff/SyntheticPreviewCache.cs",
            [
                "namespace Harbor.Ui.Framework.State;",
                string.Empty,
                "public static class SyntheticPreviewCache",
                "{",
                "    public static string Build(string[] a, string[] b)",
                "    {",
                "        var sb = new System.Text.StringBuilder();",
                "        sb.Append(\"  \").AppendLine(a[0]);",
                "        sb.Append(\"- \").AppendLine(a[1]);",
                "        sb.Append(\"+ \").AppendLine(b[1]);",
                "        return sb.ToString();",
                "    }",
                "}",
            ]),
        };

        foreach (var decoy in decoys)
        {
            DiffSurfaceReport decoyReport = DiffSurfaceNameCollisionProbe.ScanFiles(
                [pair[0], decoy],
                engineFilePresent: true);
            await Assert.That(decoyReport.Collisions).IsEmpty()
                .Because(
                    decoy.Relative + " must not be reported. A producer with a unique name is legal "
                    + "however it computes its rows — including one that cannot reach the engine by "
                    + "layering — one row prefix is an indent, a duplicate name outside the diff "
                    + "surface is another question, and a longer name is a different type. Reported: "
                    + Describe(decoyReport.Collisions));
        }
    }

    /// <summary>
    ///     An absolute engine-file path in the shape the live scan produces on the running
    ///     platform, so a control driving the anchor resolution is not quietly testing a
    ///     different platform's separators.
    /// </summary>
    private static string AbsoluteEnginePath(string fileName) => Path.Combine(
        Path.GetTempPath(),
        "checkout",
        "src",
        "Harbor.Ui.Framework.Rendering",
        "Widgets",
        fileName);

    private static string Describe(IReadOnlyList<DiffNameCollision> collisions) =>
        collisions.Count == 0
            ? "(nothing)"
            : string.Join(" | ", collisions.Select(c => c.Name + " in " + string.Join(" + ", c.DeclaringFiles)));

    private static string Describe(IReadOnlyList<ForeignDiffVocabulary> foreign) =>
        foreign.Count == 0
            ? "(nothing)"
            : string.Join(
                " | ",
                foreign.Select(f => f.Name + " declared by " + f.DeclaringProject + " in " + f.DeclaringFile
                                        + ", owned by " + f.OwningFile));

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "(nothing)" : string.Join(" | ", paths.OrderBy(p => p, StringComparer.Ordinal));
}
