// TestTreeAllocationMeasurement.cs — the ALLOCATION census of the test tree,
// measured per test project.
//
// WHAT THIS FILE IS
// -----------------
// The wall census (tools/gates/test-census) knows how long every test took.
// This file answers the other half of the same question: how many BYTES every
// test project allocates, and which of the four stages the tree-reading costs
// are paid in.
//
// WHY A STAGE BREAKDOWN, AND NOT JUST A TOTAL
// --------------------------------------------
// A total says "this project allocates a lot". It does not say WHERE. The
// tree-reading costs of this repository are paid in four stages, and each one
// has a different fix:
//
//   read        File.ReadAllText over the tree — the bytes of the sources.
//   strip       SourceScan.StripComments — the comment-stripped view.
//   split-nl    text.Split('\n') — the line view a rule matches against.
//   split-rl    SourceTreeLines.SplitLikeReadLine — the ReadAllLines-shaped
//               line view, which is the one a rule that indexes line numbers
//               needs.
//
// and there is a fifth shape that is NOT a stage but a PATTERN: the rules that
// walk a tree themselves do
//
//     File.ReadAllLines(file) -> string.Join('\n', lines)
//                             -> SourceScan.StripComments(...) -> Split('\n')
//
// which pays read + strip + split AGAIN, per rule, over a tree the shared
// snapshot has already read. That shape is measured here as `idiom`, because
// it is the one the fixes target: a rule that goes through the snapshot pays
// nothing for the stage it does not use.
//
// THE TWO COUNTERS
// ----------------
//   * `GC.GetAllocatedBytesForCurrentThread()` — exact for a window that
//     stayed on one thread. Every window here is synchronous, so it is.
//   * `GC.GetTotalAllocatedBytes(precise: true)` — the process-wide counter.
//     It is the honest answer for a window that could have hopped threads, and
//     under a parallel runner it is an UPPER BOUND: it bills whatever the
//     tests running beside this one allocated. The class is [NotInParallel],
//     so the process number is this measurement's own.
//
// THE LINES
// ---------
//     [alloc-cost] project=<name> stage=<stage> files=<n> chars=<n> bytes=<n> ms=<f>
//     [alloc-cost] project=<name> total bytes=<n> files=<n> chars=<n>
//
// The lines are picked up by `tools/gates/test-measurements`, which renders
// every `Console.WriteLine` a test emits into the CI job summary. Nothing here
// is a gate: this file measures and reports, and the rules it measures keep
// their own assertions.
//
// HOW TO REPRODUCE IT
// -------------------
//     dotnet run --project tests/Harbor.Architecture.Tests -c Release --no-build -- \
//         --treenode-filter "/*/*/TestTreeAllocationMeasurement/*"

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures the allocation cost of reading the test tree, per test project
///     and per stage, and ranks the projects by what they cost.
/// </summary>
[NotInParallel]
public sealed class TestTreeAllocationMeasurement
{
    /// <summary>
    ///     The measurement: for every test project under <c>tests/</c>, what each
    ///     stage of the tree-reading costs, and what the whole thing costs.
    /// </summary>
    [Test]
    public async Task Measure_The_Allocation_Cost_Of_Every_Test_Project()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "every project in this measurement is found under the repository root, and with no "
                + "root this measurement would report zeros that look like a cheap tree.");

        var projects = new List<ProjectCost>();

        foreach ((string name, string directory) in EnumerateTestProjects())
        {
            ProjectCost cost = MeasureProject(name, directory);
            projects.Add(cost);

            foreach (StageCost stage in cost.Stages)
            {
                Console.WriteLine(
                    $"[alloc-cost] project={name} stage={stage.Name} files={stage.Files} "
                    + $"chars={stage.Chars} bytes={stage.Bytes} ms={stage.Milliseconds:F1}");
            }

            Console.WriteLine(
                $"[alloc-cost] project={name} total files={cost.Files} chars={cost.Chars} "
                + $"bytes={cost.Bytes} ms={cost.Milliseconds:F1}");
        }

        foreach (ProjectCost cost in projects.OrderByDescending(p => p.Bytes).Take(15))
        {
            Console.WriteLine(
                $"[alloc-cost] ranking project={cost.Name} bytes={cost.Bytes} "
                + $"files={cost.Files} chars={cost.Chars}");
        }

        Console.WriteLine(
            $"[alloc-cost] ranking total projects={projects.Count} "
            + $"bytes={projects.Sum(p => p.Bytes)} files={projects.Sum(p => p.Files)}");

        // The control: the measurement must have measured SOMETHING. A census
        // that reports zeros for every project is indistinguishable from a tree
        // that is not there, and this file would be reporting the second.
        await Assert.That(projects.Count).IsGreaterThan(10)
            .Because(
                "the test tree holds more than ten projects; fewer means the walk stopped early and "
                + "the census is describing a tree that is not there.");
        await Assert.That(projects.Sum(p => p.Bytes)).IsGreaterThan(0L)
            .Because(
                "the measurement must have measured real bytes: a zero total means every stage was "
                + "skipped, and a census of zeros is a census of nothing.");
    }

    /// <summary>
    ///     One stage's cost over one project's sources.
    /// </summary>
    /// <param name="Name">The stage's name.</param>
    /// <param name="Files">How many files the stage ran over.</param>
    /// <param name="Chars">How many characters the stage handled.</param>
    /// <param name="Bytes">How many bytes the stage allocated.</param>
    /// <param name="Milliseconds">How long the stage took.</param>
    private readonly record struct StageCost(
        string Name,
        int Files,
        long Chars,
        long Bytes,
        double Milliseconds);

    /// <summary>One project's cost, over all of its stages.</summary>
    /// <param name="Name">The project's directory name.</param>
    /// <param name="Stages">The per-stage costs.</param>
    /// <param name="Files">How many files the project has.</param>
    /// <param name="Chars">How many characters the project's sources hold.</param>
    /// <param name="Bytes">How many bytes the project's stages cost.</param>
    /// <param name="Milliseconds">How long the project's stages took.</param>
    private sealed record ProjectCost(
        string Name,
        IReadOnlyList<StageCost> Stages,
        int Files,
        long Chars,
        long Bytes,
        double Milliseconds);

    /// <summary>
    ///     Measures one project's stages.
    /// </summary>
    /// <param name="name">The project's directory name.</param>
    /// <param name="directory">The project's directory.</param>
    private static ProjectCost MeasureProject(string name, string directory)
    {
        IReadOnlyList<string> files = EnumerateCsFiles(directory);
        var stages = new List<StageCost>();

        // Stage 1 — read. The texts are KEPT, because every later stage is
        // measured over the same inputs, and re-reading them would make the
        // later stages pay for the read too.
        var texts = new List<string>(files.Count);
        long readBytes = 0;
        long readTicks = 0;
        foreach (string file in files)
        {
            (string text, long bytes, long ticks) = Measure(() => File.ReadAllText(file));
            readBytes += bytes;
            readTicks += ticks;
            texts.Add(text);
        }

        long chars = texts.Sum(t => (long)t.Length);
        stages.Add(new StageCost("read", files.Count, chars, readBytes, ToMilliseconds(readTicks)));

        // Stage 2 — strip. The comment-stripped view every text rule reads the
        // tree through.
        var stripped = new List<string>(texts.Count);
        long stripBytes = 0;
        long stripTicks = 0;
        foreach (string text in texts)
        {
            (string result, long bytes, long ticks) = Measure(() => SourceScan.StripComments(text));
            stripBytes += bytes;
            stripTicks += ticks;
            stripped.Add(result);
        }

        stages.Add(new StageCost(
            "strip", files.Count, chars, stripBytes, ToMilliseconds(stripTicks)));

        // Stage 3 — split on '\n'. The line view a rule matches against.
        long splitBytes = 0;
        long splitTicks = 0;
        foreach (string text in stripped)
        {
            (string[] _, long bytes, long ticks) = Measure(() => text.Split('\n'));
            splitBytes += bytes;
            splitTicks += ticks;
        }

        stages.Add(new StageCost(
            "split-nl", files.Count, chars, splitBytes, ToMilliseconds(splitTicks)));

        // Stage 4 — split the way File.ReadAllLines splits. The line view a
        // rule that indexes line numbers needs.
        long readLineBytes = 0;
        long readLineTicks = 0;
        foreach (string text in stripped)
        {
            (string[] _, long bytes, long ticks) = Measure(() => SourceTreeLines.SplitLikeReadLine(text));
            readLineBytes += bytes;
            readLineTicks += ticks;
        }

        stages.Add(new StageCost(
            "split-rl", files.Count, chars, readLineBytes, ToMilliseconds(readLineTicks)));

        // Stage 5 — the IDIOM: what a rule that walks a tree ITSELF pays. It
        // reads the files again, joins the lines back together with '\n',
        // strips the comments of the JOINED text and splits it again.
        long idiomBytes = 0;
        long idiomTicks = 0;
        foreach (string file in files)
        {
            (string[] _, long bytes, long ticks) = Measure(() =>
            {
                string[] lines = File.ReadAllLines(file);
                return SourceScan.StripComments(string.Join('\n', lines)).Split('\n');
            });
            idiomBytes += bytes;
            idiomTicks += ticks;
        }

        stages.Add(new StageCost(
            "idiom", files.Count, chars, idiomBytes, ToMilliseconds(idiomTicks)));

        return new ProjectCost(
            name,
            stages,
            files.Count,
            chars,
            stages.Sum(s => s.Bytes),
            stages.Sum(s => s.Milliseconds));
    }

    /// <summary>
    ///     Measures one synchronous call: the bytes it allocated on this thread
    ///     and the ticks it took.
    /// </summary>
    /// <param name="body">The call to measure.</param>
    /// <typeparam name="T">What the call returns.</typeparam>
    private static (T Value, long Bytes, long Ticks) Measure<T>(Func<T> body)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        T value = body();
        long finished = Stopwatch.GetTimestamp();
        long after = GC.GetAllocatedBytesForCurrentThread();

        return (value, after - before, finished - started);
    }

    /// <summary>Ticks to milliseconds.</summary>
    /// <param name="ticks">The ticks to convert.</param>
    private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    ///     Every test project under <c>tests/</c>, with its directory.
    /// </summary>
    private static IEnumerable<(string Name, string Directory)> EnumerateTestProjects()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        string tests = Path.Combine(root, "tests");
        if (!Directory.Exists(tests))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(tests))
        {
            string name = Path.GetFileName(directory);
            if (!Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            {
                continue;
            }

            yield return (name, directory);
        }
    }

    /// <summary>
    ///     Every <c>*.cs</c> file of one project, build output excluded.
    /// </summary>
    /// <param name="directory">The project's directory.</param>
    private static IReadOnlyList<string> EnumerateCsFiles(string directory) =>
    [
        .. Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
    ];
}
