// DiSeverityDemotionRules.cs — the guard for issue #865.
//
// WHAT #865 IS, AND WHAT IS ALREADY GUARDED
// -----------------------------------------
// #865: the Avalonia block in `.editorconfig` demoted four DI rules under one
// sentence that named three of them. DI003 — captive dependency — was covered by
// neither the sentence nor the parenthetical that tried to enumerate the rest.
// That is a contradiction rather than a drift: the reason was written for a
// narrower demotion and a row was added underneath it, and nothing could say so
// because the reason is a COMMENT.
//
// `AnalyzerSeverityScopeRules` (merged for #838) already owns the other two
// halves of this same file: that a path-scoped section resolves to a real path
// (`PathScopedSections_ResolveToRealPaths`), that `docs/ANALYZERS.md` accounts
// for every path-scoped DI override, and that the set of relaxed paths is the
// declared inventory. This file is the third half and does not repeat the other
// two: a section can point at a real path, be documented, and still demote a
// rule that nothing in the file says why.
//
// WHY THE REASON IS INVISIBLE, WHICH IS THE WHOLE FINDING
// -------------------------------------------------------
// A severity demotion survives into no metadata — it is an editorconfig key. So
// the reflection sweeps that carry the rest of this project's rules cannot reach
// it by construction, and `ExemptionReason.cs` — the ONE place here that asks
// "does this tolerated row state a reason?" — fixes that for five C# exemption
// tables whose rows are VALUES. A config demotion is the sixth, and it is the one
// with no value at all: its justification is prose above the section, which
// `ExemptionReason`'s own header names as the defect it exists to kill
// ("the prose explaining why the violation is tolerated lived in a `//` comment
// above the row, which no compiler and no runtime can see").
//
// So the check reads the file the compiler already reads, and asks the one
// question nothing asks: does the row say what it authorises?
//
// THE RULE
// --------
//   Every severity set in a path-scoped section has its diagnostic id named in
//   the comment that governs it — the run directly above the row if it has one,
//   otherwise the run above the section header. Both shapes are accepted because
//   both are readable (a reason per row, or one reason over a block of rows);
//   what matters is only that a row never inherits a justification written for
//   its neighbours. Inheritance is precisely the #865 shape: three rows named,
//   a fourth silently covered by their sentence.
//
//   A row with no comment at all fails, which is the case that matters most —
//   the row the section's header does not mention and its author did not either.
//   The TUnit0055 block for NickConsoleExGoldenFrameTests is the live example:
//   it set a severity to `none` for a rule it never named, under a comment
//   arguing "no diagnostics fire in this file today" — a reason for DELETING the
//   row, kept in the place a reason for keeping it belongs.
//
// WHY NOT EXTEND `AnalyzerSeverityScopeRules` INSTEAD
// --------------------------------------------------
// Two considerations, and the second decided it. First, the other file's own
// header scopes itself to the DI family on purpose: `DiSeverityAssignment` is
// anchored on `DI\d{3}`, so the Excubo and TUnit reservations that are scoped to
// paths are deliberately out of its view. Widening it to every analyzer id would
// make its DI-scoped inventory and its doc-coverage rule answer for rules the
// severity table does not document, which is a different question from the one
// it was written for. Second, a second reader of the same file means a second
// parse to keep alive, and a guard whose two halves can disagree about what a
// "section" is reports one defect as two or none. This file reads the file and
// judges one thing; that file reads the same file and judges the other three.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — `.editorconfig` is in the checkout, parses, and yields at
//      least one path-scoped section carrying at least one row. Zero rows would
//      satisfy the rule by having nothing to grade.
//   2. NonVacuity_DetectsAnUnnamedRowInSyntheticEditorConfig — the POSITIVE
//      CONTROL. A synthetic section with one named and one unnamed row MUST
//      report the unnamed one and MUST NOT report the named one, and a comment
//      naming a DIFFERENT rule must not satisfy the check by proximity — the
//      failure mode that would leave the rule looking like it works while
//      checking nothing.
//
// WHAT THIS DOES NOT CLAIM
// ------------------------
// That a demotion is CORRECT is not checked, and cannot be by a text scan —
// whether a captive dependency exists is a question only a strict build
// answers, which is why the #865 narrowing is measured and cited in
// `.editorconfig` rather than asserted here. This asks the narrower question
// that was never asked at all.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>One analyzer severity a path-scoped <c>.editorconfig</c> section sets.</summary>
/// <param name="Id">The diagnostic id, e.g. <c>DI003</c> or <c>TUnit0055</c>.</param>
/// <param name="Severity">The severity it is set to.</param>
/// <param name="Line">1-based line of the assignment in the source file.</param>
/// <param name="Reason">
///     The comment run that governs this row: the one directly above it if it
///     has one, otherwise the run above the section header. A run is consumed by
///     the row it governs, so the next row cannot inherit it.
/// </param>
internal sealed record SeverityRowClaim(
    string Id,
    string Severity,
    int Line,
    string Reason,
    string Header);

