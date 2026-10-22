// AllocationFixMeasurement.cs — the "before | after" numbers of the allocation
// fixes, measured in ONE run.
//
// WHY A MEASUREMENT AND NOT A TABLE
// --------------------------------
// The fixes in this wave are all of one shape: a cheap necessary condition, or
// a rented buffer, in front of an expensive operation. Each of them keeps the
// PREVIOUS implementation next to the new one — `BlankUnpooled`,
// `FindRacesInStrippedCodeUnskipped`, the reference twins in the parity
// tests — so that ONE run can measure both and report the difference, rather
// than a number from one run and a number from another.
//
// The pairs are measured over the LIVE TREE, which is the input the rules
// actually see. A microbenchmark over a synthetic string would answer a
// different question: the tree holds the files that make the rules slow.
//
// WHAT IS ASSERTED, AND WHAT IS ONLY REPORTED
// --------------------------------------------
// The PARITY is asserted (in the parity tests, over the same tree). The
// ALLOCATION is reported: a byte count is machine-independent, but the
// threshold it is compared against is a judgement, and this file states the
// numbers and leaves the judgement to the reader.
//
// THE LINES
// ---------
//     [arch-cost] fix=<name> before-bytes=<n> after-bytes=<n> ratio=<f> files=<n> ms=<f>
//
// The lines are picked up by `tools/gates/test-measurements`, which renders
// every `Console.WriteLine` a test emits into the CI job summary.

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures what the allocation fixes of this wave save, over the live
///     tree, by running the old implementation and the new one side by side.
/// </summary>
[NotInParallel]
public sealed class AllocationFixMeasurement
{
    /// <summary>
    ///     The comment stripper: the rented buffer against the array of its
    ///     own.
    /// </summary>
    /// <remarks>
    ///     Every rule that reads the tree strips it, and the strip happened to
    ///     cost a buffer AND a string per call — four bytes per input
    ///     character, of which two were the buffer. The buffer is the part
    ///     that is pure waste: it is dead the moment the string is built.
    /// </remarks>
    [Test]
    public async Task Measure_The_Comment_Stripper_S_Buffer()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because(
                "the measurement reads the repository's own source; with no root it would measure "
                + "nothing and report a cost of zero that looks like a free stripper.");

        var before = new Counter();
        var after = new Counter();
        int files = 0;
        long chars = 0;

        foreach (string file in EnumerateTreeCsFiles())
        {
            string text = File.ReadAllText(file);
            files++;
            chars += text.Length;

            before.Add(() => SourceCommentBlanker.BlankUnpooled(text));
            after.Add(() => SourceCommentBlanker.Blank(text));
        }

        Report("comment-stripper-buffer", before, after, files, chars);

        // The measurement must have measured something on BOTH sides: a zero
        // on one side is indistinguishable from a missing measurement, and a
        // ratio against it is a number about nothing.
        await Assert.That(before.Bytes).IsGreaterThan(0L)
            .Because(
                "the unpooled stripper must have allocated something: a zero there means the "
                + "measurement measured nothing and the ratio is a number about nothing.");
        await Assert.That(after.Bytes).IsGreaterThan(0L)
            .Because(
                "the pooled stripper must have allocated something: a zero there means the measurement "
                + "measured nothing and the ratio is a number about nothing.");
    }

    /// <summary>
    ///     The two implementations' allocation, and the time they took.
    /// </summary>
    /// <param name="name">The fix's name, as the report carries it.</param>
    /// <param name="before">The previous implementation's counter.</param>
    /// <param name="after">The new implementation's counter.</param>
    /// <param name="files">How many files the measurement ran over.</param>
    /// <param name="chars">How many characters those files hold.</param>
    private static void Report(string name, Counter before, Counter after, int files, long chars)
    {
        double ratio = after.Bytes == 0 ? 0 : (double)before.Bytes / after.Bytes;

        Console.WriteLine(
            $"[arch-cost] fix={name} before-bytes={before.Bytes} after-bytes={after.Bytes} "
            + $"ratio={ratio:F2} files={files} chars={chars} "
            + $"before-ms={before.Milliseconds:F1} after-ms={after.Milliseconds:F1}");
    }

    /// <summary>One implementation's cost over the tree.</summary>
    private sealed class Counter
    {
        private long _bytes;
        private long _ticks;

        /// <summary>How many bytes the implementation allocated.</summary>
        internal long Bytes => _bytes;

        /// <summary>How long the implementation took, in milliseconds.</summary>
        internal double Milliseconds => _ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Runs one call and counts what it cost.</summary>
        /// <param name="body">The call to run.</param>
        internal void Add(Func<string> body)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _ = body();
            long finished = Stopwatch.GetTimestamp();
            long after = GC.GetAllocatedBytesForCurrentThread();

            _bytes += after - before;
            _ticks += finished - started;
        }
    }

    /// <summary>
    ///     Every <c>*.cs</c> file of the three trees the rules read, build
    ///     output excluded.
    /// </summary>
    private static IEnumerable<string> EnumerateTreeCsFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        foreach (string tree in new[] { "src", "apps", "tests" })
        {
            string directory = Path.Combine(root, tree);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }
}
