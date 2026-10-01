// DiSeverityDemotionRules.cs — the guard for issue #865.
//
// THE DEFECT THIS GUARDS
// ----------------------
// `.editorconfig` demotes analyzer severities in PATH-SCOPED sections, and
// nothing read them. Four sections did it, one reason sentence covered all
// four, and the sentence named three of the four rules:
//
//     # Desktop apps: relax DI rules for process-lifetime singletons
//     # (root provider, static IServiceProvider cache, transient VMs with
//     #  process lifetime)
//     [apps/Harbor.App.Avalonia/**.cs]
//     dotnet_diagnostic.DI003.severity = suggestion   <- not covered
//     dotnet_diagnostic.DI006.severity = suggestion   <- "static IServiceProvider cache"
//     dotnet_diagnostic.DI008.severity = suggestion   <- "transient VMs with process lifetime"
//     dotnet_diagnostic.DI014.severity = suggestion   <- "root provider"
//
// DI003 is a lifetime-GRAPH rule (a singleton or long-lived factory retaining
// a scoped/transient service); process lifetime says nothing about it, so the
// row granted an exception whose stated justification did not exist. Nobody
// noticed for a year, and the reason it went unnoticed is the shape below.
//
// WHY THE REASON WAS NOT CHECKABLE, AND WHY THAT IS THE REAL FINDING
// ------------------------------------------------------------------
// A demotion's reason lives in a `//` comment above the rows. It is invisible
// to the compiler, to the analyzer it demotes, and to every one of the
// repository gates in this project — none of which read `.editorconfig` at
// all except `CfeValueBaselineTests`, which greps for one specific diagnostic
// id. `ExemptionReason.cs` is the ONE place in this project that answers "does
// this tolerated row state a reason?", and its own header names this exact
// failure:
//
//     "the prose explaining why the violation is tolerated lived in a `//`
//      comment above the row, which no compiler and no runtime can see"
//
// …and then fixes it for FIVE tables (a baseline violation, a permanent
// capability, a documented layer exception, a declared-but-unbound reference, a
// plugin allowance) — none of which is a config demotion. The single check that
// exists skipped the one permission whose row is not even a value.
//
// This file is that check applied to the sixth. No allow-list is introduced
// and no table is duplicated: the rows stay in `.editorconfig`, where the
// compiler reads them, and the check asks only whether each row is answerable.
//
// THE TWO RULES
// -------------
//   R1  Every rule demoted in a path-scoped section is NAMED in that section's
//       own comment. One sentence above four rows is the shape that hid DI003:
//       a reason covering N rows is only checkable if it names them, and this
//       is the mechanical form of "the reason must be the row's name".
//
//   R2  Every path-scoped section has a subject: the directory its glob is
//       rooted at exists and holds real source. The three sibling blocks
//       [apps/Harbor.App.Wpf|Maui|Blazor/**.cs] survived d3b26e4d, which moved
//       those projects to contrib/apps/ — outside the solution and outside CI.
//       Their globs matched nothing, so they had been demoting severities for
//       code no build compiles: a permission that cannot be observed, reading
//       exactly like one that can. A guard that only asked R1 would pass on
//       them forever, since a dead section's rows are still named.
//
// WHY A TEXT SCAN AND NOT A COMPILED CHECK
// ----------------------------------------
// A severity demotion survives into no metadata: it is an editorconfig key.
// `CfeValueBaselineTests` already reads this same file with the same approach
// and lives in this same project, so the form is established and the reader is
// the build that already runs it. No new axis (#555) and no new project.
//
// PERIMETER
// ---------
// Only PATH-SCOPED sections are judged. The global `[*.{cs,csx}]` section sets
// severities for the whole repo and each rule there is documented in the
// severity table a few lines above it; R1 is about a section that overrides
// the global answer for a subset of the tree, which is a different claim and
// needs its own.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — `.editorconfig` is in the checkout, parses, and yields at
//      least one path-scoped section with at least one demoted row. Zero rows
//      would satisfy R1 by having nothing to grade.
//   2. NonVacuity_R1_DetectsAnUnnamedDemotionInSyntheticEditorConfig — the
//      POSITIVE CONTROL. A synthetic section with one named and one unnamed
//      demotion MUST report the unnamed one and MUST NOT report the named one.
//      Without it, a matcher that stopped matching would leave the rule green
//      while enforcing nothing.
//   3. NonVacuity_R2_DetectsASectionWithNoSubject — the same for R2.
//
// WHAT THIS DOES NOT CLAIM
// ------------------------
// That a demotion is CORRECT is not checked, and cannot be by a text scan —
// whether a captive dependency exists is a question only a strict build
// answers, which is why the #865 fix is measured and cited in .editorconfig
// rather than asserted here. This file asks the narrower question that was
// never asked at all: is the row answerable, and does it point at anything.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>One analyzer severity a path-scoped section sets.</summary>
/// <param name="Id">The diagnostic id, e.g. <c>DI003</c>.</param>
/// <param name="Severity">The severity it is set to.</param>
/// <param name="Reason">
///     The comment run that applies to this row: the one directly above it if it
///     has one, otherwise the run above the section header. Both shapes are
///     accepted because both are readable — a reason per row, or one reason over
///     a block of rows — and the check that matters is only whether the row's own
///     id appears in whichever run governs it.
/// </param>
internal sealed record DemotedSeverity(string Id, string Severity, string Reason);