/// <summary>Every severity row found in a path-scoped section.</summary>
/// <param name="Claims">The rows, in file order.</param>
/// <param name="SectionCount">How many path-scoped sections were parsed.</param>
internal sealed record DemotionReport(IReadOnlyList<SeverityRowClaim> Claims, int SectionCount);

/// <summary>Finds path-scoped analyzer-severity rows and whether each states why.</summary>
internal static class DiSeverityDemotionProbe
{
    /// <summary>Repo-relative path of the file every demotion lives in.</summary>
    internal const string EditorConfigRelativePath = ".editorconfig";

    /// <summary>
    ///     A section header that scopes to a path rather than to a file type.
    ///     <c>[*.{cs,csx}]</c> and <c>[Makefile]</c> are not repo paths; anything
    ///     anchoring on one is, and is what this rule judges. The anchor is the
    ///     literal text before the first glob character, matching how a reader
    ///     decides "which directory is this about?" — deliberately not an
    ///     evaluation of editorconfig glob semantics, which no shipped rule does.
    /// </summary>
    private static readonly Regex PathScopedHeader =
        new(@"^\[(?<glob>[^\]]*/[^\]]*)\]$", RegexOptions.Compiled);

    /// <summary>One <c>dotnet_diagnostic.X.severity = Y</c> row.</summary>
    private static readonly Regex SeverityRow = new(
        @"^\s*dotnet_diagnostic\.(?<id>[A-Za-z]+\d+)\.severity\s*=\s*(?<sev>\w+)",
        RegexOptions.Compiled);

    /// <summary>A comment line, the form <c>.editorconfig</c> reasons are written in.</summary>
    private static readonly Regex CommentLine = new(@"^\s*[#;]\s?(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>Parses already-split lines. Exposed so the positive control drives the real parser.</summary>
    internal static DemotionReport ParseLines(string[] lines)
    {
        var claims = new List<SeverityRowClaim>();
        var pending = new List<string>();
        string header = string.Empty;
        string headerComment = string.Empty;
        int sectionCount = 0;
        bool inPathSection = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];

            Match comment = CommentLine.Match(raw);
            if (comment.Success)
            {
                pending.Add(comment.Groups["text"].Value.Trim());
                continue;
            }

            if (raw.Trim().Length == 0)
            {
                // A blank line ends the comment run, so a reason written above
                // one section cannot be borrowed by the next — otherwise the
                // file's leading prose becomes a reason for every row in it.
                pending.Clear();
                continue;
            }

            Match scoped = PathScopedHeader.Match(raw.Trim());
            if (scoped.Success)
            {
                sectionCount++;
                header = raw.Trim();
                headerComment = string.Join(' ', pending);
                pending.Clear();
                inPathSection = true;
                continue;
            }

            // Any OTHER section header — `[*]`, `[*.{cs,csx}]`, `[Makefile]` —
            // ends the path section. The tree-wide block above the
            # PATH-SCOPED SEVERITY OVERRIDES banner sets 200+ severities under a
            // per-rule table, and judging those rows here would be judging the
            // wrong claim: those are the rule's own severity, not a permission
            // granted to a subset of the tree.
            if (raw.TrimStart().StartsWith('['))
            {
                inPathSection = false;
                pending.Clear();
                continue;
            }

            Match row = SeverityRow.Match(raw);
            if (row.Success)
            {
                if (!inPathSection)
                {
                    pending.Clear();
                    continue;
                }

                string reason = pending.Count > 0 ? string.Join(' ', pending) : headerComment;

                claims.Add(new SeverityRowClaim(
                    row.Groups["id"].Value,
                    row.Groups["sev"].Value,
                    i + 1,
                    reason,
                    header));

                // The run is consumed by the row it governs. This single line is
                // the difference between "one reason names three rows" (allowed)
                // and "the fourth row is covered by a sentence about the other
                // three" (the #865 defect, not allowed).
                pending.Clear();
                continue;
            }

            // Anything else ends the run without being a reason.
            pending.Clear();
        }

