// ContribBoundaryNameRule.cs — a MEASUREMENT, not a prohibition: how many simple
// type names are declared on BOTH sides of the boundary CI does not build.
//
// THE HOLE
// --------
// `contrib/` is not in Harbor.slnx, no `ProjectReference` from `apps/` or `src/`
// reaches it, and no workflow builds it. AGENTS.md says so outright, and the
// codebase is built that way on purpose.
//
// The consequence is that EVERY repository-scanning gate prunes it, and the
// pruning is correct: a rule that fires on `contrib/` describes code that cannot
// break. `SourceScan.IsBuildOutput` rejects `/contrib/`, and
// `DiffSurfaceNameCollisionProbe.SkippedDirectories` prunes it by name. Two
// nights in a row this came up as a live hazard — #803's `DiffLineKind` had
// exactly one caller, in `contrib/apps/Harbor.App.Wpf`, and #844 found a
// `## Public API` section claiming types no tree declares — and both times the
// thing that noticed was a person reading a diff, not a gate.
//
// So nothing in this repository MEASURES what accumulates on the far side of
// that boundary. This file is that measurement, and it is deliberately the
// narrowest possible question: not "is this duplication a bug" (see below), but
// "how many names exist on both sides".
//
// WHY IT CONDEMNS NOTHING
// -----------------------
// #843 is the worked example, and the reason this is a ratchet and not a rule.
// `apps/Harbor.App.Avalonia` declares `record DiffLine(string Text, IBrush
// Brush)` — a display row for a compact summary widget, carrying no kind — while
// `Harbor.Ui.Framework.Rendering` declares `readonly record struct DiffLine(
// DiffLineKind Kind, int OldNo, int NewNo, string Text)`, a parsed row. One word,
// two unrelated types, both live, both correct, unambiguous at every call site.
//
// It is worth being precise about which class that is, because the three
// available verdicts lead to different work. It is NOT "one redundant copy" —
// both are wired (`HdsDiffCompact` is bound from `ToolCallCardView.axaml`, and
// `UnifiedDiffParser.Parse` returns the engine's), so nothing here is dead. It is
// NOT "two implementations of one concept", which is what #805's `DiffViewModel`
// pair was and why exactly one of those two got renamed. They are different
// ASSEMBLIES whose reference closures overlap — `Harbor.App.Avalonia` reaches
// `Harbor.Ui.Framework.Rendering` transitively through `Harbor.Ui.Framework` ->
// `Harbor.Ui.Framework.State` — so the simple name is ambiguous IN PRINCIPLE,
// and not in fact: no file in the tree imports both namespaces and writes
// `DiffLine` unqualified.
//
// A guard that reported this would be reporting a legitimate name, and a guard
// that reports legitimate names gets switched off. So this rule reports the
// COUNT and pins it, and leaves every individual crossing to a human. `DiffLine`
// is not even in this rule's set: both declarations are inside the product
// perimeter, so there is no crossing to measure. Its verdict is pinned where it
// belongs — decoy (8) of `DiffSurfaceNameCollisionRule`'s rule-3 positive
// control, in its real shape, so a matcher that regressed to grading the
// foreign side by name instead of by shape fails that control.
//
// THE FLOOR
// ---------
// The count is pinned, not required to be zero. `contrib/` is out of CI by owner
// decision, so the honest invariant is "this hole may not GROW", and growing it is
// the one thing a reader can act on without an owner decision. The baseline is
// the full list of names, not a count alone, so a deletion on one side cannot be
// masked by an addition on the other.
//
// WHAT MUST NOT HAPPEN HERE
// -------------------------
// This is NOT a repo-wide duplicate-name rule wearing a new name, and it is NOT a
// second copy of #844. #844's perimeter is `samples/plugins/*/README.md` and the
// `## Public API` section inside it — phantom NAMES in prose, checked by
// `tools/check-doc-cites.py`. This perimeter is a type DECLARATION in a .cs file,
// and the question is whether the same simple name is declared in two places
// across one specific boundary. Different input, different question.
//
// Nor is it a generalisation of `DiffSurfaceNameCollisionRule`. That rule's header
// already refuses a repo-wide duplicate-name rule on the record ("the repo has
// 25+ same-named types across assemblies, most of them legitimate"), and this
// file does not overturn that: it counts, and #843 is the standing proof that a
// counted name is usually fine.
//
// NON-VACUITY
// -----------
//   1. Contrib_IsActuallyRead — the load-bearing one. `SourceScan.EnumerateCsFiles`
//      cannot be used on the `contrib/` side: `IsBuildOutput` rejects `/contrib/`,
//      so it returns an EMPTY list, and "no crossings" would then be
//      indistinguishable from "read nothing". This file walks with the shared
//      prune-don't-filter walk and a skip set that omits `contrib`, and asserts
//      both sides came back non-empty. A silent miss here is not a wrong answer,
//      it is a CLEAN report about a tree that was never opened.
//   2. CrossBoundaryNames_MatchTheMeasuredBaseline — the ratchet, and it
//      enumerates every live crossing when it fails, which is what makes the CI
//      log the measurement.
//   3. NonVacuity_DetectsACrossBoundaryCollisionInSyntheticSources — the positive
//      control, driving the REAL matcher plus three decoys and one multi-declaration
//      case. It also pins the definition of a crossing, which two earlier drafts of it
//      got backwards: a crossing is "at least one declaration on each side", NOT
//      "exactly one on each side", so a partial split inside contrib/ and a name
//      present in two contrib projects are both crossings, not exemptions.