/// <summary>One path-scoped <c>.editorconfig</c> section, with the comment above it.</summary>
/// <param name="Header">The section header verbatim, e.g. <c>[apps/…/**.cs]</c>.</param>
/// <param name="Glob">The glob inside the header, without brackets.</param>
/// <param name="Line">1-based line of the header in the source file.</param>
/// <param name="Comment">The contiguous comment block directly above the header.</param>
/// <param name="Demotions">Every severity the section sets.</param>
internal sealed record EditorConfigSection(
    string Header,
    string Glob,
    int Line,
    string Comment,
    IReadOnlyList<DemotedSeverity> Demotions);

/// <summary>What one <c>.editorconfig</c> parse found.</summary>
/// <param name="Sections">Every path-scoped section, in file order.</param>
internal sealed record EditorConfigReport(IReadOnlyList<EditorConfigSection> Sections)
{
    /// <summary>How many severity rows the sections set in total.</summary>
    internal int DemotionCount => Sections.Sum(s => s.Demotions.Count);
}

/// <summary>Finds path-scoped analyzer-severity demotions and whether each states why.</summary>
internal static class DiSeverityDemotionProbe
{
    /// <summary>Repo-relative path of the file every demotion lives in.</summary>
    internal const string EditorConfigRelativePath = ".editorconfig";

    /// <summary>
    ///     A section header that scopes to a path rather than to a file type.
    ///     <c>[*.{cs,csx}]</c> and <c>[*.cs]</c> are global; anything containing a
    ///     <c>/</c> narrows the answer to part of the tree and is what R1/R2 judge.
    /// </summary>
    private static readonly Regex PathScopedHeader =
        new(@"^\[(?<glob>[^\]]*/[^\]]*)\]$", RegexOptions.Compiled);

    /// <summary>One <c>dotnet_diagnostic.X.severity = Y</c> row.</summary>
    private static readonly Regex SeverityRow = new(
        @"^\s*dotnet_diagnostic\.(?<id>[A-Za-z]+\d+)\.severity\s*=\s*(?<sev>\w+)",
        RegexOptions.Compiled);

