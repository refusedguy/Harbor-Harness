// LogLevelMnemonicRule.cs — GUARD for issue #563.
//
// THE CONVENTION BEING ENFORCED
// -----------------------------
// The 4-character LogLevel mnemonic (TRAC / DBUG / INFO / WARN / ERRO / CRIT
// / NONE) is a shared rendered string, and a switch that maps `LogLevel` to a
// rendered string is a hand-maintained table. #563 found:
//
//   1. SIX sites spelled the table. `PanelRows.LogRows` and the two
//      `FileLogger.Write` copies in `apps/` each carried their own switch, and
//      the three copies disagreed: the panel sent an unknown level to a
//      4-question-mark sentinel, the file loggers sent it to
//      `level.ToString().ToUpperInvariant()` (so one event rendered as the
//      sentinel in the panel and as "VERBOSE" in the file).
//   2. A consumer re-read the mnemonic out of an ALREADY-RENDERED row by
//      slicing `[13..17]`, which only holds for the panel's own layout. The
//      file-logger layout is `{ts} [TAG] [{thread,3}] {cat}: {msg}`, so the
//      same slice lands on `[` plus the first three tag characters and matches
//      no arm at all.
//
// So the rule is:
//
//   A. There is exactly ONE table that maps `LogLevel` to a rendered string:
//      src/Harbor.Ui.Framework.Abstractions/Diagnostics/LogLevelTag.cs. Every
//      other site calls `LogLevelTag.For(level)`.
//   B. That table's discard arm THROWS rather than answering. A discard that
//      produces a value is what let the four copies answer differently for the
//      same input. The arm itself is mandatory — C# rejects a discard-less
//      switch expression over an enum with CS8524, because `(LogLevel)7` stays
//      constructible even when every declared member is named (the same shape
//      ToolCallStateExtensions uses, #567) — so the rule is about what it
//      computes, not whether it exists.
//   C. The retired 4-question-mark sentinel appears nowhere under `src/` or
//      `apps/`.
//
// WHY A TEXT SCAN AND NOT A COMPILED CHECK
// ----------------------------------------
// (A) and (C) are about a table NOT existing in a second file, which no type
// system can see, and (B) is about what an arm computes rather than which arms
// exist — so all three need the text scan. The scan is deliberately narrow: it
// only grades a `switch` that names at least MIN_MEMBERS distinct `LogLevel`
// members, so a
// `switch` over some other enum is never touched. Comments are stripped before
// matching, so the prose in this very file (and the XML docs that quote the
// mnemonics) cannot be mistaken for a second table.
//
// The stripping itself lives in SourceCommentStripper, shared with
// SessionStatusTableRule (#663): two rules that each carried their own copy of
// that lexer would be two lexers to keep in agreement, which is the same
// drift this rule exists to catch.
//
// PERIMETER
// ---------
// `ScanRoots` is `src` + `apps` ONLY. The three `contrib/tui/*/DiagnosticsView`
// producers and the `contrib/tui/Harbor.Tui.SpectreTui` LogsPanel consumer hold
// four more copies of this table, but `contrib/` is outside CI and outside
// support by owner decision, so it is neither scanned nor expected to be
// clean here. That gap is deliberate and is recorded in the #563 PR, not
// silently absorbed.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the canonical file is really in the scan, and the switch
//      the probe reports for it names every `LogLevel` member read BY REFLECTION
//      (`Enum.GetNames<LogLevel>()`). A hand-written list of members here would
//      age exactly like the table it grades.
//   2. NonVacuity_Scan_DetectsASecondTableInSyntheticSource — the POSITIVE
//      CONTROL. The probe is handed a synthetic snippet holding a second
//      `LogLevel`→string table with a wildcard arm, plus a clean snippet that
//      has none, and MUST report the first and not the second. A scanner whose
//      matchers stopped working would report nothing and the rule would go
//      green while enforcing nothing.

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Harbor.Architecture.Tests;

