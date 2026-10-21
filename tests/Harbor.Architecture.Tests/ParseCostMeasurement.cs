// ParseCostMeasurement.cs — the measurement behind the question "what does
// the architecture suite's source parsing cost, and what would it cost if it
// were done differently?".
//
// WHAT THIS FILE IS
// -----------------
// The architecture suite reads the repository's source tree and grades it with
// TEXT rules. Every one of those rules needs the same thing first: the source
// with its comments blanked out. That step used to allocate in proportion to
// the SQUARE of the length of each block comment, because the blanking loop
// read `Match.Value` — a property that allocates a fresh string on every
// read — once per character of the match.
//
// This file MEASURES the cost of that step, over the repository's own tree:
//
//   1. `legacy-quadratic` — the implementation the rules ran against,
//   2. `oracle-linear`   — the same two regular expressions with the
//                          quadratic read replaced by a linear one, and
//   3. `scanner`         — the scanner that replaced the whole thing
//                          (`SourceCommentBlanker`).
//
// The measurement is per FILE, so the expensive files are named rather than
// averaged away: a cost that is one 75 KB comment is a cost a reader can act
// on, and a cost that is spread evenly is not.
//
// HOW TO REPRODUCE IT
// -------------------
//     dotnet run --project tests/Harbor.Architecture.Tests -c Release --no-build -- \
//         --treenode-filter "/*/*/ParseCostMeasurement/*"
//
// The lines it prints are `[arch-cost]` lines, which
// `tools/test-measurements.py` renders into the CI job summary. The
// measurement is not a gate: the rules it measures keep their own assertions.
//
// THE NUMBERS AS MEASURED (arm64, 2026-10-21)
// --------------------------------------------
// The legacy stripper allocates 12.82 GB for ONE pass over `tests/`, and
// 11.40 GB of that comes from ONE block comment — the 75 497-character XML
// doc comment at the top of
// `tests/Harbor.Architecture.Tests/ProcessEnvIsolationRule.cs`. The product
// tree costs 0.12 GB for the same pass, because its block comments are small.
//
// THE BUDGET
// ----------
// The legacy implementation is quadratic, so running it over the whole tree
// costs 12.82 GB of garbage. That is affordable ONCE, on a measurement run,
// and it is not affordable in every CI run of the repository. So the legacy
// pass is bounded by `LegacyBudgetBytes`: files whose projected legacy cost
// exceeds it are not run through the legacy, and their cost is reported as a
// PROJECTION (the projection is exact — the legacy's cost is a function of the
// block comments' lengths alone).

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures what the comment stripping over the repository's own source
///     tree costs, and names the files that cost the most.
/// </summary>
[NotInParallel]
public sealed class ParseCostMeasurement
{
    /// <summary>
    ///     The legacy pass is bounded: the legacy implementation is quadratic,
    ///     so a file with a 75 497-character block comment costs 11.4 GB of
    ///     garbage to strip.
    /// </summary>
    /// <remarks>
    ///     The bound is the legacy's PROJECTED cost, computed from the block
    ///     comments' lengths alone, so the bound is exact rather than
    ///     approximate. Files above the bound are reported as projections.
    ///     <para>
    ///         The bound is not decoration: with the bound off, the legacy
    ///         allocates 12.5 GB in this one test, the process's resident set
    ///         reaches 2.4 GB, and every OTHER test in the shard pays for the
    ///         heap the measurement left behind. The measurement run that
    ///         produced the headline number (12 465 MB, 3 304 ms for one pass
    ///         over the tree) had the bound off; the numbers below are the
    ///         same, with the un-affordable files reported as projections.
    ///     </para>
    /// </remarks>
    private const long LegacyBudgetBytes = 64L * 1024 * 1024;

