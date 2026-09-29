// SessionStatusTableRule.cs — GUARD for issue #663.
//
// THE CONVENTION BEING ENFORCED
// -----------------------------
// `SessionStatus` (idle / working / done / error / aborted) has exactly one
// rendered form per concern, and both live in ONE file:
//
//   src/Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs
//     SessionStatusToText(status)      → "working" | "done" | ...
//     SessionStatusToBrushKey(status)  → "MochaYellow" | "MochaGreen" | ...
//
// The owner-facing principle is that the headless core computes and hands over
// data, the TEA/ELM UI framework only displays it — and a DISPLAYED STATUS is
// one domain concept, so it gets one table. A second table is not a style
// difference; it is a second opinion about what a status looks like, and the
// two opinions drift.
//
// WHAT #663 ACTUALLY FOUND
// ------------------------
// Five tables, not three, and they had already disagreed:
//
//   1. StatusMappers.SessionStatusToText / SessionStatusToBrushKey — canonical.
//      Working → "working" / "MochaYellow".
//   2. SessionItemViewModel.StatusText / StatusColor — a verbatim copy of the
//      LABELS, but Working → "AccentPrimaryBrush" for the BRUSH. A doc comment
//      asserted the divergence was intended ("Working → amber"), so two UI
//      layers each believed they owned the colour of a working session.
//   3. SessionContext.StatusText — a third copy of the labels.
//   4. SessionCardViewModel.DotState — routed the same status through a second
//      vocabulary, SessionDotState, with its own opinions (Aborted → Error).
//   5. SubagentsModel.StatusText — a fourth copy of the labels that had
//      already drifted: Working → "running", and a doc comment claiming it
//      matched SessionContext, which it did not.
//
// #567/#635 had already done this collapse for the three tool-call lifecycle
// enums; SessionStatus was what they missed.
//
// WHY A TEXT SCAN AND NOT A COMPILED CHECK
// ----------------------------------------
// The rule is "a table does not exist in a second FILE", which no type system
// can see — the duplicates are all well-typed members that happen to recompute
// a value the framework already knows how to compute. So this is a source scan,
// deliberately narrow: it grades a `switch` only when the arms name at least
// MinMembers distinct `SessionStatus` members AND map them to a STRING LITERAL
// (a rendered label or a theme resource key). Comments are stripped first
// (SourceCommentStripper, shared with #563), because both StatusMappers and
// this file quote "working" / "MochaYellow" in prose and a scanner that read
// the prose would grade the canonical table as a second one.
//
// A `SessionStatus → SomeEnum` arm is deliberately NOT graded — see the
// KNOWN_DUPLICATES note on SessionDotState below.
//
// PERIMETER
// ---------
// `ScanRoots` is `src` + `apps` ONLY. `contrib/` is outside CI and outside
// support by owner decision, so it is neither scanned nor expected to be clean
// here — the same deliberate gap #563 recorded.
//
// KNOWN_DUPLICATES
// ----------------
// One real duplicate survives #663 and is listed with the reason it cannot be
// removed here. An allowlist that can rot into a blanket permission is not an
// allowlist, so `RecordedDuplicates_AreStillReal` fails when an entry stops
// being true — the entry has to be deleted in the same commit that fixes it.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan is really walking a checkout, really found the
//      canonical file, and every `SessionStatus` member resolves to a non-empty
//      label and a non-empty brush key (read by reflection, so a member added
//      later is covered without editing this test).
//   2. NonVacuity_Scan_DetectsASecondTableInSyntheticSource — the POSITIVE
//      CONTROL. The probe is handed a second `SessionStatus`→label table in a
//      non-canonical file and MUST report it, plus four snippets it must NOT
//      report. A probe whose matchers stopped working reports nothing and the
//      rule goes green while enforcing nothing.
//
// DELIBERATELY NOT ENFORCED HERE
// ------------------------------
// The canonical table's discard arm still ANSWERS ("_ => \"idle\"",
// "_ => \"MochaOverlay0\"") rather than throwing, the way
// `ToolCallStateToBrushKey` does for ToolCallState. Turning it into a throw is
// a behaviour change for a corrupt `SessionStatus` value — today it renders as
// idle, after the change it would throw inside a binding — and that is a
// separate decision from "the table lives in one place". It is recorded here
// as a follow-up rather than done silently in a unification commit.

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Converters;

namespace Harbor.Architecture.Tests;