/// <summary>One <c>switch</c> over <see cref="LogLevel" /> the probe found.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line of the <c>switch</c> keyword.</param>
/// <param name="Members">Distinct <c>LogLevel</c> members the arms named.</param>
/// <param name="MapsToRenderedText">
///     Whether any arm's value is a string literal or a <c>ToString()</c> call —
///     i.e. whether the switch is a rendered-mnemonic table at all.
/// </param>
/// <param name="HasAnsweringDiscard">
///     Whether the arm block contains a <c>_ =&gt;</c> / <c>default:</c> arm that
///     PRODUCES A VALUE rather than throwing. C# cannot express "exhaustive over
///     the named members" of an enum — a discard-less switch expression is
///     rejected with CS8524, an unconditional error, because <c>(LogLevel)7</c>
///     stays constructible even when every declared member is listed. So the
///     canonical table must carry a discard (#567's shape, the same one
///     <c>ToolCallStateExtensions</c> uses); what this rule forbids is a discard
///     that ANSWERS, which is what let the #563 producers disagree.
/// </param>
internal sealed record LevelTableSite(
    string File,
    int Line,
    IReadOnlyList<string> Members,
    bool MapsToRenderedText,
    bool HasAnsweringDiscard);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="Tables">Every <c>LogLevel</c> switch found, in scan order.</param>
/// <param name="SentinelSites">Repo-relative paths that still emit <c>"????"</c>.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
internal sealed record LevelScanReport(
    IReadOnlyList<LevelTableSite> Tables,
    IReadOnlyList<string> SentinelSites,
    int FilesScanned);

/// <summary>Finds <c>LogLevel</c>-to-rendered-text switches across the tree.</summary>
internal static partial class LogLevelMnemonicProbe
{
    /// <summary>
    ///     The one file allowed to map <see cref="LogLevel" /> to rendered text.
    ///     A new producer calls <c>LogLevelTag.For(level)</c>; it does not
    ///     spell a mnemonic.
    /// </summary>
    internal const string CanonicalFile =
        "src/Harbor.Ui.Framework.Abstractions/Diagnostics/LogLevelTag.cs";

    /// <summary>
    ///     The retired fallback. Four producers rendered an unrecognised level
    ///     as this and two rendered it as the uppercased enum name, so the same
    ///     event read <c>????</c> in one place and <c>VERBOSE</c> in another. It
    ///     is a sentinel, not a level, and it must not come back.
    /// </summary>
    internal const string RetiredSentinel = "\"????\"";

    /// <summary>
    ///     A switch must name at least this many distinct <c>LogLevel</c> members
    ///     before it counts as a table over that union. Without the floor a
    ///     two-armed switch elsewhere that happens to test a level would be
    ///     graded for a table it never claimed to be.
    /// </summary>
    internal const int MinMembers = 2;

