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
//      the three copies disagreed: the panel sent an unknown level to the
//      sentinel "????", the file loggers sent it to
//      `level.ToString().ToUpperInvariant()` (so the same event rendered as
//      "????" in the panel and "VERBOSE" in the file).
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
//   B. That table has NO wildcard arm. A `default` arm is what let the four
//      copies answer differently for the same input, and it is also what would
//      silently swallow the next `LogLevel` member that someone adds.
//   C. The retired "????" sentinel appears nowhere under `src/` or `apps/`.
//
// WHY A TEXT SCAN AND NOT A COMPILED CHECK
// ----------------------------------------
// (B) is genuinely compile-checkable in the canonical file and nowhere else —
// but (A) and (C) are about a table NOT existing in a second file, which no
// type system can see. The scan is deliberately narrow: it only grades a
// `switch` that names at least MIN_MEMBERS distinct `LogLevel` members, so a
// `switch` over some other enum is never touched. Comments are stripped before
// matching, so the prose in this very file (and the XML docs that quote the
// mnemonics) cannot be mistaken for a second table.
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
using System.Text;
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
/// <param name="HasWildcardArm">Whether the arm block contains <c>_ =&gt;</c> or <c>default:</c>.</param>
internal sealed record LevelTableSite(
    string File,
    int Line,
    IReadOnlyList<string> Members,
    bool MapsToRenderedText,
    bool HasWildcardArm);

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
        string[] clean = lines.Select(StripComments).ToArray();

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
            bool wildcard = false;
            for (int k = start; k < end; k++)
            {
                string line = clean[k].Trim();
                foreach (Match match in LogLevelArm().Matches(line))
                {
                    members.Add(match.Groups["member"].Value);
                    string tail = line[(match.Index + match.Length)..];
                    if (FirstLiteral(tail) is not null || tail.Contains("ToString(", StringComparison.Ordinal))
                    {
                        mapsToText = true;
                    }
                }

                if (IsWildcardArm(line))
                {
                    wildcard = true;
                }
            }

            if (members.Count < MinMembers || !mapsToText)
            {
                continue;
            }

            tables.Add(new LevelTableSite(relativeFile, i + 1, [.. members], true, wildcard));
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

    private static bool IsWildcardArm(string line) =>
        line.StartsWith("_ =>", StringComparison.Ordinal)
        || line.StartsWith("default =>", StringComparison.Ordinal)
        || line.StartsWith("default:", StringComparison.Ordinal);

    /// <summary>
    ///     A <c>LogLevel</c> arm in either C# switch form. The <c>case</c> form
    ///     requires the keyword, so a ternary such as
    ///     <c>attached ? LogLevel.Debug : LogLevel.Information</c> cannot be
    ///     mistaken for one, and the <c>=&gt;</c> form cannot match a call site
    ///     such as <c>Log(LogLevel.Error, "…", msg)</c>.
    /// </summary>
    [GeneratedRegex(@"\bLogLevel\s*\.\s*(?<member>\w+)\s*=>|\bcase\s+LogLevel\s*\.\s*(?<member>\w+)\s*:")]
    private static partial Regex LogLevelArm();

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

    /// <summary>Lexical states for <see cref="StripComments" />.</summary>
    private enum StripState
    {
        /// <summary>Ordinary code.</summary>
        Code,

        /// <summary>Inside a <c>"…"</c> string, where <c>\</c> escapes.</summary>
        String,

        /// <summary>Inside a <c>@"…"</c> verbatim string, where <c>""</c> escapes.</summary>
        VerbatimString,

        /// <summary>Inside a <c>'…'</c> char.</summary>
        Char,

        /// <summary>Inside a <c>// …</c> comment, ending at the newline.</summary>
        LineComment,

        /// <summary>Inside a <c>/* … */</c> comment.</summary>
        BlockComment,
    }

    /// <summary>
    ///     Blanks out COMMENTS ONLY, keeping string literals — this scan needs
    ///     the literals, and what it must not match is the prose. Without this
    ///     step the doc comments that quote the mnemonics (including the ones in
    ///     this file) would read as a second table.
    /// </summary>
    private static string StripComments(string line)
    {
        var output = new StringBuilder(line.Length);
        StripState state = StripState.Code;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            switch (state)
            {
                case StripState.Code:
                    if (c == '/' && next == '/')
                    {
                        state = StripState.LineComment;
                        i++;
                        continue;
                    }

                    if (c == '/' && next == '*')
                    {
                        state = StripState.BlockComment;
                        i++;
                        continue;
                    }

                    if (c == '@' && next == '"')
                    {
                        state = StripState.VerbatimString;
                        output.Append(c);
                        i++;
                        continue;
                    }

                    if (c == '"')
                    {
                        state = StripState.String;
                        output.Append(c);
                        continue;
                    }

                    if (c == '\'')
                    {
                        state = StripState.Char;
                        output.Append(c);
                        continue;
                    }

                    output.Append(c);
                    continue;

                case StripState.String:
                    output.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        output.Append(next);
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = StripState.Code;
                    }

                    continue;

                case StripState.VerbatimString:
                    output.Append(c);
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            output.Append(next);
                            i++;
                        }
                        else
                        {
                            state = StripState.Code;
                        }
                    }

                    continue;

                case StripState.Char:
                    output.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        output.Append(next);
                        i++;
                    }
                    else if (c == '\'')
                    {
                        state = StripState.Code;
                    }

                    continue;

                case StripState.LineComment:
                    if (c == '\n')
                    {
                        state = StripState.Code;
                        output.Append(c);
                    }

                    continue;

                case StripState.BlockComment:
                    if (c == '*' && next == '/')
                    {
                        state = StripState.Code;
                        i++;
                    }

                    continue;

                default:
                    throw new InvalidOperationException($"[log-level-probe] unknown strip state {state}.");
            }
        }

        return output.ToString();
    }
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

        await Assert.That(definitionSites).IsEqualTo(new[] { LogLevelMnemonicProbe.CanonicalFile })
            .Because(
                "the mnemonic is a shared string format, so its spelling must live once. A second "
                + "site is a second table that can drift from the first — which is exactly what #563 "
                + "found: the panel answered an unknown level with \"????\" while the file loggers "
                + "answered `level.ToString().ToUpperInvariant()`, so the same event rendered as "
                + "\"????\" in the logs panel and \"VERBOSE\" in the log file. Call "
                + $"`LogLevelTag.For(level)` from {LogLevelMnemonicProbe.CanonicalFile} instead of "
                + "switching on the level yourself.");
    }

    /// <summary>
    ///     The one table names every <see cref="LogLevel" /> member. A wildcard
    ///     arm is what let four copies answer differently for the same input,
    ///     and it is also what would silently swallow the next member someone
    ///     adds to the enum.
    /// </summary>
    [Test]
    public async Task LevelMnemonicTable_HasNoWildcardArm()
    {
        var offenders = Report.Value.Tables
            .Where(t => t.File == LogLevelMnemonicProbe.CanonicalFile && t.HasWildcardArm)
            .Select(t => $"{t.File}:{t.Line}")
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a `_ =>` / `default:` arm in the canonical table invents an answer for a level it "
                + "does not name, which is how the six #563 producers came to disagree about the "
                + "same value. The C# compiler enforces exhaustiveness here for free — drop the arm. "
                + string.Join(", ", offenders));
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

        await Assert.That(named).IsEqualTo(expected)
            .Because("the canonical table must name every LogLevel member (read by reflection, so a "
                   + "member added later is covered without editing this test). A member missing here "
                   + $"means {LogLevelMnemonicProbe.CanonicalFile} would render it through a wildcard.");
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

        string canonical = """
            private static string Tag(LogLevel level) => level switch
            {
                LogLevel.Trace => "TRAC",
                LogLevel.Warning => "WARN",
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

        await Assert.That(duplicateTables[0].HasWildcardArm).IsTrue()
            .Because("the first snippet's `_ => \"????\"` arm is the divergent fallback rule 2 "
                   + "rejects");

        await Assert.That(duplicateTables[0].Members).IsEqualTo(new[] { "Trace", "Warning" })
            .Because("the probe must read the union members the arms name, or rule 2 cannot tell a "
                   + "table from an unrelated switch");

        await Assert.That(duplicateSentinels).IsEqualTo(new[] { "src/Somewhere/Duplicate.cs" })
            .Because("the first snippet emits the retired \"????\" sentinel, which rule 3 must catch "
                   + "on its own so it cannot reappear outside a switch");

        await Assert.That(canonicalTables.Count).IsEqualTo(1)
            .Because("the second snippet is a LogLevel-to-text table in the canonical file, so the "
                   + "probe must still see it — otherwise rule 1 is passing because the probe finds "
                   + "nothing rather than because there is one table");

        await Assert.That(canonicalTables[0].HasWildcardArm).IsFalse()
            .Because("the second snippet has no wildcard arm, so rule 2 must not fire on it");

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