        return new DemotionReport(claims, sectionCount);
    }

    /// <summary>Reads the file out of a checkout. A missing root parses nothing, which the rule reports.</summary>
    internal static DemotionReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new DemotionReport([], 0);
        }

        string path = Path.Combine(repoRoot, EditorConfigRelativePath);
        if (!File.Exists(path))
        {
            return new DemotionReport([], 0);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return new DemotionReport([], 0);
        }

        return ParseLines(lines);
    }

    /// <summary>
    ///     Rows whose diagnostic id is not named in the comment governing them.
    ///     The match is on the whole id token, so a comment that discusses a
    ///     different rule cannot satisfy this one by proximity.
    /// </summary>
    internal static IReadOnlyList<string> UnnamedRows(DemotionReport report)
    {
        var offenders = new List<string>();

        foreach (SeverityRowClaim claim in report.Claims)
        {
            if (claim.Reason.Contains(claim.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            offenders.Add(
                $"{EditorConfigRelativePath}:{claim.Line} {claim.Header} sets {claim.Id} to "
                + $"'{claim.Severity}' without naming {claim.Id} in the comment that governs it. That comment "
                + "is the only thing that says what the demotion authorises, and it is invisible to every tool "
                + "that reads this file — the compiler, the demoted analyzer, and this guard all see only the "
                + "key. Write the argument next to the row it authorises and name the rule: a row that inherits "
                + "its neighbours' sentence is how one sat unexamined for a year (#865, where DI003 was demoted "
                + "under a reason about process-lifetime singletons). Comment as read: \""
                + TrimForMessage(claim.Reason) + "\"");
        }

        return offenders;
    }

    /// <summary>Shortens a value so a failure message stays readable.</summary>
    internal static string TrimForMessage(string value) =>
        value.Length <= 160 ? value : value[..157] + "...";
}

/// <summary>
///     Issue #865: every analyzer severity a path-scoped <c>.editorconfig</c>
///     section sets is named in the comment that governs it.
/// </summary>
public class DiSeverityDemotionRules
{
    private static readonly Lazy<DemotionReport> Report = new(
        () => DiSeverityDemotionProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     Every severity set in a path-scoped section has its rule named where it
    ///     is set. This is the row that failed: DI003 was demoted under a sentence
    ///     that described process-lifetime singletons, which is a different rule of
    ///     a different kind.
    /// </summary>
    [Test]
    public async Task PathScopedSection_NamesEveryRuleItSets()
    {
        IReadOnlyList<string> offenders = DiSeverityDemotionProbe.UnnamedRows(Report.Value);

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a demotion's reason is a comment, so it is invisible to the compiler, to the analyzer it "
                + "silences, and to every gate in this project — an unnamed row is one nobody can audit later. "
                + "#865 sat that way: DI003 (captive dependency, a lifetime-GRAPH rule) was set to 'suggestion' "
                + "under a reason about process-lifetime singletons, which covered neither the rule nor the "
                + "breadth of the section. Write the argument next to the row it authorises. Offending rows: "
                + (offenders.Count == 0 ? "(none)" : string.Join(" | ", offenders)));
    }

    /// <summary>
    ///     The scan really read the checkout and really found path-scoped rows.
    ///     A report with none satisfies the rule by having nothing to grade, which
    ///     is indistinguishable from a broken reader.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it parses zero rows and the rule above "
                   + "is satisfied by having nothing to look at");

        DemotionReport report = Report.Value;

        await Assert.That(report.SectionCount).IsGreaterThan(0)
            .Because($"{DiSeverityDemotionProbe.EditorConfigRelativePath} yielded no path-scoped section, so the "
                   + "header matcher stopped working and the rule is vacuously green");

        await Assert.That(report.Claims.Count).IsGreaterThan(0)
            .Because($"no severity row was read out of {DiSeverityDemotionProbe.EditorConfigRelativePath}, so "
                   + "the rule compares against an empty set and passes for any file whatsoever");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. A synthetic section with one named and one
    ///     unnamed row MUST report the unnamed one and MUST NOT report the named
    ///     one. Without this, a matcher that stopped matching would leave the
    ///     rule green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_DetectsAnUnnamedRowInSyntheticEditorConfig()
    {
        DemotionReport Parse(string content) =>
            DiSeverityDemotionProbe.ParseLines(content.Split('\n'));

        // The shape a correct edit has: every rule named.
        DemotionReport named = Parse(
            """
            # DI006 — the XAML object graph needs a container handed to it.
            # DI014 — a process-lifetime root provider is disposed at exit.
            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI006.severity = suggestion
            dotnet_diagnostic.DI014.severity = suggestion
            """);

        // The #865 shape: three named, the fourth covered by their sentence.
        DemotionReport inherited = Parse(
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
        DemotionReport unrelated = Parse(
            """
            # DI015 is unrelated to this section and appears here only as prose.
            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI003.severity = suggestion
            """);

        // A blank line ends the comment run, so prose above an unrelated
        // section cannot be borrowed.
        DemotionReport notBorrowed = Parse(
            """
            # DI003 — a reason that belongs to a different section, not this one.

            [apps/Some.App/**.cs]
            dotnet_diagnostic.DI003.severity = suggestion
            """);

        // A row with no comment anywhere: the case that matters most, being the
        // row neither the section header nor the author mentioned.
        DemotionReport bare = Parse(
            """
            [apps/Some.App/**.cs]
            dotnet_diagnostic.TUnit0055.severity = none
            """);

        await Assert.That(DiSeverityDemotionProbe.UnnamedRows(named)).IsEmpty()
            .Because("both rules in the first snippet are named in the comment that governs them, which is the "
                   + "shape a correct edit has — the rule firing on it would forbid the fix it exists to demand");

        IReadOnlyList<string> inheritedOffenders = DiSeverityDemotionProbe.UnnamedRows(inherited);
        await Assert.That(inheritedOffenders.Count).IsEqualTo(1)
            .Because("the second snippet is the exact #865 shape: DI003 inherits a sentence written about the "
                   + "other three. A miss here means the run-consumption or the id reader stopped working and the "
                   + "rule enforces nothing. Reported: " + inheritedOffenders.Count);

        await Assert.That(inheritedOffenders[0]).Contains("DI003")
            .Because("the failure must name the rule that is unaccounted for, or the message cannot tell the "
                   + "author which row needs a reason");

        await Assert.That(DiSeverityDemotionProbe.UnnamedRows(unrelated).Count).IsEqualTo(1)
            .Because("a comment naming DI015 does not cover a DI003 row; a rule that matched on 'the comment "
                   + "mentions some diagnostic id' would pass any section that has ever been discussed");

        await Assert.That(DiSeverityDemotionProbe.UnnamedRows(notBorrowed).Count).IsEqualTo(1)
            .Because("a blank line ends a comment run, so a reason written above a different section cannot be "
                   + "borrowed — otherwise the file's leading prose becomes a reason for every row in it");

        await Assert.That(DiSeverityDemotionProbe.UnnamedRows(bare).Count).IsEqualTo(1)
            .Because("a row with no comment at all is the case worth catching: it is the row neither the "
                   + "section header nor its author mentioned, and the TUnit0055 block was in exactly this "
                   + "state — a severity set to 'none' under a comment that never named the rule");
    }
}