/// <summary>One <c>switch</c> over <see cref="SessionStatus" /> the probe found.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line of the <c>switch</c> keyword.</param>
/// <param name="Members">Distinct <c>SessionStatus</c> members the arms named.</param>
internal sealed record SessionStatusTableSite(
    string File,
    int Line,
    IReadOnlyList<string> Members);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="Tables">Every <c>SessionStatus</c>→string table found, in scan order.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
internal sealed record SessionStatusScanReport(
    IReadOnlyList<SessionStatusTableSite> Tables,
    int FilesScanned);

/// <summary>Finds <c>SessionStatus</c>-to-rendered-string switches across the tree.</summary>
internal static partial class SessionStatusTableProbe
{
    /// <summary>
    ///     The one file allowed to map <see cref="SessionStatus" /> to a label or
    ///     a brush key. Everything else calls
    ///     <c>StatusMappers.SessionStatusToText</c> /
    ///     <c>StatusMappers.SessionStatusToBrushKey</c>.
    /// </summary>
    internal const string CanonicalFile =
        "src/Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs";

    /// <summary>
    ///     A switch must name at least this many distinct <c>SessionStatus</c>
    ///     members before it counts as a table over that union. Without the floor
    ///     a two-armed switch elsewhere that happens to test a status would be
    ///     graded for a table it never claimed to be.
    /// </summary>
    internal const int MinMembers = 2;

