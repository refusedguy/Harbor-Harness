// SourceParseCostMeasurement.cs — what the parsing substrate costs, and what
// the forms it replaced cost.
//
// WHAT THIS FILE IS
// -----------------
// A MEASUREMENT, and it prints two kinds of line:
//
//   [arch-cost]  the cost. These lines are expected to move, and moving them
//                DOWN is the point of the rewrite.
//   [arch-count] the shape of the corpus. These lines say how much was
//                measured, so a number cannot be read without knowing what it
//                was measured over.
//
// WHY IT IS ITS OWN CLASS, AND WHY IT IS [NotInParallel]
// ------------------------------------------------------
// The bare `[NotInParallel]` is load-bearing for the same reason it is
// load-bearing in `SourceTreeScanMeasurement`: a measurement that reads the
// wall clock while a neighbouring test burns cores is measuring the
// neighbour. The bare form takes the `NotInParallelLock`, so this class runs
// COMPLETELY ALONE — which also means the per-test durations of every other
// rule in this assembly are measured without this measurement distorting them.
//
// WHAT IS MEASURED
// ----------------
//   * the comment stripper: the span-based pass, the regex pair it replaced,
//     and the QUADRATIC form that came before the regex pair — the last one
//     is measured ARITHMETICALLY, because running it over the tree would
//     allocate 12.8 GB of strings and take the runner down with it;
//   * the literal blanker: the in-place lexer against the `StringBuilder`
//     lexer;
//   * the identifier counter: the span lookup against the `Regex.Matches`
//     walk;
//   * the Roslyn stripper, over a SAMPLE, because it parses a file to find
//     what a single pass over its characters finds.
//
// The allocations are the thread's own, read with
// `GC.GetAllocatedBytesForCurrentThread()`: the work is single-threaded
// here, so the number is the work's.

using System.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Measures what the parsing substrate costs over the graded corpus, and
///     what the forms it replaced cost.
/// </summary>
[NotInParallel]
public sealed class SourceParseCostMeasurement
{
    /// <summary>How many files the sampled variants are measured over.</summary>
    /// <remarks>
    ///     The sample is the LARGEST files — the worst case for every one of
    ///     these variants — and the per-byte cost it reports is what the
    ///     projection for the whole tree rests on. The comment stripper is
    ///     measured over the WHOLE tree instead, because its numbers are the
    ///     headline: the quadratic form's allocation and the form that
    ///     replaced it, over the same bytes.
    /// </remarks>
    private const int SampleSize = 300;

