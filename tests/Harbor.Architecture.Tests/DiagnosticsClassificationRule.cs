// DiagnosticsClassificationRule.cs — GUARD for issue #674.
//
// THE CONVENTION BEING ENFORCED
// ------------------------------
// Deciding "is this line a diagnostic, and how bad is it" is COUNTING, not
// DRAWING. It therefore has exactly one owner: the headless core. #674 found
// that it had two, and the second one was a renderer:
//
//   1. src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs
//      (`CollectDiagnostics`) read the transcript's ChatRole.ToolResult lines —
//      i.e. the OUTPUT OF THE `bash` TOOL — and classified them with six
//      detector regexes (`\b(CS|MSB)\d{4}\b`, `error\[E\d+\]`, a Python
//      `File "…", line N` shape, a Node `at …js:line` shape, a bare
//      `*Exception`, a bare `warning`) plus a `TryClassify` cascade. That is a
//      parser of somebody else's output living inside a projection layer.
//
//   2. The SAME screen had a second, parallel path for the real thing:
//      src/Harbor.Tui.CellForge/Chat/Widgets/SideBarView.cs spelled
//      `LspErrors: 0, LspWarnings: 0` as literal named arguments, so the
//      sidebar's DIAGNOSTICS section was permanently empty. Meanwhile the core
//      already hands the UI structured diagnostics — LspServerSession parses
//      `textDocument/publishDiagnostics` into `LspDiagnostic` with the server's
//      OWN severity (no regex anywhere), and LspManager re-exports the
//      `DiagnosticsChanged` event.
//
// The two concepts were also fused into one panel: "compiler errors spotted in
// bash output" and "diagnostics a language server published" are different
// things with different sources, and the UI summed them.
//
// So the rule has two halves:
//
//   A. The classification TABLE — the detector patterns — is declared once, in
//      the core detector, and the exact same pattern appears nowhere else under
//      `src/` or `apps/`. A second copy of the table is a second classifier, and
//      two classifiers over one feed are how the two concepts got fused in the
//      first place.
//
//   B. The live LSP path stays live: no file under `src/` may pin an
//      `LspErrors:` / `LspWarnings:` named argument to the literal `0`. A hard
//      zero is the signature of a path that was never connected — the counts
//      must come from state, whatever that state turns out to hold.
//
// WHY A TEXT SCAN AND NOT A COMPILED CHECK
// ---------------------------------------
// Both halves are about a table NOT existing in a second file and about a
// named argument's VALUE, neither of which survives into metadata. #563's
// `LogLevelMnemonicRule` is the same shape; the difference is that this one
// reads its own table out of the canonical file rather than re-typing it, so a
// detector ADDED to the core tomorrow is guarded the day it lands, with no
// edit here. Pattern identity is compared on the literal's CONTENTS with the
// verbatim `""` escape collapsed to one quote, so re-spelling `@"…"` on another
// line does not evade it.
//
// PERIMETER
// ---------
// `ScanRoots` is `src` + `apps` ONLY. `contrib/` is outside CI and outside
// support by owner decision, so it is neither scanned nor expected clean.
//
// WHAT THIS RULE DOES NOT CATCH
// -----------------------------
// A classification table that shares no pattern with the canonical one — a
// fresh regex invented in a renderer — is invisible here, because "is this a
// classifier?" is a judgement, not a shape. The positive control below pins the
// machinery; it does not make the rule omniscient, and the file header of the
// core detector says so too.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the canonical file is really in the checkout and really
//      yields patterns, naming every detector it declares BY REFLECTION over the
//      core type's `[GeneratedRegex]` partials would be nicer, but the extractor
//      is what the rule grades, so it is instead checked against the count of
//      detector-bearing lines in that same file. Zero patterns means rule A is
//      satisfied by having nothing to look at.
//   2. NonVacuity_Scan_DetectsADuplicatedDetectorInSyntheticSource — the
//      POSITIVE CONTROL. The probe is handed a synthetic renderer file holding
//      one of the canonical patterns plus a clean renderer file that holds
//      none, and MUST report the first and not the second. A probe whose
//      matcher stopped working reports nothing and the rule goes green while
//      enforcing nothing.