namespace Harbor.Architecture.Tests;

/// <summary>One simple type name declared on both sides of the CI boundary.</summary>
/// <param name="Name">The duplicated simple type name.</param>
/// <param name="ProductFiles">Where <c>src/</c>+<c>apps/</c> declare it, sorted.</param>
/// <param name="ContribFiles">Where <c>contrib/</c> declares it, sorted.</param>
internal sealed record CrossBoundaryName(
    string Name,
    IReadOnlyList<string> ProductFiles,
    IReadOnlyList<string> ContribFiles);

/// <summary>Everything this measurement needs from one repository scan.</summary>
/// <param name="Crossings">Every name declared on both sides, sorted by name.</param>
/// <param name="ProductFilesRead">How many <c>.cs</c> files the product side contributed.</param>
/// <param name="ContribFilesRead">How many <c>.cs</c> files the <c>contrib/</c> side contributed.</param>
internal sealed record CrossBoundaryReport(
    IReadOnlyList<CrossBoundaryName> Crossings,
    int ProductFilesRead,
    int ContribFilesRead);

/// <summary>Counts the simple type names declared on both sides of the CI boundary.</summary>
internal static class ContribBoundaryNameProbe
{
    /// <summary>All three trees, in one walk. The boundary is the FIRST path segment.</summary>
    private static readonly string[] Roots = ["src", "apps", "contrib"];

    /// <summary>
    ///     The shared skip set with <c>contrib</c> taken back out. Everything else stays:
    ///     descending into <c>.git</c>, <c>bin/</c>, <c>obj/</c> and a nested <c>.worktrees/</c>
    ///     costs a full second copy of the repository on every run and, for <c>.worktrees/</c>,
    ///     would report against code the caller is not editing.
    /// </summary>
    private static readonly string[] Skipped =
        [.. DiffSurfaceNameCollisionProbe.SkippedDirectories.Where(d => !string.Equals(d, "contrib", StringComparison.Ordinal))];

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports.</summary>
    internal static CrossBoundaryReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new CrossBoundaryReport([], 0, 0);
        }

        var sources = new List<(string Relative, string[] Lines)>();
        foreach (string file in DiffSurfaceNameCollisionProbe.EnumerateFiles(repoRoot, Roots, "*.cs", Skipped))
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

            sources.Add((DiffSurfaceNameCollisionProbe.MakeRelative(repoRoot, file), lines));
        }

        return ScanFiles(sources);
    }

    /// <summary>
    ///     Grades already-read files. Exposed so the positive control drives the REAL matcher —
    ///     comment stripping included — rather than a second implementation of it, which is the
    ///     only way "it can fail" means anything.
    /// </summary>
    internal static CrossBoundaryReport ScanFiles(List<(string Relative, string[] Lines)> sources)
    {
        var product = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var contrib = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        int productFiles = 0;
        int contribFiles = 0;

        foreach (var source in sources)
        {
            bool isContrib = IsContrib(source.Relative);
            if (isContrib)
            {
                contribFiles++;
            }
            else
            {
                productFiles++;
            }

            // The REAL declaration matcher, on comment-stripped source: a name in an XML doc
            // is not a declaration, and this tree's docs quote each other's types constantly.
            Dictionary<string, SortedSet<string>> side = isContrib ? contrib : product;
            foreach (string name in DiffSurfaceNameCollisionProbe.DeclaredTypeNames(
                         SourceCommentStripper.StripAll(source.Lines)))
            {
                if (!side.TryGetValue(name, out SortedSet<string>? files))
                {
                    files = new SortedSet<string>(StringComparer.Ordinal);
                    side[name] = files;
                }

                files.Add(source.Relative);
            }
        }

        var crossings = new List<CrossBoundaryName>();
        foreach ((string name, SortedSet<string> files) in product)
        {
            if (contrib.TryGetValue(name, out SortedSet<string>? other))
            {
                crossings.Add(new CrossBoundaryName(name, [.. files], [.. other]));
            }
        }

        crossings.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new CrossBoundaryReport(crossings, productFiles, contribFiles);
    }

    /// <summary>
    ///     Whether a repo-relative path is on the <c>contrib/</c> side of the boundary.
    /// </summary>
    /// <remarks>
    ///     Keyed on the FIRST path segment, and never on the second one. <c>ProjectOf</c> reads
    ///     <c>segments[1]</c> and is right for <c>src/</c> and <c>apps/</c>; applied to
    ///     <c>contrib/apps/Harbor.App.Wpf/…</c> it returns <c>"apps"</c>, which would file every
    ///     <c>contrib/</c> file under the product tree and make the whole measurement vacuous in
    ///     exactly the way that hides a bug.
    /// </remarks>
    private static bool IsContrib(string relativeFile) =>
        relativeFile.StartsWith("contrib/", StringComparison.Ordinal);
}