    /// <summary>A comment line, the form <c>.editorconfig</c> reasons are written in.</summary>
    private static readonly Regex CommentLine = new(@"^\s*[#;]\s?(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>Parses already-split lines. Exposed so a positive control drives the real parser.</summary>
    internal static EditorConfigReport ParseLines(string[] lines)
    {
        var sections = new List<EditorConfigSection>();
        var pendingComment = new List<string>();
        EditorConfigSection? open = null;
        var demotions = new List<DemotedSeverity>();

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];

            Match comment = CommentLine.Match(raw);
            if (comment.Success)
            {
                pendingComment.Add(comment.Groups["text"].Value.Trim());
                continue;
            }

            if (raw.Trim().Length == 0)
            {
                // A blank line ends the comment run, so two adjacent sections
                // cannot borrow each other's prose — and a section's reason has
                // to sit directly above it, not at the top of the file.
                pendingComment.Clear();
                continue;
            }

            Match header = PathScopedHeader.Match(raw.Trim());
            if (header.Success)
            {
                if (open is not null)
                {
                    sections.Add(open with { Demotions = demotions });
                }

                open = new EditorConfigSection(
                    raw.Trim(),
                    header.Groups["glob"].Value,
                    i + 1,
                    string.Join(' ', pendingComment),
                    []);
                demotions = [];
                pendingComment.Clear();
                continue;
            }

            Match row = SeverityRow.Match(raw);
            if (row.Success)
            {
                // A comment run directly above a row is that row's reason; with
                // no such run, the run above the section header governs it. The
                // run is then consumed, so the next row cannot inherit it — that
                // inheritance is how one sentence ends up looking like the
                // justification for four rows, which is the #865 shape.
                string reason = pendingComment.Count > 0
                    ? string.Join(' ', pendingComment)
                    : (open?.Comment ?? string.Empty);

                demotions.Add(new DemotedSeverity(row.Groups["id"].Value, row.Groups["sev"].Value, reason));
                pendingComment.Clear();
                continue;
            }

            // A non-comment, non-header, non-severity line ends the comment run
            // without being a reason for anything.
            pendingComment.Clear();
        }

        if (open is not null)
        {
            sections.Add(open with { Demotions = demotions });
        }

