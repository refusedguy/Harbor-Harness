// AnsiTerminalBufferParityTests.cs — the pooled grid searches are PROVED
// against the old ones, and MEASURED against them.
//
// WHAT THIS FILE IS
// -----------------
// `AnsiTerminalBuffer` is the 2D terminal emulator every PTY scenario asserts
// on, and `TuiDriver.WaitForTextAsync` searches it on every poll. The three
// searches it offers used to MATERIALISE what they searched:
//
//   * `ContainsText`          — a `StringBuilder` and a string per row;
//   * `GetVisibleText`        — a `StringBuilder` and a string per row;
//   * `ContainsTextIgnoringWhitespace` — the visible text, built as a string
//     and then built AGAIN without its whitespace.
//
// They now read the grid in place and write into a pooled buffer. The grid is
// what a scenario's verdict is read off, so "cheaper" is the only thing these
// rewrites are allowed to be: a search that answers differently makes a
// scenario pass that used to fail.
//
// So the old implementations stay, in `AnsiTerminalBufferOracle`, and this
// file:
//
//   1. requires the new searches to answer EXACTLY as the old ones do, over
//      generated grids — the oracle reads the same grid, through the same
//      seam; and
//   2. MEASURES what each side allocated, and prints the two numbers, so
//      "cheaper" is a figure and not a claim.
//
// THE MEASUREMENT
// ---------------
// `GC.GetAllocatedBytesForCurrentThread()` counts the bytes the CALLING THREAD
// allocated. The bodies measured here are synchronous and never await, so the
// per-thread counter is the right one — a body that could suspend would need
// the process-wide counter, and mixing the two would produce a number that
// means nothing.
//
// Each measurement runs the body many times and divides, so the per-call
// figure is not dominated by the first call's JIT.

using Harbor.E2E.Framework;

namespace Harbor.E2E.Framework.Tests;

/// <summary>
///     The parity proof and the allocation measurement for the terminal-grid
///     searches.
/// </summary>
public class AnsiTerminalBufferParityTests
{
    /// <summary>How many times a measured body runs, so the figure is a per-call one.</summary>
    private const int Iterations = 200;

    /// <summary>
    ///     The grids the parity proof runs over: empty, half-written, full,
    ///     scrolled, and one with control characters in it.
    /// </summary>
    private static IEnumerable<AnsiTerminalBuffer> Grids()
    {
        var empty = new AnsiTerminalBuffer(40, 6);

        var half = new AnsiTerminalBuffer(40, 6);
        half.Write("hello world");
        half.Write("\nsecond row\twith a tab");

        var full = new AnsiTerminalBuffer(40, 6);
        for (int i = 0; i < 6; i++)
        {
            full.Write($"row {i} of the grid\n");
        }

        var scrolled = new AnsiTerminalBuffer(20, 4);
        for (int i = 0; i < 12; i++)
        {
            scrolled.Write($"line {i}\n");
        }

        var ansi = new AnsiTerminalBuffer(40, 6);
        ansi.Write("\u001b[1;32mgreen\u001b[0m and \u001b[4munderlined\u001b[0m\n");
        ansi.Write("\u001b[2Jcleared above\u001b[H");

        return [empty, half, full, scrolled, ansi];
    }

    /// <summary>The patterns the parity proof searches for.</summary>
    private static readonly string[] Patterns =
    [
        "hello",
        "hello world",
        "row 3 of the grid",
        "underlined",
        "zzzz",
        "h",
        "o w",
        "row",
        "cleared above",
        "\t",
        "with a tab"
    ];