    /// <summary>The measurement.</summary>
    [Test]
    public void The_Parsing_Cost_Is_Measured()
    {
        IReadOnlyList<string> files = SourceParseCorpus.Files();
        IReadOnlyList<string> texts = SourceParseCorpus.Texts();

        long treeBytes = 0;
        long blockCommentChars = 0;
        long quadraticChars = 0;

        foreach (string text in texts)
        {
            treeBytes += text.Length;
            blockCommentChars += SourceParse.BlockCommentChars(text);
            quadraticChars += SourceParse.QuadraticAllocationChars(text);
        }

        Console.WriteLine(
            $"[arch-count] parse-corpus files={files.Count} bytes={treeBytes} "
            + $"block-comment-chars={blockCommentChars} quadratic-chars={quadraticChars}");

        // ── the comment stripper ──────────────────────────────────────────
        (long legacyMs, long legacyBytes) = Measure(
            texts,
            static text => SourceParse.StripCommentsLegacy(text).Length);

        (long spanMs, long spanBytes) = Measure(
            texts,
            static text => SourceParse.StripComments(text).Length);

        Console.WriteLine($"[arch-cost] parse strip-legacy ms={legacyMs} bytes={legacyBytes}");
        Console.WriteLine($"[arch-cost] parse strip-span ms={spanMs} bytes={spanBytes}");

        // The quadratic form, measured arithmetically: the evaluator
        // materialised the matched substring once per character of every block
        // comment, so the allocation is the sum of the squares, and a char is
        // two bytes.
        Console.WriteLine($"[arch-cost] parse strip-quadratic ms=- bytes={quadraticChars * 2}");

        // ── the literal blanker ───────────────────────────────────────────
        // The blanker and the counter are measured over a SAMPLE: the legacy
        // counter materialises a `Match` and a `string` for every identifier
        // of every file, and running that over the whole corpus twice — once
        // to warm up, once to measure — is the most expensive thing this
        // measurement could do, in a shard that is measured in seconds. The
        // sample is the LARGEST files, so the per-byte cost is the worst
        // case, and the projection for the whole tree rests on it.
        var sampleOrder = Enumerable.Range(0, texts.Count)
            .OrderByDescending(i => texts[i].Length)
            .Take(SampleSize)
            .OrderBy(i => i)
            .ToList();

        var sample = new List<string>(sampleOrder.Count);
        long sampleBytes = 0;
        foreach (int i in sampleOrder)
        {
            sample.Add(texts[i]);
            sampleBytes += texts[i].Length;
        }

        (long blankLegacyMs, long blankLegacyBytes) = Measure(
            sample,
            static text => SourceParse.BlankLiteralsAndCommentsLegacy(text).Length);

        (long blankSpanMs, long blankSpanBytes) = Measure(
            sample,
            static text => SourceParse.BlankLiteralsAndComments(text).Length);

        Console.WriteLine(
            $"[arch-cost] parse blank sample={sample.Count} sample-bytes={sampleBytes} "
            + $"legacy-ms={blankLegacyMs} legacy-bytes={blankLegacyBytes} "
            + $"span-ms={blankSpanMs} span-bytes={blankSpanBytes}");

        // ── the identifier counter ───────────────────────────────────────
        (long countLegacyMs, long countLegacyBytes) = Measure(
            sample,
            static text => SourceParse.CountIdentifiersLegacy(text).Count);

        (long countSpanMs, long countSpanBytes) = Measure(
            sample,
            static text =>
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                SourceParse.CountIdentifiers(text, counts);
                return counts.Count;
            });

        Console.WriteLine(
            $"[arch-cost] parse count sample={sample.Count} sample-bytes={sampleBytes} "
            + $"legacy-ms={countLegacyMs} legacy-bytes={countLegacyBytes} "
            + $"span-ms={countSpanMs} span-bytes={countSpanBytes}");

        // ── the Roslyn variant, over the same sample ──────────────────────
        (long roslynMs, long roslynBytes) = Measure(
            sample,
            static text => SourceParse.StripCommentsRoslyn(text).Length);

        Console.WriteLine(
            $"[arch-cost] parse strip-roslyn sample={sample.Count} sample-bytes={sampleBytes} "
            + $"ms={roslynMs} bytes={roslynBytes}");

        // The per-byte cost is what the projection for the whole tree rests on,
        // and it is a PROJECTION: the whole tree was not measured with this
        // variant, because it would have cost minutes.
        if (sampleBytes > 0 && treeBytes > 0)
        {
            long projectedMs = (long)((double)roslynMs / sampleBytes * treeBytes);
            Console.WriteLine(
                $"[arch-cost] parse strip-roslyn projected-tree-ms={projectedMs} "
                + $"projection=per-byte-cost-x-tree-bytes");
        }
    }

    /// <summary>
    ///     Runs the work over the corpus and reports how long it took and how
    ///     much it allocated.
    /// </summary>
    /// <param name="texts">The corpus.</param>
    /// <param name="work">The work to run over one file.</param>
    /// <returns>The elapsed milliseconds and the allocated bytes.</returns>
    /// <remarks>
    ///     The warm-up is ONE file, not the whole corpus: the first call into a
    ///     regex pays for the interpreter's first use, and a warm-up that cost
    ///     as much as the measurement would double the cost of the suite for a
    ///     number that is about the steady state.
    /// </remarks>
    private static (long Ms, long Bytes) Measure(IReadOnlyList<string> texts, Func<string, int> work)
    {
        if (texts.Count == 0)
        {
            return (0, 0);
        }

        long warm = work(texts[0]);
        if (warm < 0)
        {
            throw new InvalidOperationException("the warm-up must not be optimised away.");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();

        long total = 0;
        foreach (string text in texts)
        {
            total += work(text);
        }

        watch.Stop();
        long after = GC.GetAllocatedBytesForCurrentThread();

        if (total < 0)
        {
            throw new InvalidOperationException("the work must not be optimised away.");
        }

        return (watch.ElapsedMilliseconds, after - before);
    }
}