/// <summary>
///     Measures the same-named types living across the <c>contrib/</c> boundary that CI does not
///     build, and pins the number so the hole can be seen and cannot grow unseen.
/// </summary>
public sealed class ContribBoundaryNameRule
{
    private static readonly Lazy<CrossBoundaryReport> Report = new(
        () => ContribBoundaryNameProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     The crossing names measured on <c>dev</c>, sorted. This is the finding, not a threshold
    ///     somebody chose: every row is a simple type name declared in a project CI builds AND in a
    ///     project CI never compiles. Rows are names rather than counts precisely so the list
    ///     stays readable in a diff — a count alone hides WHICH duplicates a reviewer is being
    ///     asked to accept.
    /// </summary>
    /// <remarks>
    ///     <b>54 names, measured.</b> Taken verbatim from the red run in the #843 PR body (run
    ///     36722789493, <c>Harbor.Architecture.Tests</c>, 1,043 files read), which is the only
    ///     reason this list is here at all: the first version of this array was empty, so the
    ///     first CI run failed and printed the whole set. A floor that was never stood on nothing
    ///     is not a floor, and reading 164 <c>.cs</c> files in <c>contrib/</c> by eye is how the
    ///     count came out wrong the first time.
    ///     <para>
    ///         Every row is legal in itself, and the header says why the rule therefore counts and
    ///         does not condemn. What the list is for is the direction of travel: 54 is the number
    ///         to watch, and a 55th name is the finding.
    ///     </para>
    /// </remarks>
    private static readonly string[] MeasuredBaseline =
    [
        "AgentErrorHandler",
        "App",
        "AssistantStreamHandler",
        "BrushKeyConverter",
        "ChatBubble",
        "ChatHistoryView",
        "ChatMessageViewModel",
        "ChatScreen",
        "ChatState",
        "ChatView",
        "ChatViewModel",
        "CodeEditorView",
        "CodeEditorViewModel",
        "CommandEntry",
        "CommandPaletteView",
        "CommandPaletteViewModel",
        "CompactionHandler",
        "CostToUsdConverter",
        "DiagnosticsPanel",
        "DialogService",
        "DiffView",
        "DiffViewModel",
        "Entry",
        "HarborTheme",
        "InputView",
        "MainViewModel",
        "MainWindow",
        "MarkdownRenderer",
        "ModelEntryViewModel",
        "NoopDisposable",
        "Program",
        "ProviderBrowserView",
        "ProviderBrowserViewModel",
        "ProviderEntryViewModel",
        "SessionEntryViewModel",
        "SessionListViewModel",
        "SessionRow",
        "SettingsView",
        "SettingsViewModel",
        "StatusBadge",
        "StatusBarView",
        "StatusTextToBrushConverter",
        "ThemeService",
        "TimeAgoConverter",
        "Toast",
        "ToastNotificationsView",
        "ToastService",
        "ToastViewModel",
        "TokenBarViewModel",
        "TokenUsageView",
        "TokenUsageViewModel",
        "TokensToCompactConverter",
        "ToolLifecycleHandler",
        "ToolStartHandler",
    ];

    private static readonly Lazy<CrossBoundaryReport> Synthetic = new(
        () => ContribBoundaryNameProbe.ScanFiles(
        [
            ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticRow.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public sealed class SyntheticRow",
                "{",
                "}",
            ]),
            ("contrib/apps/Harbor.App.Wpf/ViewModels/SyntheticRow.cs",
            [
                "namespace Harbor.App.Wpf.ViewModels;",
                string.Empty,
                "public sealed class SyntheticRow",
                "{",
                "}",
            ]),
        ]));

    // =====================================================================
    // 1. The measurement.
    // =====================================================================

    /// <summary>
    ///     The ratchet. The live set of crossing names must equal <see cref="MeasuredBaseline" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Asserted as a set of names, not as a number. A count ratchet is satisfiable by
    ///         deleting one duplicate on one side and adding another on the other side: the count
    ///         holds, the hole changes character, and the diff that did it looks like a wash. The
    ///         names make that impossible — a name that stops crossing is a name somebody has to
    ///         delete from this array on purpose, which is the edit that deserves review.
    ///     </para>
    ///     <para>
    ///         And when this fails, the message IS the measurement. That is why the first version
    ///         of this rule shipped with an empty array: the red run is what put a number and a
    ///         list in front of anyone, which no amount of reading <c>contrib/</c> by eye had.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task CrossBoundaryNames_MatchTheMeasuredBaseline()
    {
        var report = Report.Value;
        var live = report.Crossings.Select(c => c.Name).ToArray();

        await Assert.That(string.Join(" | ", live))
            .IsEqualTo(string.Join(" | ", MeasuredBaseline))
            .Because(
                "these are the simple type names declared BOTH in a project CI builds (src/, apps/) and "
                + "in contrib/, which no workflow compiles. contrib/ is outside the solution and outside "
                + "CI by owner decision (AGENTS.md), so this rule cannot demand zero — every gate that "
                + "prunes contrib/ would otherwise be reporting code that cannot break, and the tree has "
                + "25+ legitimate same-named types. What it CAN demand is that the hole stops growing: a "
                + "name added here is a name that has begun to diverge in a project nobody builds. "
                + "Deleting a row is the intended way to shrink it, and it is deliberate on purpose. "
                + "Live crossings: " + live.Length + " name(s). Files read — product: "
                + report.ProductFilesRead + ", contrib: " + report.ContribFilesRead
                + ". Crossings, with both sides: " + Describe(report.Crossings)
                + " | baseline held " + MeasuredBaseline.Length + " row(s): " + string.Join(" | ", MeasuredBaseline));
    }

    // =====================================================================
    // 2. Non-vacuity. The load-bearing one.
    // =====================================================================

    /// <summary>
    ///     The <c>contrib/</c> side of the walk really produced files, and so did the product side.
    /// </summary>
    /// <remarks>
    ///     This is the test that earns the rule above, and it exists because of a specific trap
    ///     rather than as boilerplate. <c>SourceScan.EnumerateCsFiles("contrib")</c> — the shared
    ///     helper seven other gates use — returns an EMPTY list for that tree by construction:
    ///     <c>IsBuildOutput</c> rejects any path containing <c>/contrib/</c>. A rule built on that
    ///     helper would report zero crossings, forever, in every configuration, and read exactly
    ///     like a clean tree. This file therefore walks with
    ///     <c>DiffSurfaceNameCollisionProbe.EnumerateFiles</c> and a skip set that takes
    ///     <c>contrib</c> back out; without this assertion, a future refactor that swaps the walk
    ///     back to the shared helper turns the measurement into a green light wired to nothing.
    /// </remarks>
    [Test]
    public async Task Contrib_IsActuallyRead()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the measurement needs a repository checkout; without one both sides read zero "
                   + "files and the ratchet above is satisfied by having looked at nothing");

        var report = Report.Value;

        await Assert.That(report.ContribFilesRead).IsGreaterThan(0)
            .Because(
                "the whole point of this rule is the side of the boundary nobody builds, and it read no "
                + "files there. A zero here is the failure mode this file was written against: the "
                + "shared SourceScan helper prunes /contrib/ by name, so a walk built on it reports "
                + "\"no duplicates across the boundary\" for a tree it never opened, and that reads as "
                + "a clean bill of health. Files read — product: " + report.ProductFilesRead
                + ", contrib: " + report.ContribFilesRead);

        await Assert.That(report.ProductFilesRead).IsGreaterThan(0)
            .Because(
                "a boundary needs two sides. If the product side read nothing, every name would be "
                + "counted as contrib-only and the crossing count would collapse toward zero for a "
                + "reason that has nothing to do with the tree. Files read — product: "
                + report.ProductFilesRead + ", contrib: " + report.ContribFilesRead);

        await Assert.That(MeasuredBaseline).IsNotEmpty()
            .Because(
                "an empty baseline means the ratchet is standing on nothing: it would accept any "
                + "crossing, because the only set it compares against is the empty set. The baseline "
                + "is the measured list of names, and it is non-empty because contrib/ really does "
                + "declare types that the built tree also declares");
    }

    // =====================================================================
    // 3. The positive control.
    // =====================================================================

    /// <summary>
    ///     THE POSITIVE CONTROL. Two files, one in a built project and one in a project CI never
    ///     compiles, declaring the same name — plus three decoys it must NOT report, and one
    ///     multi-declaration case it must report as a SINGLE crossing carrying both files.
    /// </summary>
    [Test]
    public async Task NonVacuity_DetectsACrossBoundaryCollisionInSyntheticSources()
    {
        var report = Synthetic.Value;

        await Assert.That(string.Join(" | ", report.Crossings.Select(c => c.Name)))
            .IsEqualTo("SyntheticRow")
            .Because(
                "this is the shape the rule exists to count, and the control drives the real matcher so "
                + "\"it can fire\" means something. Every source is synthetic and the name appears "
                + "nowhere in the tree: a control written against the names this rule actually reports "
                + "would pass against a matcher that had learned those names instead of the shape. "
                + "Reported: " + Describe(report.Crossings));

        CrossBoundaryName hit = report.Crossings[0];
        await Assert.That(hit.ProductFiles).IsEquivalentTo(["src/Harbor.Desktop.Abstractions/ViewModels/SyntheticRow.cs"])
            .Because("the finding must say which BUILT project declares the name, since \"this name "
                   + "exists twice\" without saying where is the ambiguity #843 is about");

        await Assert.That(hit.ContribFiles).IsEquivalentTo(["contrib/apps/Harbor.App.Wpf/ViewModels/SyntheticRow.cs"])
            .Because("and it must say which unbuilt one, so a reader can go and look at the side that "
                   + "nothing else in CI will ever tell them about");

        await Assert.That(report.ProductFilesRead).IsEqualTo(1)
            .Because("the control must put exactly one file on each side, or a matcher that filed every "
                   + "contrib file under the product tree would still pass this assertion");

        await Assert.That(report.ContribFilesRead).IsEqualTo(1)
            .Because("the contrib side is counted separately and separately asserted: this is the "
                   + "counterpart to the vacuity trap in Contrib_IsActuallyRead, which is why the "
                   + "boundary is read from the first path segment rather than from ProjectOf");

        // --- The decoys, each through the SAME matcher.
        List<(string Relative, string[] Lines)> decoys = new()
        {
            // (3) Both sides, but the contrib side only NAMES the type in prose. Comments are
            // stripped before the matcher runs, so documentation is not a declaration — and this
            // tree's contrib files are full of exactly that.
            ("contrib/apps/Harbor.App.Wpf/ViewModels/SyntheticProse.cs",
            [
                "namespace Harbor.App.Wpf.ViewModels;",
                string.Empty,
                "/// <summary>Reads <see cref=\"SyntheticRow\" /> to pick a brush.</summary>",
                "public sealed class SyntheticProse",
                "{",
                "    // TODO: reuse SyntheticRow here one day.",
                "}",
            ]),
            // (4) A name that merely CONTAINS the crossing one. Different type.
            ("contrib/apps/Harbor.App.Wpf/ViewModels/SyntheticRowCache.cs",
            [
                "namespace Harbor.App.Wpf.ViewModels;",
                string.Empty,
                "public sealed class SyntheticRowCache",
                "{",
                "}",
            ]),
            // (5) A duplicate declared twice inside the product perimeter and NOT in contrib.
            // Real duplication, a different rule's business entirely — and precisely the #843
            // case, where one word legitimately names two unrelated types.
            ("apps/Harbor.App.Cli/Hosting/SyntheticRow.cs",
            [
                "namespace Harbor.App.Cli.Hosting;",
                string.Empty,
                "public sealed class SyntheticRow",
                "{",
                "}",
            ]),
        };

        foreach (var decoy in decoys)
        {
            var decoyReport = ContribBoundaryNameProbe.ScanFiles(
            [
                ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticRow.cs",
                [
                    "namespace Harbor.Desktop.Abstractions.ViewModels;",
                    string.Empty,
                    "public sealed class SyntheticRow",
                    "{",
                    "}",
                ]),
                decoy,
            ]);

            await Assert.That(string.Join(" | ", decoyReport.Crossings.Select(c => c.Name)))
                .IsNotEqualTo("SyntheticRow")
                .Because(
                    decoy.Relative + " must not be reported as a crossing. Prose naming a type is not "
                    + "a declaration, a longer name is a different type, and duplication wholly inside "
                    + "the built perimeter is DiffSurfaceNameCollisionRule's question rather than this "
                    + "one. Reported: " + Describe(decoyReport.Crossings));
        }

        // The other direction, and the reason the two rows above were mis-filed the first time.
        //
        // A crossing is NOT "exactly one declaration on each side". It is "at least one on each
        // side", and everything here is a crossing:
        //
        //   * a partial type split across two files of ONE contrib project — legal C#, one
        //     implementation, and still a second implementation of a name the built tree declares.
        //     This is the #803 shape: a partial fossil nobody is maintaining.
        //   * the same name in a SECOND contrib project, which is a genuine third copy and the
        //     case the live 54 actually contains (`App` in Maui and Wpf, `ThemeService` in Blazor
        //     and Wpf, `ChatView` in four TUI projects).
        //
        // So all of it is ONE crossing carrying every declaration, and that is asserted here
        // rather than left to inference — a rule that de-duplicated by project, the obvious
        // "partials are one implementation" shortcut, would report the first file and drop the
        // rest, and the dropped copy is the one nobody reads.
        List<(string Relative, string[] Lines)> bothInContrib = new()
        {
            ("contrib/apps/Harbor.App.Wpf/ViewModels/SyntheticRow.cs",
            [
                "namespace Harbor.App.Wpf.ViewModels;",
                string.Empty,
                "public sealed class SyntheticRow",
                "{",
                "}",
            ]),
            ("contrib/tui/Harbor.Tui.Other/SyntheticRow.cs",
            [
                "namespace Harbor.Tui.Other;",
                string.Empty,
                "public sealed partial class SyntheticRow",
                "{",
                "}",
            ]),
        };

        var twoSided = ContribBoundaryNameProbe.ScanFiles(
        [
            ("src/Harbor.Desktop.Abstractions/ViewModels/SyntheticRow.cs",
            [
                "namespace Harbor.Desktop.Abstractions.ViewModels;",
                string.Empty,
                "public sealed class SyntheticRow",
                "{",
                "}",
            ]),
            .. bothInContrib,
        ]);

        await Assert.That(twoSided.Crossings.Count).IsEqualTo(1)
            .Because("a name declared on both sides is ONE crossing however many files declare it on "
                   + "either side. Counting declarations rather than names would inflate the pinned "
                   + "number with partial splits — real C# that this tree is full of — and make the "
                   + "baseline rot on ordinary refactorings");

        await Assert.That(twoSided.Crossings[0].ContribFiles)
            .IsEquivalentTo(bothInContrib.Select(f => f.Relative).ToArray())
            .Because("both contrib files must be named, or the finding says \"contrib/ declares this\" "
                   + "and a reader cannot tell that it declares it TWICE, in two projects, neither of "
                   + "which CI will ever compile. Reported: " + Describe(twoSided.Crossings));
    }

    private static string Describe(IReadOnlyList<CrossBoundaryName> crossings) =>
        crossings.Count == 0
            ? "(nothing)"
            : string.Join(
                " | ",
                crossings.Select(c => c.Name + ": " + string.Join(" + ", c.ProductFiles)
                                        + "  vs  " + string.Join(" + ", c.ContribFiles)));
}