    /// <summary>Repository roots the scan walks. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] ScanRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", "external", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Duplicates that exist today and that <b>#663 could not remove</b>, each
    ///     with the reason. An entry that stops being true fails
    ///     <c>RecordedDuplicates_AreStillReal</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SubagentsModel.StatusText</c> spells a fourth copy of the
    ///         labels and had already drifted (<c>Working</c> → "running"). It
    ///         cannot be collapsed in this issue: the canonical table lives in
    ///         <c>Harbor.Ui.Framework.ViewModels</c>, and
    ///         <c>Harbor.Ui.Framework.Projection</c> — the project holding
    ///         SubagentsModel — does NOT reference ViewModels and is not allowed
    ///         to (both are Presentation-layer siblings in
    ///         docs/ARCHITECTURE_LAYERS.md §2). Collapsing it means either
    ///         adding that edge or relocating the canonical table downwards, and
    ///         both are larger architectural changes than this issue. Its
    ///         CellForge test pins "running"; moving the label is a visible
    ///         output change that needs its own decision. Follow-up, recorded
    ///         here rather than absorbed silently.
    ///     </para>
    ///     <para>
    ///         The probe does NOT grade <c>SessionStatus → SessionDotState</c>
    ///         arms at all, because those map to an enum member rather than a
    ///         string literal: <c>SessionDotState.Running</c> is not a rendered
    ///         label or a theme key, so it cannot drift the way a colour string
    ///         can. #663 removed the one such site that lived in a view model
    ///         (<c>SessionCardViewModel.DotState</c>) by moving it next to the
    ///         enum it produces; what remains is
    ///         <c>StatusDot</c>'s own presentation state, which is a control
    ///         concern and not a second spelling of the domain status.
    ///     </para>
    /// </remarks>
    internal static readonly FrozenDictionary<string, string> KnownDuplicates =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Harbor.Ui.Framework.Projection/Projection/SubagentsModel.cs"] =
                "SessionStatus→label copy, drifted (Working→\"running\"). Cannot call " +
                "StatusMappers: Harbor.Ui.Framework.Projection does not reference " +
                "Harbor.Ui.Framework.ViewModels and both are Presentation siblings. " +
                "Needs a cross-project call or a downward move of the canonical table.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static SessionStatusScanReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new SessionStatusScanReport([], 0);
        }

        var tables = new List<SessionStatusTableSite>();
        int scanned = 0;

        foreach (string file in EnumerateSources(repoRoot))
        {
            string relative = MakeRelative(repoRoot, file);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            scanned++;
            ScanSource(relative, lines, tables);
        }

        return new SessionStatusScanReport(tables, scanned);
    }

    /// <summary>
    ///     Scans already-read lines. Exposed so the positive control drives the
    ///     REAL matcher (comment stripping included) instead of a second
    ///     implementation of it, which is the only way "it can fail" means
    ///     anything.
    /// </summary>
    internal static void ScanSource(
        string relativeFile,
        string[] lines,
        List<SessionStatusTableSite> tables)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);

        int n = clean.Length;
        var depthBefore = new int[n];
        int depth = 0;
        for (int i = 0; i < n; i++)
        {
            depthBefore[i] = depth;
            depth += clean[i].Count(c => c == '{') - clean[i].Count(c => c == '}');
        }

        for (int i = 0; i < n; i++)
        {
            if (!clean[i].Contains("switch", StringComparison.Ordinal))
            {
                continue;
            }

            int start = ArmBlockStart(clean, i);
            if (start < 0)
            {
                continue;
            }

            // Brace depth INSIDE the arm block, so the scan cannot run past the
            // block's closing brace and absorb a later switch in the same method.
            int inner = depthBefore[start] + 1;
            int end = start + 1;
            while (end < n && depthBefore[end] >= inner)
            {
                end++;
            }

            var members = new SortedSet<string>(StringComparer.Ordinal);
            bool mapsToString = false;
            for (int k = start; k < end; k++)
            {
                string line = clean[k].Trim();
                foreach (Match match in SessionStatusSwitchArm().Matches(line))
                {
                    members.Add(match.Groups["member"].Value);
                    string tail = line[(match.Index + match.Length)..];
                    if (FirstLiteral(tail) is not null)
                    {
                        mapsToString = true;
                    }
                }

                // A statement-form `case SessionStatus.X:` puts the mapped
                // expression on the NEXT line, so the tail is read from there.
                foreach (Match match in SessionStatusCaseArm().Matches(line))
                {
                    members.Add(match.Groups["member"].Value);
                    string tail = (match.Index + match.Length < line.Length ? line[(match.Index + match.Length)..] : string.Empty)
                                  + (k + 1 < end ? " " + clean[k + 1].Trim() : string.Empty);
                    if (FirstLiteral(tail) is not null)
                    {
                        mapsToString = true;
                    }
                }
            }

            if (members.Count < MinMembers || !mapsToString)
            {
                continue;
            }

            tables.Add(new SessionStatusTableSite(relativeFile, i + 1, [.. members]));
        }
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (string root in ScanRoots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                if (SkippedDirectories.Any(dir =>
                        path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    /// <summary>
    ///     Index of the line whose brace opens the arm block — the <c>switch</c>
    ///     line itself when it carries the brace, otherwise one of the next few
    ///     lines (the expression form, where the brace follows the pattern).
    /// </summary>
    private static int ArmBlockStart(string[] clean, int switchLine)
    {
        if (clean[switchLine].Contains('{'))
        {
            return switchLine;
        }

        for (int k = switchLine; k < Math.Min(switchLine + 5, clean.Length); k++)
        {
            if (clean[k].Contains('{'))
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>
    ///     A <c>SessionStatus</c> arm in expression form:
    ///     <c>SessionStatus.Working =&gt; "…"</c>. It cannot match a call site
    ///     such as <c>SetStatus(id, SessionStatus.Error)</c>, because that has a
    ///     <c>,</c> where the <c>=&gt;</c> must be.
    /// </summary>
    [GeneratedRegex(@"\bSessionStatus\s*\.\s*(?<member>\w+)\s*=>")]
    private static partial Regex SessionStatusSwitchArm();

    /// <summary>
    ///     A <c>SessionStatus</c> arm in statement form:
    ///     <c>case SessionStatus.Working:</c>. The <c>case</c> keyword is
    ///     REQUIRED, so a ternary such as <c>running ? SessionStatus.Working :
    ///     SessionStatus.Idle</c> is not mistaken for a table.
    /// </summary>
    [GeneratedRegex(@"\bcase\s+SessionStatus\s*\.\s*(?<member>\w+)\s*:")]
    private static partial Regex SessionStatusCaseArm();

    [GeneratedRegex(@"""(?<literal>[^""\\]*)""")]
    private static partial Regex StringLiteral();

    /// <summary>The first string literal's contents, or <c>null</c> when the tail holds none.</summary>
    private static string? FirstLiteral(string tail)
    {
        Match match = StringLiteral().Match(tail);
        return match.Success ? match.Groups["literal"].Value : null;
    }

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard for issue #663: a <see cref="SessionStatus" /> is rendered by exactly
///     one table, so the colour of a working session cannot be one thing in one
///     UI layer and another thing in the next.
/// </summary>
public sealed class SessionStatusTableRule
{
    private static readonly Lazy<SessionStatusScanReport> Report = new(
        () => SessionStatusTableProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     Exactly one file under <c>src/</c> + <c>apps/</c> maps
    ///     <see cref="SessionStatus" /> to a rendered label or a brush key,
    ///     outside the recorded exceptions. Every other site calls
    ///     <c>StatusMappers</c>, so a status added or re-coloured in one place
    ///     reaches every surface.
    /// </summary>
    [Test]
    public async Task SessionStatusTable_HasExactlyOneDefinitionSite()
    {
        string[] definitionSites = [.. Report.Value.Tables
            .Select(t => t.File)
            .Where(f => !SessionStatusTableProbe.KnownDuplicates.ContainsKey(f))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)];

        // Compared as a joined scalar, not as an array: the set is already
        // distinct and sorted, so the join is order-deterministic, and the
        // failure message then names the offenders outright.
        await Assert.That(string.Join(" | ", definitionSites))
            .IsEqualTo(SessionStatusTableProbe.CanonicalFile)
            .Because(
                "a session status is ONE domain concept, so its label and its colour must be "
                + "spelled once. A second table is a second opinion that drifts: #663 found "
                + "Working rendering as \"MochaYellow\" through StatusMappers but as "
                + "AccentPrimaryBrush in SessionItemViewModel — with a doc comment asserting the "
                + "difference was intended, so both layers believed they owned the colour. Call "
                + "StatusMappers.SessionStatusToText / SessionStatusToBrushKey instead of switching "
                + "on the status yourself. Found: "
                + (definitionSites.Length == 0
                    ? "(nothing — the scan graded no table at all, which is its own failure)"
                    : string.Join(" | ", definitionSites)));
    }

    /// <summary>
    ///     Every recorded exception is still a real duplicate. An allowlist that
    ///     outlives the thing it excuses becomes a blanket permission, so the
    ///     entry has to be deleted in the same commit that removes the duplicate.
    /// </summary>
    [Test]
    public async Task RecordedDuplicates_AreStillReal()
    {
        var stale = SessionStatusTableProbe.KnownDuplicates.Keys
            .Where(f => !Report.Value.Tables.Any(t => string.Equals(t.File, f, StringComparison.Ordinal)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        await Assert.That(stale).IsEmpty()
            .Because(
                "these files are excused from SessionStatusTable_HasExactlyOneDefinitionSite as "
                + "carrying a SessionStatus table that could not be removed in #663. If the scan no "
                + "longer finds one, the excuse has expired: delete the entry and make sure the file "
                + "calls StatusMappers. Expired: " + (stale.Count == 0 ? "(none)" : string.Join(", ", stale)));
    }

    /// <summary>
    ///     The scan really walked a checkout, really found the canonical table,
    ///     and every <see cref="SessionStatus" /> member resolves to a non-empty
    ///     label and a non-empty brush key. Members are read BY REFLECTION, so a
    ///     member added later is covered without editing this test — a hand-written
    ///     list would age exactly like the table it grades.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero definition "
                   + "sites and every rule here is satisfied by having nothing to look at");

        SessionStatusScanReport report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter "
                   + "are wrong and every rule here is vacuously green");

        SessionStatusTableSite[] canonical = [.. report.Tables
            .Where(t => string.Equals(t.File, SessionStatusTableProbe.CanonicalFile, StringComparison.Ordinal))];

        await Assert.That(canonical.Length).IsGreaterThan(0)
            .Because($"the scan found no `SessionStatus` table in "
                   + $"{SessionStatusTableProbe.CanonicalFile}, which is where the single definition is "
                   + "supposed to be. If the table moved, update CanonicalFile in the same commit — do "
                   + "not delete the row.");

        foreach (SessionStatus status in Enum.GetValues<SessionStatus>())
        {
            string label = StatusMappers.SessionStatusToText(status);
            string brush = StatusMappers.SessionStatusToBrushKey(status);

            await Assert.That(label).IsNotEmpty()
                .Because($"{status} has no label in the canonical table");
            await Assert.That(brush).IsNotEmpty()
                .Because($"{status} has no brush key in the canonical table");
        }
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed a synthetic second
    ///     <c>SessionStatus</c>→label table in a NON-canonical file and MUST
    ///     report it, plus four snippets it must NOT report. A probe whose
    ///     matchers stopped matching reports nothing, and the rule would go green
    ///     while enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsASecondTableInSyntheticSource()
    {
        // The #663 shape: a second spelling of the labels outside StatusMappers.
        string duplicate = """
            private static string Badge(SessionStatus status) => status switch
            {
                SessionStatus.Working => "working",
                SessionStatus.Done => "done",
                _ => "idle",
            };
            """;

        // A second BRUSH table — the divergence #663 was actually about. It must
        // be caught by the same rule, or the colour half of the concept stays
        // ungoverned while the label half is guarded.
        string duplicateBrush = """
            private static string DotBrush(SessionStatus status) => status switch
            {
                SessionStatus.Working => "AccentPrimaryBrush",
                SessionStatus.Error => "StateErrorBrush",
                _ => "TextTertiaryBrush",
            };
            """;

        // The canonical shape, in the canonical file: it must still be SEEN, or
        // rule 1 passes because the probe finds nothing rather than because
        // there is one table.
        string canonical = """
            public static string SessionStatusToText(SessionStatus status) => status switch
            {
                SessionStatus.Working => "working",
                SessionStatus.Done => "done",
                _ => "idle",
            };
            """;

        // A switch over another enum must be left alone.
        string unrelated = """
            private static string Pick(int n) => n switch
            {
                1 => "one",
                2 => "two",
                _ => "many",
            };
            """;

        // A ternary over SessionStatus is not a table — the `case` matcher
        // requires the keyword.
        string ternary = """
            private static SessionStatus Pick(bool busy) =>
                busy ? SessionStatus.Working : SessionStatus.Idle;
            """;

        // A call site passes a status as an ARGUMENT; the `=>` matcher requires
        // the arm form.
        string callSite = """
            private static void Mark(string id) => SetStatus(id, SessionStatus.Error);
            """;

        // A SessionStatus → enum arm maps to a MEMBER, not a rendered string, so
        // it is out of this rule's scope. This is the shape
        // SessionCardViewModel.DotState had; it must NOT be reported, or the
        // rule would be grading a translation into a control's own vocabulary.
        string enumArm = """
            private static SessionDotState Dot(SessionStatus status) => status switch
            {
                SessionStatus.Working => SessionDotState.Running,
                SessionStatus.Done => SessionDotState.Done,
                _ => SessionDotState.Idle,
            };
            """;

        var duplicateTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            "src/Somewhere/Duplicate.cs", duplicate.Split('\n'), duplicateTables);

        var brushTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            "src/Somewhere/DuplicateBrush.cs", duplicateBrush.Split('\n'), brushTables);

        var canonicalTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            SessionStatusTableProbe.CanonicalFile, canonical.Split('\n'), canonicalTables);

        var unrelatedTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            "src/Somewhere/Unrelated.cs", unrelated.Split('\n'), unrelatedTables);

        var ternaryTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            "src/Somewhere/Ternary.cs", ternary.Split('\n'), ternaryTables);

        var callSiteTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            "src/Somewhere/CallSite.cs", callSite.Split('\n'), callSiteTables);

        var enumTables = new List<SessionStatusTableSite>();
        SessionStatusTableProbe.ScanSource(
            "src/Somewhere/EnumArm.cs", enumArm.Split('\n'), enumTables);

        await Assert.That(duplicateTables.Count).IsEqualTo(1)
            .Because("the first snippet is a second SessionStatus-to-label table in a non-canonical "
                   + "file, which is precisely what the rule must reject. A miss means the arm matchers "
                   + "or the block-depth walk stopped working and the rule enforces nothing.");

        await Assert.That(string.Join(" | ", duplicateTables[0].Members))
            .IsEqualTo("Done | Working")
            .Because("the probe must read the union members the arms name, or it cannot tell a table "
                   + "from an unrelated switch. Scanned: "
                   + string.Join(" | ", duplicateTables[0].Members));

        await Assert.That(brushTables.Count).IsEqualTo(1)
            .Because("the second snippet is a second SessionStatus-to-brush table — the colour "
                   + "divergence #663 was actually about (Working→AccentPrimaryBrush beside the "
                   + "canonical MochaYellow). A rule that guards only the label half leaves the "
                   + "colour half ungoverned.");

        await Assert.That(canonicalTables.Count).IsEqualTo(1)
            .Because("the third snippet is a SessionStatus table in the canonical file, so the probe "
                   + "must still see it — otherwise the rule is passing because the probe finds nothing "
                   + "rather than because there is one table");

        await Assert.That(unrelatedTables.Count).IsEqualTo(0)
            .Because("the fourth snippet switches over `int`, not SessionStatus; a probe that graded it "
                   + "would be grading a switch that never claimed to be a table");

        await Assert.That(ternaryTables.Count).IsEqualTo(0)
            .Because("the fifth snippet is a ternary over SessionStatus, not a switch. The `case` matcher "
                   + "requires the keyword so this cannot be mistaken for a table");

        await Assert.That(callSiteTables.Count).IsEqualTo(0)
            .Because("the sixth snippet passes SessionStatus.Error as an ARGUMENT, and the `=>` matcher "
                   + "requires the arm form so this cannot be mistaken for a table");

        await Assert.That(enumTables.Count).IsEqualTo(0)
            .Because("the seventh snippet maps SessionStatus onto SessionDotState, an enum member rather "
                   + "than a rendered string. It cannot drift the way a colour STRING can, and it is a "
                   + "control's own presentation vocabulary — grading it here would make the rule "
                   + "claim coverage it does not have.");
    }
}