    /// <summary>Repository roots the scan walks. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] ScanRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", "external", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static LevelScanReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new LevelScanReport([], [], 0);
        }

        var tables = new List<LevelTableSite>();
        var sentinels = new List<string>();
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
            ScanSource(relative, lines, tables, sentinels);
        }

        return new LevelScanReport(tables, sentinels, scanned);
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
        List<LevelTableSite> tables,
        List<string> sentinels)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);

        for (int i = 0; i < clean.Length; i++)
        {
            if (clean[i].Contains(RetiredSentinel, StringComparison.Ordinal)
                && !sentinels.Contains(relativeFile, StringComparer.Ordinal))
            {
                sentinels.Add(relativeFile);
            }
        }

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
            bool mapsToText = false;
            bool answeringDiscard = false;
            for (int k = start; k < end; k++)
            {
                string line = clean[k].Trim();
                foreach (Match match in LogLevelSwitchArm().Matches(line))
                {
                    members.Add(match.Groups["member"].Value);
                    string tail = line[(match.Index + match.Length)..];
                    if (FirstLiteral(tail) is not null || tail.Contains("ToString(", StringComparison.Ordinal))
                    {
                        mapsToText = true;
                    }
                }

                // A statement-form `case LogLevel.X:` puts the mapped expression on
                // the NEXT line, so the tail is read from there instead.
                foreach (Match match in LogLevelCaseArm().Matches(line))
                {
                    members.Add(match.Groups["member"].Value);
                    string tail = (match.Index + match.Length < line.Length ? line[(match.Index + match.Length)..] : string.Empty)
                                  + (k + 1 < end ? " " + clean[k + 1].Trim() : string.Empty);
                    if (FirstLiteral(tail) is not null || tail.Contains("ToString(", StringComparison.Ordinal))
                    {
                        mapsToText = true;
                    }
                }

                if (IsAnsweringDiscard(line))
                {
                    answeringDiscard = true;
                }
            }

            if (members.Count < MinMembers || !mapsToText)
            {
                continue;
            }

            tables.Add(new LevelTableSite(relativeFile, i + 1, [.. members], true, answeringDiscard));
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
    ///     A discard arm that ANSWERS, as opposed to one that throws. The
    ///     canonical table is required to carry a discard — C# rejects a
    ///     discard-less switch expression over an enum with CS8524 — so the rule
    ///     is not "no discard" but "the discard must not invent an answer".
    /// </summary>
    private static bool IsAnsweringDiscard(string line)
    {
        foreach (string prefix in (string[])["_ =>", "default =>", "default:"])
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string tail = line[prefix.Length..].TrimStart();
            return !(tail.StartsWith("throw", StringComparison.Ordinal)
                     || tail.StartsWith("//", StringComparison.Ordinal));
        }

        return false;
    }

    /// <summary>
    ///     A <c>LogLevel</c> arm in expression form: <c>LogLevel.Warning =&gt; "…"</c>.
    ///     It cannot match a call site such as <c>Log(LogLevel.Error, "…", m)</c>,
    ///     because that has a <c>,</c> where the <c>=&gt;</c> must be.
    /// </summary>
    [GeneratedRegex(@"\bLogLevel\s*\.\s*(?<member>\w+)\s*=>")]
    private static partial Regex LogLevelSwitchArm();

    /// <summary>
    ///     A <c>LogLevel</c> arm in statement form: <c>case LogLevel.Warning:</c>.
    ///     The <c>case</c> keyword is REQUIRED, and that is the whole point — a
    ///     ternary such as <c>attached ? LogLevel.Debug : LogLevel.Information</c>
    ///     has the same <c>LogLevel.X :</c> shape and is not a switch at all. One
    ///     regex per form, rather than one alternation reusing the group name,
    ///     because relying on .NET's duplicate-named-group rule buys nothing and a
    ///     [GeneratedRegex] source-generator failure is a hard build error.
    /// </summary>
    [GeneratedRegex(@"\bcase\s+LogLevel\s*\.\s*(?<member>\w+)\s*:")]
    private static partial Regex LogLevelCaseArm();

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
///     Guard for issue #563: the <see cref="LogLevel" /> mnemonic is rendered
///     from exactly one table, that table answers for every member by name
///     rather than through a sentinel, and the sentinel is gone.
/// </summary>
public sealed class LogLevelMnemonicRule
{
    private static readonly Lazy<LevelScanReport> Report = new(
        () => LogLevelMnemonicProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     Exactly one file under <c>src/</c> + <c>apps/</c> maps
    ///     <see cref="LogLevel" /> to rendered text. Every other producer calls
    ///     <c>LogLevelTag.For</c>, so a mnemonic added in one place reaches
    ///     every row and a seventh level cannot go missing in one of them.
    /// </summary>
    [Test]
    public async Task LevelMnemonic_HasExactlyOneDefinitionSite()
    {
        string[] definitionSites = Report.Value.Tables
            .Select(t => t.File)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        // Compared as a joined scalar, not as an array. The set is already
        // distinct and sorted, so the join is order-deterministic, and this
        // dodges any question about how a collection assertion compares two
        // arrays — the failure message then names the offenders outright.
        await Assert.That(string.Join(" | ", definitionSites))
            .IsEqualTo(LogLevelMnemonicProbe.CanonicalFile)
            .Because(
                "the mnemonic is a shared string format, so its spelling must live once. A second "
                + "site is a second table that can drift from the first — which is exactly what #563 "
                + "found: the panel answered an unknown level with a 4-question-mark sentinel while "
                + "the file loggers answered `level.ToString().ToUpperInvariant()`, so one event "
                + "rendered as the sentinel in the logs panel and as \"VERBOSE\" in the log file. "
                + "Call `LogLevelTag.For(level)` instead of switching on the level yourself. "
                + "Found: " + (definitionSites.Length == 0
                    ? "(nothing — the scan graded no table at all, which is its own failure)"
                    : string.Join(" | ", definitionSites)));
    }

    /// <summary>
    ///     The one table's discard arm THROWS rather than answering. A discard
    ///     that produced a value is what let the four #563 producers disagree
    ///     about the same level — the panel answered with a sentinel, the file
    ///     loggers with the uppercased enum name. A discard is mandatory here
    ///     (CS8524 rejects a discard-less switch expression over an enum), so
    ///     the rule is about what it does, not whether it exists.
    /// </summary>
    [Test]
    public async Task LevelMnemonicTable_HasNoAnsweringDiscard()
    {
        var offenders = Report.Value.Tables
            .Where(t => t.File == LogLevelMnemonicProbe.CanonicalFile && t.HasAnsweringDiscard)
            .Select(t => $"{t.File}:{t.Line}")
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a discard arm in the canonical table that PRODUCES A VALUE invents an answer for a "
                + "level it does not name, which is how the six #563 producers came to disagree about "
                + "the same value. The arm has to exist — C# rejects a discard-less switch expression "
                + "over an enum with CS8524 — so it must throw, the way "
                + "ToolCallStateExtensions.IsTerminal does (#567). Offending sites: "
                + (offenders.Count == 0 ? "(none)" : string.Join(", ", offenders)));
    }