    /// <summary>
    ///     The measurement: the same tree, three implementations, the cost of
    ///     each in bytes and in milliseconds, and the files that cost the most.
    /// </summary>
    [Test]
    public async Task Measure_The_Cost_Of_Comment_Stripping_Over_The_Live_Tree()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "every rule in this project reads the tree from RepoPaths.RepoRoot, and with no "
                + "root this measurement would report zeros that look like a clean tree.");

        var legacy = new TreeCost("legacy-quadratic");
        var oracle = new TreeCost("oracle-linear");
        var scanner = new TreeCost("scanner");

        foreach (string directory in new[] { "src", "apps", "tests" })
        {
            foreach (string file in EnumerateCsFiles(directory))
            {
                string text = File.ReadAllText(file);
                string relative = SourceScan.Relative(file);

                legacy.Add(relative, text.Length, CommentStripperOracle.ProjectedLegacyBytes(text), Measure(text, CommentStripperOracle.StripCommentsLegacy));
                oracle.Add(relative, text.Length, CommentStripperOracle.ProjectedLegacyBytes(text), Measure(text, CommentStripperOracle.StripCommentsOracleText));
                scanner.Add(relative, text.Length, CommentStripperOracle.ProjectedLegacyBytes(text), Measure(text, SourceCommentBlanker.Blank));
            }
        }

        foreach (TreeCost cost in new[] { legacy, oracle, scanner })
        {
            Console.WriteLine(
                $"[arch-cost] stripper {cost.Name} files={cost.Files} chars={cost.Chars} "
                + $"measured-mb={cost.MeasuredBytes / 1024.0 / 1024.0:F1} "
                + $"projected-mb={cost.ProjectedBytes / 1024.0 / 1024.0:F1} "
                + $"not-run={cost.NotRun} ms={ToMilliseconds(cost.ElapsedTicks):F1}");

            foreach ((string file, long bytes, long ticks) in cost.Top(5))
            {
                Console.WriteLine(
                    $"[arch-cost] stripper {cost.Name} worst file={file} "
                    + $"measured-mb={bytes / 1024.0 / 1024.0:F1} ms={ToMilliseconds(ticks):F1}");
            }
        }

        // The ratio is the number the whole exercise exists for: how much of
        // the cost the quadratic read was responsible for.
        Console.WriteLine(
            $"[arch-cost] stripper ratio legacy/scanner "
            + $"measured={(double)legacy.MeasuredBytes / Math.Max(1, scanner.MeasuredBytes):F1} "
            + $"projected={(double)legacy.ProjectedBytes / Math.Max(1, scanner.ProjectedBytes):F1}");

        Console.WriteLine(
            $"[arch-cost] stripper meter calls={StripCommentsMeter.Calls} input-chars={StripCommentsMeter.InputChars} "
            + $"big-calls={StripCommentsMeter.BigCalls} big-chars={StripCommentsMeter.BigInputChars}");

        Console.WriteLine(
            $"[arch-cost] stripper rss-mb={Environment.WorkingSet / 1024.0 / 1024.0:F1} "
            + $"gc-heap-mb={GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1}");
    }

    /// <summary>
    ///     The text-scan layer of ONE representative rule, and the cost of the
    ///     two things it is made of: the pattern style, and the parser the
    ///     alternative (Roslyn) would use instead.
    /// </summary>
    /// <remarks>
    ///     The rule is the "single home" rule for a session's token cell
    ///     (<c>TokenCellSingleHomeRules</c>). It is representative: it walks the
    ///     product tree, strips the comments, splits into lines, and asks a
    ///     shape per line.
    ///     <para>
    ///         Two numbers come out of it. The first is the rule's own cost,
    ///         through the rule's own matcher. The second is the cost of the
    ///         PATTERN STYLE: the rule's patterns are conjunctions of lookahead
    ///         assertions (<c>(?=[^\n]*X)</c>), and each lookahead is tried at
    ///         every position of the line, which makes the match quadratic in
    ///         the line's length. The same three checks, asked independently,
    ///         are linear — and the Go prototype of this rule uses the
    ///         independent form, because Go's regexp cannot express a lookahead
    ///         at all.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Measure_The_Text_Scan_Of_One_Rule_And_The_Cost_Of_Roslyn()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the measurement reads the repository's own source; with no root it measures nothing.");

        // (1) The rule's own scan, through the rule's own matcher.
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        IReadOnlyList<string> hits = TokenCellSingleHomeRules.LiveHits();
        long finished = Stopwatch.GetTimestamp();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Console.WriteLine(
            $"[arch-cost] rule token-cell live hits={hits.Count} ms={ToMilliseconds(finished - started):F1} "
            + $"alloc-mb={(after - before) / 1024.0 / 1024.0:F1}");

        foreach (string hit in hits)
        {
            Console.WriteLine($"[arch-count] rule token-cell live hit {hit}");
        }

        // (2) The pattern style: the lookahead conjunction against the same
        //     three checks asked independently.
        Regex lookahead = new(
            @"(?=[^\n]*/\s*1_?000(?:_000)?(?:\.0)?\s*[,)])(?=[^\n]*\.ToString\(\s*\x22\s*(?:[Ff][0-9]|0\.#))(?=[^\n]*[KkMm]\b)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        Regex scaled = new(@"/\s*1_?000(?:_000)?(?:\.0)?\s*[,)]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        Regex toString = new(@"\.ToString\(\s*\x22\s*(?:[Ff][0-9]|0\.#)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        Regex suffix = new(@"[KkMm]\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        long lookaheadTicks = 0;
        long splitTicks = 0;
        long lines = 0;
        long lookaheadMatches = 0;
        long splitMatches = 0;

        foreach (string file in EnumerateCsFiles("src").Concat(EnumerateCsFiles("apps")))
        {
            string text = File.ReadAllText(file);
            foreach (string line in SourceScan.StripComments(text).Split('\n'))
            {
                lines++;

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool a = lookahead.IsMatch(line);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool b = scaled.IsMatch(line) && toString.IsMatch(line) && suffix.IsMatch(line);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

                lookaheadTicks += t1 - t0;
                splitTicks += t2 - t1;

                if (a)
                {
                    lookaheadMatches++;
                }

                if (b)
                {
                    splitMatches++;
                }
            }
        }

        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine(
            $"[arch-cost] rule token-cell pattern lines={lines} lookahead-ms={lookaheadTicks * toMs:F1} "
            + $"split-ms={splitTicks * toMs:F1} lookahead-matches={lookaheadMatches} split-matches={splitMatches}");

        // (3) The parser the ALTERNATIVE would use: Roslyn over the same tree.
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        long parseBytes = 0;
        long parseTicks = 0;
        long parseFiles = 0;
        long parseChars = 0;

        foreach (string file in EnumerateCsFiles("src").Concat(EnumerateCsFiles("apps")))
        {
            string text = File.ReadAllText(file);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, options, path: file);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            parseBytes += tree.GetText().Length;
            parseTicks += t1 - t0;
            parseFiles++;
            parseChars += text.Length;
        }

        Console.WriteLine(
            $"[arch-cost] roslyn parse files={parseFiles} chars={parseChars} "
            + $"ms={parseTicks * toMs:F1}");

        // The controls: the corpus a second implementation must reproduce.
        foreach (ScanControl control in TokenCellSingleHomeRules.ControlCorpus())
        {
            string stripped = control.File.EndsWith(".axaml", StringComparison.Ordinal)
                ? SourceScan.StripMarkupComments(control.Source)
                : SourceScan.StripComments(control.Source);

            var reported = new List<string>();
            foreach (string line in stripped.Split('\n'))
            {
                if (control.File.EndsWith(".axaml", StringComparison.Ordinal))
                {
                    if (Regex.IsMatch(line, @"(?=[^\n]*StringFormat=)(?=[^\n]*\{0:N\d)(?=[^\n]*\{Binding\s+[A-Za-z0-9_]*[Tt]oken)", RegexOptions.CultureInvariant))
                    {
                        reported.Add("TOKEN-CELL-HAND-SPELLED-IN-XAML");
                    }
                }
                else if (Regex.IsMatch(line, @"(?=[^\n]*/\s*1_?000(?:_000)?(?:\.0)?\s*[,)])(?=[^\n]*\.ToString\(\s*\x22\s*(?:[Ff][0-9]|0\.#))(?=[^\n]*[KkMm]\b)", RegexOptions.CultureInvariant))
                {
                    reported.Add("TOKEN-CELL-SCALED-AND-SUFFIXED-BY-HAND");
                }
                else if (Regex.IsMatch(line, @"(?=[^\n]*\b[A-Za-z0-9_]*[Tt]oken[A-Za-z0-9_]*\b)(?=[^\n]*[\u2191\u2193])", RegexOptions.CultureInvariant))
                {
                    reported.Add("TOKEN-CELL-ASSEMBLED-BY-HAND");
                }
                else if (Regex.IsMatch(line, @"\bFormat[A-Za-z0-9_]*[Tt]okens?[A-Za-z0-9_]*\s*\([^)]*Span<char>", RegexOptions.CultureInvariant))
                {
                    reported.Add("TOKEN-CELL-HAND-ROLLED-INTO-A-SPAN");
                }
            }

            Console.WriteLine(
                $"[arch-count] rule token-cell control file={control.File} "
                + $"reported={(reported.Count == 0 ? "NONE" : string.Join("|", reported))} "
                + $"expected={(control.ExpectSubId ?? "NONE")}");
        }
    }

    /// <summary>
    ///     One measured call: how many bytes it allocated and how long it took.
    /// </summary>
    /// <remarks>
    ///     The time is counted in TICKS, not in milliseconds: the scanner's
    ///     whole pass over the tree takes less than a millisecond per file,
    ///     and an integer millisecond per file would report the sum as zero.
    /// </remarks>
    /// <param name="text">The text to measure over.</param>
    /// <param name="implementation">The implementation to measure.</param>
    private static (long AllocatedBytes, long Ticks) Measure(
        string text,
        Func<string, string> implementation)
    {
        // Warm the code paths on a TINY input: the first call of a measurement
        // is the cost of the runtime's lazy paths, and warming it on the real
        // input would pay the quadratic cost twice.
        _ = implementation("/* warm */\n// warm\n");

        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        _ = implementation(text);
        long finished = Stopwatch.GetTimestamp();
        long after = GC.GetAllocatedBytesForCurrentThread();

        return (after - before, finished - started);
    }

    /// <summary>Ticks to milliseconds, with the fraction kept.</summary>
    /// <param name="ticks">The ticks to convert.</param>
    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    ///     Every <c>*.cs</c> file under a directory, build output excluded.
    /// </summary>
    /// <param name="directory">The directory to walk.</param>
    private static IReadOnlyList<string> EnumerateCsFiles(string directory)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string path = Path.Combine(root, directory);
        if (!Directory.Exists(path))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .Where(p =>
                    !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !p.Contains($"{Path.DirectorySeparatorChar}contrib{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !p.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>One implementation's cost over the tree, and the files that cost the most.</summary>
    private sealed class TreeCost(string name)
    {
        private readonly List<(string File, long Bytes, long Ticks)> _files = [];

        internal string Name { get; } = name;

        internal int Files => _files.Count;

        internal long Chars { get; private set; }

        /// <summary>The bytes actually measured, over the files that were run.</summary>
        internal long MeasuredBytes { get; private set; }

        /// <summary>The bytes every file would cost, measured or projected.</summary>
        internal long ProjectedBytes { get; private set; }

        /// <summary>How many files were not run because they are over the budget.</summary>
        internal int NotRun { get; private set; }

        internal long ElapsedTicks { get; private set; }

        internal void Add(
            string file,
            int chars,
            long projected,
            (long AllocatedBytes, long Ticks) cost)
        {
            Chars += chars;
            ProjectedBytes += projected;
            ElapsedTicks += cost.Ticks;

            if (projected > LegacyBudgetBytes)
            {
                NotRun++;
                _files.Add((file, projected, 0));
                return;
            }

            MeasuredBytes += cost.AllocatedBytes;
            _files.Add((file, cost.AllocatedBytes, cost.Ticks));
        }

        internal IReadOnlyList<(string File, long Bytes, long Ticks)> Top(int count) =>
            [.. _files.OrderByDescending(f => f.Bytes).Take(count)];
    }
}