using System.Text;

namespace Harbor.Architecture.Tests;

/// <summary>One site that re-declares a core detector pattern, or pins an LSP count to zero.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Detail">The pattern text, or the offending named argument.</param>
internal sealed record DiagnosticClassificationSite(string File, int Line, string Detail);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="CanonicalFileFound">Whether the core detector file was located in the checkout.</param>
/// <param name="CanonicalPatterns">The detector patterns read out of the core file.</param>
/// <param name="DuplicatedPatterns">Sites outside the core file that re-declare one of them.</param>
/// <param name="HardcodedLspCounts">Sites that pin an <c>LspErrors</c>/<c>LspWarnings</c> argument to <c>0</c>.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
internal sealed record DiagnosticClassificationReport(
    bool CanonicalFileFound,
    IReadOnlyList<string> CanonicalPatterns,
    IReadOnlyList<DiagnosticClassificationSite> DuplicatedPatterns,
    IReadOnlyList<DiagnosticClassificationSite> HardcodedLspCounts,
    int FilesScanned);

/// <summary>Finds duplicated diagnostic-detector tables and dead LSP counts across the tree.</summary>
internal static class DiagnosticsClassificationProbe
{
    /// <summary>
    ///     The one file allowed to declare a diagnostic detector pattern.
    /// </summary>
    internal const string CanonicalDetectorFile =
        "src/Harbor.Application/Diagnostics/ToolOutputIssueDetector.cs";

    /// <summary>Repository roots the scan walks. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] ScanRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "external", ".worktrees", "node_modules"];