    /// <summary>
    ///     The <c>"????"</c> sentinel is retired. It is not a level; it was
    ///     "this code path did not know", spelled into a column a consumer then
    ///     had to allowlist.
    /// </summary>
    [Test]
    public async Task RetiredQuestionMarkSentinel_IsGone()
    {
        await Assert.That(Report.Value.SentinelSites).IsEmpty()
            .Because(
                "\"????\" was the fallback four producers used for a level they did not recognise, "
                + "and it leaked into a consumer's allowlist as if it were a level. An unrecognised "
                + "level is now a thrown/failed mapping inside LogLevelTag, not a rendered token. "
                + "Still emitted by: " + string.Join(", ", Report.Value.SentinelSites));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really found the canonical
    ///     table, naming every <see cref="LogLevel" /> member read BY REFLECTION.
    ///     A hand-written member list here would age exactly like the table it
    ///     grades, which is the failure #578 rule 4 describes.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero definition "
                   + "sites and every rule below is satisfied by having nothing to look at");

        LevelScanReport report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter "
                   + "are wrong and every rule here is vacuously green");

        LevelTableSite[] canonical = [.. report.Tables
            .Where(t => t.File == LogLevelMnemonicProbe.CanonicalFile)];

        await Assert.That(canonical.Length).IsGreaterThan(0)
            .Because($"the scan found no `LogLevel` table in {LogLevelMnemonicProbe.CanonicalFile}, "
                   + "which is where the single definition is supposed to be. If the mnemonic moved, "
                   + "update CanonicalFile in the same commit — do not delete the row.");

        string[] named = [.. canonical[0].Members];
        string[] expected = [.. Enum.GetNames<LogLevel>().OrderBy(n => n, StringComparer.Ordinal)];

        // Joined scalars, not array-to-array: the member set is sorted on both
        // sides, so the join is deterministic and the failure message lists the
        // members outright.
        await Assert.That(string.Join(" | ", named)).IsEqualTo(string.Join(" | ", expected))
            .Because("the canonical table must name every LogLevel member (read by reflection, so a "
                   + "member added later is covered without editing this test). A member missing here "
                   + $"means {LogLevelMnemonicProbe.CanonicalFile} would render it through a wildcard. "
                   + $"Scanned: {string.Join(" | ", named)}. Expected: {string.Join(" | ", expected)}.");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed two synthetic snippets
    ///     that both map <see cref="LogLevel" /> to text: one with a wildcard
    ///     arm in a NON-canonical file, one without a wildcard arm in the
    ///     canonical file. It MUST report the first (as a second definition
    ///     site, and as a wildcard) and MUST NOT report the second. A probe
    ///     whose matchers stopped matching reports nothing, and the rule would
    ///     go green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsASecondTableInSyntheticSource()
    {
        string duplicate = """
            private static string Tag(LogLevel level) => level switch
            {
                LogLevel.Trace => "TRAC",
                LogLevel.Warning => "WARN",
                _ => "????",
            };
            """;

        // The shape the canonical table is REQUIRED to have: every member named,
        // and a discard that throws rather than answering. C# rejects a
        // discard-less switch expression over an enum with CS8524, so the probe
        // must recognise this as clean, or rule 2 would demand the impossible.
        string canonical = """
            private static string Tag(LogLevel level) => level switch
            {
                LogLevel.Trace => "TRAC",
                LogLevel.Warning => "WARN",
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, "no mnemonic"),
            };
            """;

        // A `switch` that never mentions LogLevel must be left alone, or the
        // probe is not scoped to the union it claims to grade.
        string unrelated = """
            private static string Pick(int n) => n switch
            {
                1 => "one",
                2 => "two",
                _ => "many",
            };
            """;

        // A ternary over LogLevel is not a table — the `case` matcher requires
        // the keyword precisely so this is not reported.
        string ternary = """
            private static LogLevel Pick(bool attached) =>
                attached ? LogLevel.Debug : LogLevel.Information;
            """;

        // The call-site shape `Log(LogLevel.Error, "…", msg)` is not an arm.
        string callSite = """
            private static void Report(string m) => Log(LogLevel.Error, "Harbor.Probe", m);
            """;

        var duplicateTables = new List<LevelTableSite>();
        var duplicateSentinels = new List<string>();
        LogLevelMnemonicProbe.ScanSource("src/Somewhere/Duplicate.cs", duplicate.Split('\n'),
            duplicateTables, duplicateSentinels);

        var canonicalTables = new List<LevelTableSite>();
        var canonicalSentinels = new List<string>();
        LogLevelMnemonicProbe.ScanSource(LogLevelMnemonicProbe.CanonicalFile, canonical.Split('\n'),
            canonicalTables, canonicalSentinels);

        var unrelatedTables = new List<LevelTableSite>();
        var unrelatedSentinels = new List<string>();
        LogLevelMnemonicProbe.ScanSource("src/Somewhere/Unrelated.cs", unrelated.Split('\n'),
            unrelatedTables, unrelatedSentinels);

        var ternaryTables = new List<LevelTableSite>();
        var ternarySentinels = new List<string>();
        LogLevelMnemonicProbe.ScanSource("src/Somewhere/Ternary.cs", ternary.Split('\n'),
            ternaryTables, ternarySentinels);

        var callSiteTables = new List<LevelTableSite>();
        var callSiteSentinels = new List<string>();
        LogLevelMnemonicProbe.ScanSource("src/Somewhere/CallSite.cs", callSite.Split('\n'),
            callSiteTables, callSiteSentinels);

        await Assert.That(duplicateTables.Count).IsEqualTo(1)
            .Because("the first snippet is a second LogLevel-to-text table in a non-canonical file, "
                   + "which is precisely what rule 1 must reject. A miss means the arm matchers or "
                   + "the block-depth walk stopped working and the rule enforces nothing.");

        await Assert.That(duplicateTables[0].HasAnsweringDiscard).IsTrue()
            .Because("the first snippet's sentinel arm is the divergent, ANSWERING fallback that "
                   + "rule 2 rejects");

        await Assert.That(string.Join(" | ", duplicateTables[0].Members))
            .IsEqualTo("Trace | Warning")
            .Because("the probe must read the union members the arms name, or rule 2 cannot tell a "
                   + "table from an unrelated switch. Scanned: "
                   + string.Join(" | ", duplicateTables[0].Members));

        await Assert.That(string.Join(" | ", duplicateSentinels))
            .IsEqualTo("src/Somewhere/Duplicate.cs")
            .Because("the first snippet emits the retired 4-question-mark sentinel, which rule 3 must "
                   + "catch on its own so it cannot reappear outside a switch. Scanned: "
                   + (duplicateSentinels.Count == 0 ? "(nothing)" : string.Join(" | ", duplicateSentinels)));

        await Assert.That(canonicalTables.Count).IsEqualTo(1)
            .Because("the second snippet is a LogLevel-to-text table in the canonical file, so the "
                   + "probe must still see it — otherwise rule 1 is passing because the probe finds "
                   + "nothing rather than because there is one table");

        await Assert.That(canonicalTables[0].HasAnsweringDiscard).IsFalse()
            .Because("the second snippet's discard THROWS, which is the shape the canonical table is "
                   + "required to have — rule 2 must not fire on it");

        await Assert.That(unrelatedTables.Count).IsEqualTo(0)
            .Because("the third snippet switches over `int`, not LogLevel; a probe that graded it "
                   + "would be grading a switch that never claimed to be a table");

        await Assert.That(ternaryTables.Count).IsEqualTo(0)
            .Because("the fourth snippet is a ternary over LogLevel, not a switch. The `case` matcher "
                   + "requires the keyword so this cannot be mistaken for a table");

        await Assert.That(callSiteTables.Count).IsEqualTo(0)
            .Because("the fifth snippet passes LogLevel.Error as an ARGUMENT, and the `=>` matcher "
                   + "requires the arm form so this cannot be mistaken for a table");
    }
}
