// TestRenderArtParityTests.cs — the art dump is PROVED against the old one,
// and MEASURED against it.
//
// WHAT THIS FILE IS
// -----------------
// `TestRender.Art` is how every CellForge widget test reads the screen back:
// the art is the picture, and the assertions are made on the picture's
// characters. The golden frames record it as a fixture, so the art is not free
// to change by a single character.
//
// It used to APPEND STRINGS: `((char)rune).ToString()` allocated a
// one-character string for every cell of every grid. An 80x24 grid is 1 920
// cells, so one call allocated 1 920 short-lived strings — about 46 KiB of
// garbage for a picture that is 2 KiB long — and `Art` is called by every
// widget test in the CellForge suite, and once per frame by the golden
// frames.
//
// So the file:
//
//   1. requires the art to be BYTE-FOR-BYTE the art the old code produced,
//      over generated grids; and
//   2. MEASURES what each side allocated, and prints the two numbers.
//
// THE MEASUREMENT
// ---------------
// `GC.GetAllocatedBytesForCurrentThread()` counts the bytes the CALLING THREAD
// allocated. The bodies measured here are synchronous and never await, so the
// per-thread counter is the right one.
//
// The grid is prepared OUTSIDE the measured window in both cases: what the
// figure measures is the DUMP.

using System.Text;
using Harbor.TestKit;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The parity proof and the allocation measurement for the art dump.
/// </summary>
public class TestRenderArtParityTests
{
    /// <summary>How many times a measured body runs, so the figure is a per-call one.</summary>
    private const int Iterations = 200;

    /// <summary>
    ///     The grids the parity proof dumps.
    /// </summary>
    private static IEnumerable<ScreenBuffer> Grids()
    {
        var empty = TestRender.Buffer(20, 4);

        var text = TestRender.Buffer(20, 4);
        text.SetText(0, 0, "hello", CellStyle.Plain);
        text.SetText(0, 1, "world", CellStyle.Plain);

        var full = TestRender.Buffer(40, 3);
        for (int y = 0; y < 3; y++)
        {
            full.SetText(0, y, new string('=', 40), CellStyle.Plain);
        }

        var control = TestRender.Buffer(20, 3);
        control.SetText(0, 0, "tab\there", CellStyle.Plain);
        control.SetText(0, 1, "bell\u0007and\u001bescape", CellStyle.Plain);

        var wide = TestRender.Buffer(30, 3);
        wide.SetText(0, 0, "中文混排", CellStyle.Plain);
        wide.SetText(0, 1, "emoji \U0001F600 here", CellStyle.Plain);

        return [empty, text, full, control, wide];
    }

    /// <summary>
    ///     The art is byte-for-byte the art the old dump produced.
    /// </summary>
    [Test]
    public async Task Art_IsByteForByteTheOldDump()
    {
        var failures = new List<string>();

        foreach (ScreenBuffer grid in Grids())
        {
            string expected = LegacyArt(grid);
            string actual = TestRender.Art(grid);

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add(
                    $"old [{expected.Replace("\n", "\\n")}], new [{actual.Replace("\n", "\\n")}]");
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("; ", failures));
    }

    /// <summary>
    ///     What the dump costs, before and after.
    /// </summary>
    [Test]
    public async Task TheArtDump_AllocatesLessThanTheOldOne()
    {
        var grid = TestRender.Buffer(80, 24);
        for (int y = 0; y < 24; y++)
        {
            grid.SetText(0, y, new string((char)('a' + (y % 26)), 80), CellStyle.Plain);
        }

        // Warm both sides first, so the first call's JIT is not in the figure.
        LegacyArt(grid);
        TestRender.Art(grid);

        long oldDump = Measure(() => LegacyArt(grid));
        long newDump = Measure(() => TestRender.Art(grid));

        Console.WriteLine(
            $"[alloc-cost] mechanism=cellforge-art-dump before={oldDump} after={newDump} "
            + $"per-call={Iterations}");

        await Assert.That(newDump).IsLessThanOrEqualTo(oldDump)
            .Because("the dump appends characters now, so it must not allocate more than the string-per-cell one did");
    }

    /// <summary>
    ///     The OLD dump, verbatim: a string per cell.
    /// </summary>
    /// <param name="buffer">The grid to dump.</param>
    /// <returns>The art.</returns>
    private static string LegacyArt(ScreenBuffer buffer)
    {
        var sb = new StringBuilder();
        for (int y = 0; y < buffer.Rows; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                var cell = buffer.Get(x, y);
                if (cell.Width == Cell.WSkip)
                {
                    continue;
                }

                sb.Append(LegacyArtChar(cell.Rune));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    ///     The OLD cell character, verbatim: a string per cell.
    /// </summary>
    /// <param name="rune">The cell's rune.</param>
    /// <returns>The character, as a string.</returns>
    private static string LegacyArtChar(int rune) => rune switch
    {
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFFFD when !char.IsSurrogate((char)rune) => ((char)rune).ToString(),
        _ => "?",
    };

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
