using System.Diagnostics;
using System.Threading.Tasks;
using Harbor.Tui.Abstractions.Rendering;
using Harbor.Tui.SpectreTui.View;
namespace Harbor.Tui.Tests;
public class GfmTableParserTests
{
    [Test]
    public async Task IsTableStart_DetectsHeaderPlusSeparator()
    {
        var lines = new[]
        {
            "| a | b |",
            "|---|---|",
            "| 1 | 2 |"
        };
        await Assert.That(GfmTableParser.IsTableStart(lines, 0)).IsTrue();
    }

    [Test]
    public async Task IsTableStart_FalseForLonePipeLine()
    {
        var lines = new[] { "| a | b |", "just text" };
        await Assert.That(GfmTableParser.IsTableStart(lines, 0)).IsFalse();
    }

    [Test]
    public async Task TryParse_ReadsHeadersRowsAlignments()
    {
        var lines = new[]
        {
            "| name | score |",
            "|------|:-----:|",
            "| bob | 10 |",
            "| sue | 20 |"
        };
        bool ok = GfmTableParser.TryParse(lines, 0, out var table, out int next);
        await Assert.That(ok).IsTrue();
        await Assert.That(table.Headers.Count).IsEqualTo(2);
        await Assert.That(table.Rows.Count).IsEqualTo(2);
        await Assert.That(table.Alignments[0]).IsEqualTo(GfmAlign.Left);
        await Assert.That(table.Alignments[1]).IsEqualTo(GfmAlign.Center);
        // consumed header + separator + 2 rows
        await Assert.That(next).IsEqualTo(4);
    }

    [Test]
    public async Task TryParse_PadsShortRowsAndStopsAtBlank()
    {
        var lines = new[]
        {
            "| a | b | c |",
            "|---|---|---|",
            "| 1 |",
            "",
            "after"
        };
        bool ok = GfmTableParser.TryParse(lines, 0, out var table, out int next);
        await Assert.That(ok).IsTrue();
        await Assert.That(table.Rows.Count).IsEqualTo(1);
        await Assert.That(table.Rows[0].Count).IsEqualTo(3);
        await Assert.That(table.Rows[0][2]).IsEqualTo(string.Empty);
        await Assert.That(next).IsEqualTo(3);
    }
}

public class GfmTableFormatterTests
{
    private static GfmTable Simple()
        => new(
            new[] { "name", "score" },
            new[] { new[] { "bob", "10" }, new[] { "sue", "20" } },
            new[] { GfmAlign.Left, GfmAlign.Right });

    [Test]
    public async Task Format_ProducesBorderedGrid()
    {
        var grid = GfmTableFormatter.Format(Simple());
        // top, header, sep, 2 data rows, bottom
        await Assert.That(grid.Count).IsEqualTo(6);
        await Assert.That(grid[0].StartsWith('┌')).IsTrue();
        await Assert.That(grid[1].Contains("name")).IsTrue();
        await Assert.That(grid[5].StartsWith('└')).IsTrue();
    }

    [Test]
    public async Task Format_RightAlignsNumericColumn()
    {
        var grid = GfmTableFormatter.Format(Simple());
        // row "sue | 20" is the 2nd data row → grid index 4 (0 top,1 header,2 sep,3 row0,4 row1).
        // Right-aligned "20" sits flush against the right border '│'.
        await Assert.That(grid[4].EndsWith("20 │") || grid[4].Contains(" 20 │")).IsTrue();
    }

    [Test]
    public async Task Format_ShrinksToMaxWidth()
    {
        var wide = new GfmTable(
            new[] { "col1", "col2", "col3" },
            new[]
            {
                new[] { "aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc" },
                new[] { "x", "y", "z" }
            },
            new[] { GfmAlign.Left, GfmAlign.Left, GfmAlign.Left });
        var grid = GfmTableFormatter.Format(wide, 40);
        foreach (var row in grid)
            await Assert.That(row.Length).IsLessThanOrEqualTo(40);
    }

    [Test]
    public async Task Format_HugeCellIsBounded()
    {
        var huge = new GfmTable(
            new[] { "a", "b" },
            new[] { new[] { new string('x', 200_000), "y" } },
            new[] { GfmAlign.Left, GfmAlign.Left });
        var sw = Stopwatch.StartNew();
        var grid = GfmTableFormatter.Format(huge, 80);
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(200);
        await Assert.That(grid[1].Length).IsLessThanOrEqualTo(80);
    }

    [Test]
    public async Task Format_AllRowsSameWidth_NoDesync()
    {
        var t = new GfmTable(
            new[] { "№", "Тип", "Имя", "Путь" },
            new[]
            {
                new[] { "1", "dir", "Папка", "D:\\RiderProjects\\ai-harness" },
                new[] { "2", "file", "readme", "C:\\Users\\pitch\\file.txt" }
            },
            new[] { GfmAlign.Left, GfmAlign.Left, GfmAlign.Left, GfmAlign.Left });
        var grid = GfmTableFormatter.Format(t, 50);
        int w = grid[0].Length;
        foreach (var row in grid)
            await Assert.That(row.Length).IsEqualTo(w);
        await Assert.That(w).IsLessThanOrEqualTo(50);
    }

    [Test]
    public async Task Format_NeverExceedsMaxWidth()
    {
        var t = new GfmTable(
            new[] { "command", "description" },
            new[]
            {
                new[]
                {
                    "verylongcommandname",
                    "Some fairly long description text that must be truncated to fit the panel width"
                }
            },
            new[] { GfmAlign.Left, GfmAlign.Left });
        var grid = GfmTableFormatter.Format(t, 36);
        foreach (var row in grid)
            await Assert.That(DispLen(row)).IsLessThanOrEqualTo(36);
    }

    private static int DispLen(string s)
    {
        int w = 0;
        foreach (var c in s)
        {
            bool wide = (c >= 0x1100 && c <= 0x115F) ||
                        (c >= 0x2E80 && c <= 0xA4CF) ||
                        (c >= 0xAC00 && c <= 0xD7A3) ||
                        (c >= 0xF900 && c <= 0xFAFF) ||
                        (c >= 0xFF00 && c <= 0xFFE6);
            w += wide ? 2 : 1;
        }
        return w;
    }
}

public class ChatMarkdownInlineTests
{
    [Test]
    public async Task ToSpans_UnmatchedDelimiters_DoesNotHang()
    {
        string[] inputs =
        {
            "* unmatched bold",
            "_ stray underscore",
            "*mixed* but _ unclosed",
            "**bold then * again",
            "text ` no close backtick",
            "a*b_c_d*e"
        };

        foreach (var input in inputs)
        {
            var task = Task.Run(() => ChatMarkdown.ToSpans(input).ToList());
            var completed = await Task.WhenAny(task, Task.Delay(2000));
            await Assert.That(completed == task).IsTrue(); // not the timeout
            await Assert.That(task.IsCompleted).IsTrue();   // finished, not hung
            _ = task.Result; // must have produced spans
        }
    }

    [Test]
    public async Task ToSpans_HugeRunawayInput_IsBounded()
    {
        var input = "*" + new string('x', 100_000) + "_tail";
        var sw = Stopwatch.StartNew();
        var spans = ChatMarkdown.ToSpans(input).ToList();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(200);
        await Assert.That(spans.Count).IsGreaterThan(0);
    }
}
