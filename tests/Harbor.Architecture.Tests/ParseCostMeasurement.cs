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
    ///         This MEASUREMENT branch runs the legacy over the whole tree,
    ///         so the bound is effectively off here: the number the report
    ///         quotes — 12.9 GB for one pass over <c>tests/</c> — is measured,
    ///         not projected. The bound comes back when the measurement becomes
    ///         a permanent test.
    ///     </para>
    /// </remarks>
    private const long LegacyBudgetBytes = long.MaxValue;

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
                + $"not-run={cost.NotRun} ms={cost.ElapsedMilliseconds}");

            foreach ((string file, long bytes, long ms) in cost.Top(5))
            {
                Console.WriteLine(
                    $"[arch-cost] stripper {cost.Name} worst file={file} "
                    + $"measured-mb={bytes / 1024.0 / 1024.0:F1} ms={ms}");
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
    ///     One measured call: how many bytes it allocated and how long it took.
    /// </summary>
    /// <param name="text">The text to measure over.</param>
    /// <param name="implementation">The implementation to measure.</param>
    private static (long AllocatedBytes, long ElapsedMilliseconds) Measure(
        string text,
        Func<string, string> implementation)
    {
        // Warm the code paths on a TINY input: the first call of a measurement
        // is the cost of the runtime's lazy paths, and warming it on the real
        // input would pay the quadratic cost twice.
        _ = implementation("/* warm */\n// warm\n");

        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        _ = implementation(text);
        watch.Stop();
        long after = GC.GetAllocatedBytesForCurrentThread();

        return (after - before, watch.ElapsedMilliseconds);
    }

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
                    && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>One implementation's cost over the tree, and the files that cost the most.</summary>
    private sealed class TreeCost(string name)
    {
        private readonly List<(string File, long Bytes, long Ms)> _files = [];

        internal string Name { get; } = name;

        internal int Files => _files.Count;

        internal long Chars { get; private set; }

        /// <summary>The bytes actually measured, over the files that were run.</summary>
        internal long MeasuredBytes { get; private set; }

        /// <summary>The bytes every file would cost, measured or projected.</summary>
        internal long ProjectedBytes { get; private set; }

        /// <summary>How many files were not run because they are over the budget.</summary>
        internal int NotRun { get; private set; }

        internal long ElapsedMilliseconds { get; private set; }

        internal void Add(
            string file,
            int chars,
            long projected,
            (long AllocatedBytes, long ElapsedMilliseconds) cost)
        {
            Chars += chars;
            ProjectedBytes += projected;
            ElapsedMilliseconds += cost.ElapsedMilliseconds;

            if (projected > LegacyBudgetBytes)
            {
                NotRun++;
                _files.Add((file, projected, 0));
                return;
            }

            MeasuredBytes += cost.AllocatedBytes;
            _files.Add((file, cost.AllocatedBytes, cost.ElapsedMilliseconds));
        }

        internal IReadOnlyList<(string File, long Bytes, long Ms)> Top(int count) =>
            [.. _files.OrderByDescending(f => f.Bytes).Take(count)];
    }
}