    /// <summary>
    ///     The new grid search answers exactly as the old one did.
    /// </summary>
    [Test]
    public async Task ContainsText_AnswersWhatTheOldSearchAnswered()
    {
        var failures = new List<string>();

        foreach (AnsiTerminalBuffer buffer in Grids())
        {
            foreach (string pattern in Patterns)
            {
                bool expected = AnsiTerminalBufferOracle.ContainsText(buffer, pattern);
                bool actual = buffer.ContainsText(pattern);

                if (expected != actual)
                {
                    failures.Add(
                        $"pattern '{pattern}': old said {expected}, new said {actual} "
                        + $"({buffer.Width}x{buffer.Height} grid)");
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("; ", failures));
    }

    /// <summary>
    ///     The new visible text is byte-for-byte the text the old one built.
    /// </summary>
    [Test]
    public async Task GetVisibleText_IsByteForByteTheOldText()
    {
        var failures = new List<string>();

        foreach (AnsiTerminalBuffer buffer in Grids())
        {
            string expected = AnsiTerminalBufferOracle.GetVisibleText(buffer);
            string actual = buffer.GetVisibleText();

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add(
                    $"old {expected.Length} chars, new {actual.Length} chars, and they differ: "
                    + $"old [{expected.Replace("\n", "\\n")}], new [{actual.Replace("\n", "\\n")}]");
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("; ", failures));
    }

    /// <summary>
    ///     The tear-tolerant grid search answers exactly as the old one did.
    /// </summary>
    [Test]
    public async Task ContainsTextIgnoringWhitespace_AnswersWhatTheOldSearchAnswered()
    {
        var failures = new List<string>();

        foreach (AnsiTerminalBuffer buffer in Grids())
        {
            foreach (string needle in new[] { "helloworld", "row3ofthegrid", "underlined", "zzz", "ow" })
            {
                bool expected = AnsiTerminalBufferOracle.ContainsTextIgnoringWhitespace(buffer, needle);
                bool actual = buffer.ContainsTextIgnoringWhitespace(needle);

                if (expected != actual)
                {
                    failures.Add($"needle '{needle}': old said {expected}, new said {actual}");
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("; ", failures));
    }

    /// <summary>
    ///     What the searches cost, before and after.
    /// </summary>
    /// <remarks>
    ///     The figures are printed, not asserted: a budget on them would be a
    ///     budget on the JIT, the runner and the machine. The point of the
    ///     measurement is the DIFFERENCE between the two sides of it, and both
    ///     sides are measured in the same process, on the same grid, in the
    ///     same run.
    /// </remarks>
    [Test]
    public async Task TheGridSearches_AllocateLessThanTheOldOnes()
    {
        var buffer = new AnsiTerminalBuffer(120, 50);
        for (int i = 0; i < 50; i++)
        {
            buffer.Write($"row {i} of a hundred and twenty column grid, with some text in it\n");
        }

        // Warm both sides first, so the first call's JIT is not in the figure.
        AnsiTerminalBufferOracle.ContainsText(buffer, "row 7");
        buffer.ContainsText("row 7");
        AnsiTerminalBufferOracle.GetVisibleText(buffer);
        buffer.GetVisibleText();

        long oldSearch = Measure(() => AnsiTerminalBufferOracle.ContainsText(buffer, "row 7 of a hundred"));
        long newSearch = Measure(() => buffer.ContainsText("row 7 of a hundred"));

        long oldVisible = Measure(() => AnsiTerminalBufferOracle.GetVisibleText(buffer));
        long newVisible = Measure(() => buffer.GetVisibleText());

        long oldTolerant = Measure(() => AnsiTerminalBufferOracle.ContainsTextIgnoringWhitespace(buffer, "row7ofahundredandtwentycolumngrid"));
        long newTolerant = Measure(() => buffer.ContainsTextIgnoringWhitespace("row7ofahundredandtwentycolumngrid"));

        Console.WriteLine(
            $"[alloc-cost] mechanism=ansi-terminal-grid-search before={oldSearch} after={newSearch} "
            + $"per-call={Iterations}");
        Console.WriteLine(
            $"[alloc-cost] mechanism=ansi-terminal-visible-text before={oldVisible} after={newVisible} "
            + $"per-call={Iterations}");
        Console.WriteLine(
            $"[alloc-cost] mechanism=ansi-terminal-tear-tolerant before={oldTolerant} after={newTolerant} "
            + $"per-call={Iterations}");

        await Assert.That(newSearch).IsLessThanOrEqualTo(oldSearch)
            .Because("the grid is read in place now, so the search must not allocate more than the old one did");
        await Assert.That(newVisible).IsLessThanOrEqualTo(oldVisible)
            .Because("the rows are appended into one builder now, so the text must not cost more than it did");
        await Assert.That(newTolerant).IsLessThanOrEqualTo(oldTolerant)
            .Because("the tear-tolerant search writes into a pooled buffer now, so it must not cost more");
    }

    /// <summary>
    ///     The bytes one call of <paramref name="body" /> allocates, averaged
    ///     over <see cref="Iterations" /> calls.
    /// </summary>
    /// <param name="body">The body to measure. Must not suspend.</param>
    /// <returns>The average number of bytes per call.</returns>
    private static long Measure(Action body)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            body();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return allocated / Iterations;
    }
}