        return new EditorConfigReport(sections);
    }

    /// <summary>Reads the file out of a checkout. A missing root parses nothing, which the rule reports.</summary>
    internal static EditorConfigReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new EditorConfigReport([]);
        }

        string path = Path.Combine(repoRoot, EditorConfigRelativePath);
        if (!File.Exists(path))
        {
            return new EditorConfigReport([]);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return new EditorConfigReport([]);
        }

        return ParseLines(lines);
    }

    /// <summary>
    ///     Severity rows whose diagnostic id is not named anywhere in the section's
    ///     own comment. The match is on the whole id token, so a comment that
    ///     discusses a different rule does not accidentally satisfy this one.
    /// </summary>
    internal static IReadOnlyList<string> UnnamedDemotions(EditorConfigReport report)
    {
        var offenders = new List<string>();

        foreach (EditorConfigSection section in report.Sections)
        {
            foreach (DemotedSeverity demotion in section.Demotions)
            {
                if (demotion.Reason.Contains(demotion.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                offenders.Add(
                    $"{EditorConfigRelativePath}:{section.Line} {section.Header} sets {demotion.Id} to "
                    + $"'{demotion.Severity}' without naming {demotion.Id} in the comment that governs it. That "
                    + "comment is the only thing that says what the demotion authorises, and it is invisible to "
                    + "every tool that reads this file — the compiler, the demoted analyzer, and this guard all see "
                    + "only the key. Write the argument next to the row it authorises, and name the rule: one "
                    + "sentence covering several rows is the shape that let this one sit unexamined for a year "
                    + $"(#865). Comment as read: \"{TrimForMessage(demotion.Reason)}\"");
            }
        }

        return offenders;
    }

    /// <summary>Path-scoped sections whose subject directory holds no real source file.</summary>
    internal static IReadOnlyList<string> SectionsWithNoSubject(EditorConfigReport report, string? repoRoot)
    {
        var offenders = new List<string>();

        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return offenders;
        }

        foreach (EditorConfigSection section in report.Sections)
        {
            if (HasSubject(repoRoot, section.Glob))
            {
                continue;
            }

            offenders.Add(
                $"{EditorConfigRelativePath}:{section.Line} {section.Header} scopes to a path with no source behind "
                + $"it, so every severity it sets is unreachable. A demotion no build compiles is a comment that "
                + $"reads like a permission — and it survives a directory move, because nothing resolves the glob. "
                + $"Resolved: {DirectoryForGlob(repoRoot, section.Glob)}");
        }

        return offenders;
    }

    /// <summary>Shortens a value so a failure message stays readable.</summary>
    internal static string TrimForMessage(string value) =>
        value.Length <= 160 ? value : value[..157] + "...";

    /// <summary>
    ///     The absolute directory a glob is rooted at: everything before the first
    ///     wildcard segment, or the last directory before a wildcard-free
    ///     file-name glob. <c>apps/…/**.cs</c> and <c>tests/…/File.cs</c> both resolve
    ///     to the directory that has to exist for the section to have a subject.
    /// </summary>
    internal static string DirectoryForGlob(string repoRoot, string glob)
    {
        string normalized = glob.Replace('\\', '/');

        int wildcard = normalized.IndexOfAny(['*', '?', '{', '[']);
        string prefix = wildcard < 0 ? normalized : normalized[..wildcard];

        int lastSlash = prefix.LastIndexOf('/');
        string directory = lastSlash <= 0 ? string.Empty : prefix[..lastSlash];

        return Path.GetFullPath(Path.Combine(repoRoot, directory));
    }

    /// <summary>
    ///     Whether the section's subject directory exists and holds at least one
    ///     <c>.cs</c> file that is not build output. Only the existence of a subject
    ///     is at issue here — whether the project is IN the solution is a separate
    ///     question this rule does not ask, and a section pointing at a project
    ///     that is out of the solution but present on disk is a coverage question,
    ///     not a dead-glob question.
    /// </summary>
    private static bool HasSubject(string repoRoot, string glob)
    {
        string directory = DirectoryForGlob(repoRoot, glob);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (!IsBuildOutput(file))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Build output is not a subject: a stale obj/ copy would keep a moved project alive.</summary>
    private static bool IsBuildOutput(string path) =>
        HasSegment(path, "obj")
        || HasSegment(path, "bin")
        || HasSegment(path, ".worktrees");

    /// <summary>Whether a path segment appears as a whole directory name.</summary>
    private static bool HasSegment(string path, string segment) =>
        path.Contains(Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

/// <summary>
///     Issue #865: every path-scoped analyzer-severity demotion names the rule it
///     demotes, and every path-scoped section points at a path that exists.
/// </summary>
public class DiSeverityDemotionRules
{
    private static readonly Lazy<EditorConfigReport> Report = new(
        () => DiSeverityDemotionProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. R1 — a demoted rule is named where it is demoted.
    // =====================================================================

    /// <summary>
    ///     Every severity set in a path-scoped section has its rule named in that
    ///     section's own comment. This is the row that failed: DI003 was demoted
    ///     under a sentence that described process-lifetime singletons, which is a
    ///     different rule of a different kind.
    /// </summary>
    [Test]
    public async Task PathScopedSection_NamesEveryRuleItSets()
    {
        IReadOnlyList<string> offenders = DiSeverityDemotionProbe.UnnamedDemotions(Report.Value);

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a demotion's reason is a comment, and a comment is invisible to the compiler, to the analyzer it "
                + "silences, and to every gate in this project — so an unnamed row is one nobody can audit later. "
                + "#865 sat that way: DI003 (captive dependency, a lifetime-GRAPH rule) was set to 'suggestion' under "
                + "a reason about process-lifetime singletons, which covered neither the rule nor the breadth of the "
                + "section. Write the argument next to the row it authorises. Offending rows: "
                + (offenders.Count == 0 ? "(none)" : string.Join(" | ", offenders)));
    }

    // =====================================================================
    // 2. R2 — a path-scoped section has a subject.
    // =====================================================================

    /// <summary>
    ///     Every path-scoped section resolves to at least one real source file.
    ///     The three <c>Harbor.App.Wpf|Maui|Blazor</c> blocks outlived d3b26e4d,
    ///     which moved those projects under <c>contrib/apps/</c> — so they had
    ///     been setting severities for code no build compiles.
    /// </summary>
    [Test]
    public async Task PathScopedSection_HasRealSourceBehindIt()
    {
        IReadOnlyList<string> offenders =
            DiSeverityDemotionProbe.SectionsWithNoSubject(Report.Value, RepoPaths.RepoRoot);

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a path-scoped demotion whose path is gone grants nothing and reads exactly like one that does, which "
                + "is how three dead blocks survived a directory move unnoticed. If the project moved, the block is "
                + "either re-pointed at its new home or deleted — a block aimed at contrib/ (out of the solution and "
                + "out of CI) has no build to demote. Subjectless sections: "
                + (offenders.Count == 0 ? "(none)" : string.Join(" | ", offenders)));
    }

    // =====================================================================
    // 3. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really read the checkout and really found path-scoped sections
    ///     that set severities. A report with none satisfies R1 and R2 by having
    ///     nothing to grade, which is indistinguishable from a broken reader.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it parses zero sections and both rules "
                   + "here are satisfied by having nothing to look at");

        EditorConfigReport report = Report.Value;

        await Assert.That(report.Sections.Count).IsGreaterThan(0)
            .Because($"{DiSeverityDemotionProbe.EditorConfigRelativePath} yielded no path-scoped section, so the "
                   + "header matcher stopped working and both rules are vacuously green");

        await Assert.That(report.DemotionCount).IsGreaterThan(0)
            .Because($"no severity row was read out of {DiSeverityDemotionProbe.EditorConfigRelativePath}, so R1 "
                   + "compares against an empty set and passes for any file whatsoever");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL for R1. A synthetic section setting one named and
    ///     one unnamed rule MUST report the unnamed one and MUST NOT report the
    ///     named one — and a comment that discusses a DIFFERENT rule must not
    ///     satisfy R1 by proximity, which is the failure mode that would make the
    ///     rule look like it works while checking nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_R1_DetectsAnUnnamedRowInSyntheticEditorConfig()
    {
        EditorConfigReport Parse(string content) =>
            DiSeverityDemotionProbe.ParseLines(content.Split('\n'));

        // The shape a correct edit has: every rule named.
        EditorConfigReport named = Parse(
            """
            # DI006 — the XAML object graph needs a container handed to it.
            # DI014 — a process-lifetime root provider is disposed at exit.
            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI006.severity = suggestion
            dotnet_diagnostic.DI014.severity = suggestion
            """);

        // The #865 shape: three named, the fourth not.
        EditorConfigReport unnamed = Parse(
            """
            # DI006 — the XAML object graph needs a container handed to it.
            # DI008 — a disposable transient that lives for the whole process.
            # DI014 — a process-lifetime root provider is disposed at exit.
            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI006.severity = suggestion
            dotnet_diagnostic.DI008.severity = suggestion
            dotnet_diagnostic.DI003.severity = suggestion
            dotnet_diagnostic.DI014.severity = suggestion
            """);

        // A comment naming a rule the section does NOT set must not count as
        // covering the one it does.
        EditorConfigReport unrelated = Parse(
            """
            # DI015 is unrelated to this section and appears here only as prose.
            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI003.severity = suggestion
            """);

        // A blank line ends the comment run, so prose above an unrelated
        // section cannot be borrowed as this section's reason.
        EditorConfigReport notBorrowed = Parse(
            """
            # DI003 — a reason that belongs to a different section, not this one.

            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI003.severity = suggestion
            """);

        await Assert.That(DiSeverityDemotionProbe.UnnamedDemotions(named)).IsEmpty()
            .Because("both rules in the first snippet are named in the section's own comment, which is the shape a "
                   + "correct edit looks like — R1 firing on it would forbid the fix it exists to demand");

        IReadOnlyList<string> unnamedOffenders = DiSeverityDemotionProbe.UnnamedDemotions(unnamed);
        await Assert.That(unnamedOffenders.Count).IsEqualTo(1)
            .Because("the second snippet is the exact #865 shape: DI003 is set to 'suggestion' under a comment that "
                   + "names the other three and not it. A miss here means the id reader or the comment attachment "
                   + "stopped working and the rule enforces nothing. Reported: " + unnamedOffenders.Count);

        await Assert.That(unnamedOffenders[0]).Contains("DI003")
            .Because("the failure must name the rule that is unaccounted for, or the message cannot tell the author "
                   + "which row needs a reason");

        await Assert.That(DiSeverityDemotionProbe.UnnamedDemotions(unrelated).Count).IsEqualTo(1)
            .Because("a comment naming DI015 does not cover a DI003 row. A rule that matched on 'the comment mentions "
                   + "some diagnostic id' would pass any section that has ever been discussed");

        await Assert.That(DiSeverityDemotionProbe.UnnamedDemotions(notBorrowed).Count).IsEqualTo(1)
            .Because("a blank line ends a comment run, so a reason written above a different section cannot be "
                   + "borrowed — otherwise the file's leading prose becomes a reason for every row in it");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL for R2: a section aimed at a path that does not
    ///     exist MUST be reported, one aimed at a real directory MUST NOT, and a
    ///     directory whose only <c>.cs</c> files are build output MUST NOT keep a
    ///     section alive.
    /// </summary>
    [Test]
    public async Task NonVacuity_R2_DetectsASectionWithNoSubject()
    {
        string? maybeRoot = RepoPaths.RepoRoot;
        await Assert.That(maybeRoot).IsNotNull()
            .Because("the R2 positive control needs a repository checkout; without one there is no filesystem "
                   + "to resolve a glob against, and both halves of the control would be unreachable");

        if (maybeRoot is not { } root)
        {
            return;
        }

        // The block d3b26e4d left behind, reproduced verbatim.
        EditorConfigReport dead = DiSeverityDemotionProbe.ParseLines(
            [
                "[apps/Harbor.App.Wpf/**.cs]",
                "dotnet_diagnostic.DI006.severity = suggestion",
            ]);

        // A section over a directory that is on disk.
        EditorConfigReport live = DiSeverityDemotionProbe.ParseLines(
            [
                "[apps/Harbor.App.Cli/**.cs]",
                "dotnet_diagnostic.DI006.severity = suggestion",
            ]);

        // A directory that exists but holds nothing but build output must not
        // count as a subject.
        string stale = Path.Combine(root, "src", "Harbor.Abstractions", "obj", "DiSeverityDemotionRulesProbe");
        Directory.CreateDirectory(stale);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(stale, "Generated.cs"), "// build output, not a subject\n");

            await Assert.That(DiSeverityDemotionProbe.SectionsWithNoSubject(dead, root).Count).IsEqualTo(1)
                .Because("the Wpf block is the shape #865's fix deleted: a path-scoped demotion whose project had "
                       + "moved to contrib/apps/ and whose glob had matched nothing ever since");

            await Assert.That(DiSeverityDemotionProbe.SectionsWithNoSubject(live, root)).IsEmpty()
                .Because("apps/Harbor.App.Cli is on disk and holds sources — R2 firing on it would forbid every "
                       + "path-scoped demotion in the file, which is not what this rule is for");

            EditorConfigReport buildOutputOnly = DiSeverityDemotionProbe.ParseLines(
                ["[src/Harbor.Abstractions/obj/DiSeverityDemotionRulesProbe/**.cs]"]);

            await Assert.That(DiSeverityDemotionProbe.SectionsWithNoSubject(buildOutputOnly, root).Count)
                .IsEqualTo(1)
                .Because("a directory that exists but contains only obj/ output is not a subject: a stale build "
                       + "artefact is the one thing that must not keep a moved project's demotion alive");
        }
        finally
        {
            try
            {
                Directory.Delete(stale, recursive: true);
            }
            catch (IOException)
            {
                // A leftover directory under obj/ is build output by definition.
            }
        }
    }
}