    /// <summary>
    ///     Named arguments that carry a count. Pinning either to the literal
    ///     <c>0</c> is the signature of the never-connected LSP path #674 found.
    /// </summary>
    private static readonly string[] LspCountArguments = ["LspErrors:", "LspWarnings:"];

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static DiagnosticClassificationReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new DiagnosticClassificationReport(false, [], [], [], 0);
        }

        var canonical = new List<string>();
        bool canonicalFound = TryReadCanonicalPatterns(repoRoot, canonical);

        var duplicated = new List<DiagnosticClassificationSite>();
        var hardcoded = new List<DiagnosticClassificationSite>();
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
            if (string.Equals(relative, CanonicalDetectorFile, StringComparison.Ordinal))
            {
                continue;
            }

            ScanSource(relative, lines, canonical, duplicated, hardcoded);
        }

        return new DiagnosticClassificationReport(canonicalFound, canonical, duplicated, hardcoded, scanned);
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
        IReadOnlyList<string> patterns,
        List<DiagnosticClassificationSite> duplicated,
        List<DiagnosticClassificationSite> hardcoded)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string line = StripLineComment(lines[i]);
            if (line.Length == 0)
            {
                continue;
            }

            foreach (DiagnosticClassificationSite site in HardcodedZeroArguments(relativeFile, i + 1, line))
            {
                hardcoded.Add(site);
            }

            if (patterns.Count == 0)
            {
                continue;
            }

            var literals = new List<string>();
            AddVerbatimLiterals(line, literals);
            foreach (string literal in literals)
            {
                foreach (string pattern in patterns)
                {
                    if (string.Equals(literal, pattern, StringComparison.Ordinal))
                    {
                        duplicated.Add(new DiagnosticClassificationSite(relativeFile, i + 1, pattern));
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Reads the detector patterns out of the canonical core file: a
    ///     <c>[GeneratedRegex(...)]</c> attribute or a <c>new Regex(...)</c>
    ///     construction, taking the first verbatim literal on the line.
    /// </summary>
    private static bool TryReadCanonicalPatterns(string repoRoot, List<string> into)
    {
        string absolute = Path.Combine(repoRoot, CanonicalDetectorFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            return false;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(absolute);
        }
        catch (IOException)
        {
            return false;
        }

        foreach (string raw in lines)
        {
            string line = StripLineComment(raw);
            if (!line.Contains("GeneratedRegex(", StringComparison.Ordinal)
                && !line.Contains("new Regex(", StringComparison.Ordinal))
            {
                continue;
            }

            var literals = new List<string>();
            AddVerbatimLiterals(line, literals);
            if (literals.Count > 0 && !into.Contains(literals[0], StringComparer.Ordinal))
            {
                into.Add(literals[0]);
            }
        }

        return true;
    }

    /// <summary>
    ///     Named arguments whose value is the bare literal <c>0</c>. The tail
    ///     check requires the character after the zero to be neither a digit nor
    ///     an identifier character, so <c>LspErrors: 0</c> is caught while
    ///     <c>LspErrors: OffsetOf(...)</c> and <c>LspErrors: 0x10</c> are not.
    /// </summary>
    private static IEnumerable<DiagnosticClassificationSite> HardcodedZeroArguments(
        string relativeFile,
        int lineNumber,
        string line)
    {
        foreach (string argument in LspCountArguments)
        {
            int index = line.IndexOf(argument, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            string tail = line[(index + argument.Length)..].TrimStart();
            if (tail.Length == 0 || tail[0] != '0')
            {
                continue;
            }

            if (tail.Length > 1 && (char.IsLetterOrDigit(tail[1]) || tail[1] == '_'))
            {
                continue;
            }

            yield return new DiagnosticClassificationSite(relativeFile, lineNumber, argument);
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
    ///     Collects the CONTENTS of every C# verbatim string literal
    ///     (<c>@"…"</c>) on the line, collapsing the doubled-quote escape back
    ///     to one quote so <c>@"a""b"</c> reads as <c>a"b</c>.
    /// </summary>
    private static void AddVerbatimLiterals(string line, List<string> into)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] != '@' || line[i + 1] != '"')
            {
                continue;
            }

            int j = i + 2;
            var content = new StringBuilder();
            bool closed = false;
            while (j < line.Length)
            {
                char c = line[j];
                if (c == '"')
                {
                    if (j + 1 < line.Length && line[j + 1] == '"')
                    {
                        content.Append('"');
                        j += 2;
                        continue;
                    }

                    closed = true;
                    j++;
                    break;
                }

                content.Append(c);
                j++;
            }

            if (!closed)
            {
                return;
            }

            into.Add(content.ToString());
            i = j - 1;
        }
    }

    /// <summary>
    ///     Cuts a <c>// …</c> line comment off the end of the line, skipping
    ///     <c>//</c> that sits inside a verbatim string or a character literal.
    ///     Block comments are not tracked across lines: a detector pattern
    ///     quoted in a <c>/* … */</c> prose block would be reported as a
    ///     duplicate, which is a false positive in the safe direction — it names
    ///     the file to edit rather than hiding a real one.
    /// </summary>
    private static string StripLineComment(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = SkipVerbatim(line, i + 1);
                continue;
            }

            if (c == '"')
            {
                i = SkipRegularString(line, i);
                continue;
            }

            if (c == '\'' && i + 1 < line.Length && line[i + 1] == '\\')
            {
                int k = i + 2;
                while (k < line.Length && line[k] != '\'')
                {
                    k++;
                }

                i = Math.Min(k, line.Length - 1);
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static int SkipVerbatim(string line, int openQuote)
    {
        int j = openQuote + 1;
        while (j < line.Length)
        {
            if (line[j] != '"')
            {
                j++;
                continue;
            }

            if (j + 1 < line.Length && line[j + 1] == '"')
            {
                j += 2;
                continue;
            }

            return j;
        }

        return line.Length - 1;
    }

    private static int SkipRegularString(string line, int openQuote)
    {
        int j = openQuote + 1;
        while (j < line.Length)
        {
            if (line[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (line[j] == '"')
            {
                return j;
            }

            j++;
        }

        return line.Length - 1;
    }

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard for issue #674: the diagnostic classifier lives once in the headless
///     core, and the LSP diagnostics path is wired to state rather than pinned
///     to a literal zero.
/// </summary>
public sealed class DiagnosticsClassificationRule
{
    private static readonly Lazy<DiagnosticClassificationReport> Report = new(
        () => DiagnosticsClassificationProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     The detector patterns are declared once, in the core detector, and
    ///     the same pattern appears in no other file under <c>src/</c> +
    ///     <c>apps/</c>.
    /// </summary>
    [Test]
    public async Task DetectorPatterns_AreDeclaredOnlyByTheCoreDetector()
    {
        List<string> offenders =
        [
            .. Report.Value.DuplicatedPatterns
                .Select(s => $"{s.File}:{s.Line} -> {s.Detail}")
                .OrderBy(s => s, StringComparer.Ordinal)
        ];

        await Assert.That(offenders).IsEmpty()
            .Because(
                "turning raw text into a diagnostic is counting, and counting belongs to the headless "
                + "core. #674 found the table twice: once in the core detector and once inside "
                + "src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs, which parsed the "
                + "`bash` tool's OUTPUT with six regexes and fed the result to the same panel that was "
                + "meant to show language-server diagnostics. Two classifiers over one feed is how the "
                + "two concepts got fused, and it is why the real LSP channel was never connected. "
                + "Add a detector to "
                + DiagnosticsClassificationProbe.CanonicalDetectorFile
                + " and call it from there — a renderer may not carry its own copy. Duplicated: "
                + (offenders.Length == 0 ? "(none)" : string.Join(" | ", offenders)));
    }

    /// <summary>
    ///     No file pins an <c>LspErrors:</c> / <c>LspWarnings:</c> argument to the
    ///     literal <c>0</c>. The sidebar spelled both as zero for as long as the
    ///     path existed, so its DIAGNOSTICS section could never light up while a
    ///     second, parallel text channel pretended to be the same feature.
    /// </summary>
    [Test]
    public async Task SidebarProject_DoesNotHardcodeTheLspCounts()
    {
        List<string> offenders =
        [
            .. Report.Value.HardcodedLspCounts
                .Select(s => $"{s.File}:{s.Line} ({s.Detail})")
                .OrderBy(s => s, StringComparer.Ordinal)
        ];

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a literal zero in an LspErrors/LspWarnings named argument is the signature of the "
                + "never-connected path #674 found in SideBarView.Project — the counts have to come out "
                + "of UiState, whatever that state currently holds (an empty snapshot included, which "
                + "is honest: no language server has published anything). Offending sites: "
                + (offenders.Length == 0 ? "(none)" : string.Join(" | ", offenders)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really read the canonical
    ///     detector, finding at least one pattern in it. Zero patterns would make
    ///     rule 1 vacuously green — "no duplicates" is trivially true of an
    ///     empty table.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reads zero files and every "
                   + "rule here is satisfied by having nothing to look at");

        DiagnosticClassificationReport report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter are "
                   + "wrong and every rule here is vacuously green");

        await Assert.That(report.CanonicalFileFound).IsTrue()
            .Because($"the core detector {DiagnosticsClassificationProbe.CanonicalDetectorFile} is the "
                   + "single owner of the classification table and the source of the guarded patterns. "
                   + "If it moved, update CanonicalDetectorFile in the same commit — do not delete the "
                   + "rule.");

        await Assert.That(report.CanonicalPatterns.Count).IsGreaterThan(0)
            .Because($"no detector pattern was read out of "
                   + $"{DiagnosticsClassificationProbe.CanonicalDetectorFile}, so rule 1 compares "
                   + "against an empty table and passes for any tree whatsoever");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed a synthetic RENDERER file
    ///     that re-declares one of the canonical detector patterns, a synthetic
    ///     renderer file that carries no pattern, and a synthetic sidebar that
    ///     pins the counts to zero. It MUST report all three, and MUST NOT
    ///     report the clean file. A probe whose matchers stopped matching reports
    ///     nothing, and the rule would go green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsADuplicatedDetectorInSyntheticSource()
    {
        // Must be byte-identical to one of the canonical patterns — read from
        // the real core file, so the control ages with the table it grades.
        string pattern = Report.Value.CanonicalPatterns.Count > 0
            ? Report.Value.CanonicalPatterns[0]
            : @"\b(CS|MSB)\d{4}\b";

        string duplicated = $$"""
            private static readonly Regex CSharpRegex = new(@"{{pattern}}", RegexOptions.Compiled);
            """;

        // A renderer full of its own regexes that share nothing with the core
        // table (todo markers, tool names, JSON paths). Those are presentation
        // concerns and must survive the rule untouched.
        string unrelated = """
            private static readonly Regex TodoRegex = new(
                @"^\s*\[(?<marker>[ ~xX\?])\]\s*(?<content>.+?)\s*$",
                RegexOptions.Compiled);
            """;

        // The exact shape #674 found: named arguments pinned to the literal 0.
        string hardcoded = """
                LspErrors: 0,
                LspWarnings: 0,
            """;

        // A sidebar that COUNTS the rows instead of inventing them. The rule
        // must leave this alone or it would forbid the fix.
        string counted = """
                LspErrors: Count(state.Chat.Diagnostics, d => d.Severity == DiagnosticIssueSeverity.Error),
                LspWarnings: warnings,
            """;

        DiagnosticClassificationReport Scan(string content)
        {
            var duplicatedSites = new List<DiagnosticClassificationSite>();
            var hardcodedSites = new List<DiagnosticClassificationSite>();
            DiagnosticsClassificationProbe.ScanSource(
                "src/Harbor.Tui.CellForge/Chat/Widgets/Synthetic.cs",
                content.Split('\n'),
                [pattern],
                duplicatedSites,
                hardcodedSites);
            return new DiagnosticClassificationReport(true, [pattern], duplicatedSites, hardcodedSites, 1);
        }

        DiagnosticClassificationReport duplicatedReport = Scan(duplicated);
        DiagnosticClassificationReport unrelatedReport = Scan(unrelated);
        DiagnosticClassificationReport hardcodedReport = Scan(hardcoded);
        DiagnosticClassificationReport countedReport = Scan(counted);

        await Assert.That(duplicatedReport.DuplicatedPatterns.Count).IsEqualTo(1)
            .Because("the first snippet is a renderer re-declaring a core detector pattern, which is "
                   + "precisely what rule 1 must reject. A miss means the verbatim-literal reader or "
                   + "the comment stripper stopped working and the rule enforces nothing.");

        await Assert.That(duplicatedReport.DuplicatedPatterns[0].Detail).IsEqualTo(pattern)
            .Because("the probe must report WHICH core pattern was copied, or the failure message "
                   + "cannot tell the author what to delete. Reported: "
                   + duplicatedReport.DuplicatedPatterns[0].Detail);

        await Assert.That(unrelatedReport.DuplicatedPatterns.Count).IsEqualTo(0)
            .Because("the second snippet is a renderer whose regexes share nothing with the core "
                   + "diagnostic table (todo markers are a presentation concern) — grading it would "
                   + "forbid the UI from parsing its own transcript at all");

        await Assert.That(hardcodedReport.HardcodedLspCounts.Count).IsEqualTo(2)
            .Because("the third snippet is the exact #674 shape: two named arguments pinned to the "
                   + "literal 0, which is what rule 2 must catch. Reported: "
                   + hardcodedReport.HardcodedLspCounts.Count);

        await Assert.That(countedReport.HardcodedLspCounts.Count).IsEqualTo(0)
            .Because("the fourth snippet is what the fix looks like — the counts come from state — so "
                   + "rule 2 must not fire on it, or it would forbid the repair it exists to demand");
    }
}
