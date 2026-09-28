using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// #487 rewrote <see cref="StatusBarLayout.Fit"/> from "re-sum the whole row
/// inside its own shrink loop" to "measure once, carry the running total". The
/// rewrite is allowed to change cost and nothing else, so everything here is a
/// behaviour pin.
///
/// <para>The pin that matters is the overflow one. A faster <c>Fit</c> that
/// paints past the end of the terminal row is worse than the quadratic it
/// replaced, and the widths worth checking are the pathological ones: 0, 1, 2
/// cells, where every character cut has to give and the row can become
/// irreducible. At those widths the contract is not "fits" but
/// <b>fits, or every survivor is down to one cell</b> — beyond that there is
/// nothing left to give, and the honest answer is to stop rather than drop a
/// fixed-priority segment or spin.</para>
///
/// <para>Drop order and packing are already pinned in
/// <c>Harbor.Tui.CellForge.Tests.StatusSegmentBarTests</c> against real
/// projected rows; this file pins the rewrite against the same properties on
/// shapes that no renderer builds, because those are the shapes that reach the
/// loops.</para>
/// </summary>
public class StatusBarFitTests
{
    /// <summary>Widths, including the pathological narrow ones, plus the real ones.</summary>
    private static readonly int[] Widths =
    [
        0, 1, 2, 3, 4, 5, 6, 8, 11, 14, 18, 24, 30, 40, 56, 79, 80, 100, 120, 160, 400,
    ];

    /// <summary>
    /// The shape a real status row has: two fixed-priority segments (model, mode
    /// hint) that must survive, then the flexible run — context bar, tokens,
    /// cost, elapsed — which dies from the right edge inward.
    /// </summary>
    private static StatusSeg[] StandardRow(int segments)
    {
        var row = new StatusSeg[segments];
        row[0] = new StatusSeg("kilocode/hilo3", StatusAccent.Accent, FixedPriority: true);
        row[1] = new StatusSeg("⏸ awaiting approval", StatusAccent.Warning, FixedPriority: true);
        for (int i = 2; i < segments; i++)
        {
            row[i] = new StatusSeg($"seg-{i} 12k↑ 4.5k", StatusAccent.Dim, FixedPriority: false);
        }

        return row;
    }

    /// <summary>
    /// A row of wide runes and emoji, where a character cut overshoots the cell
    /// budget (there is no one-cell prefix of a two-cell rune) and where the
    /// "does it fit" arithmetic is decided by the width table, not by
    /// <c>string.Length</c>.
    /// </summary>
    private static StatusSeg[] WideRuneRow(int segments)
    {
        var row = new StatusSeg[segments];
        row[0] = new StatusSeg("日本語モデル", StatusAccent.Accent, FixedPriority: true);
        row[1] = new StatusSeg("⏸ 承認待ち", StatusAccent.Warning, FixedPriority: true);
        for (int i = 2; i < segments; i++)
        {
            row[i] = new StatusSeg($"指标{i} 🙂 12k↑", StatusAccent.Success, FixedPriority: false);
        }

        return row;
    }

    private static string? OverflowProblem(StatusSeg[] row, int kept, int width)
    {
        int total = StatusBarLayout.TotalWidth(row.AsSpan()[..kept]);
        if (total <= width)
        {
            return null;
        }

        for (int i = 0; i < kept; i++)
        {
            int cells = UnicodeWidth.Width(row[i].Text);
            if (cells > 1)
            {
                return $"width {width}: row is {total} cells and survivor {i} ('{row[i].Text}') still holds {cells}";
            }
        }

        // Over budget with every survivor at one cell: the row cannot be made
        // narrower without dropping a fixed segment. That is the documented end
        // state, not a failure.
        return null;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Fit_NeverOverflows_TheWidthItWasGiven(bool wideRunes)
    {
        var problems = new List<string>();
        foreach (int segments in new[] { 2, 3, 6, 12, 24 })
        {
            foreach (int width in Widths)
            {
                StatusSeg[] row = wideRunes ? WideRuneRow(segments) : StandardRow(segments);
                int kept = StatusBarLayout.Fit(row, width);
                string? problem = OverflowProblem(row, kept, width);
                if (problem is not null)
                {
                    problems.Add($"{(wideRunes ? "wide" : "narrow")}/{segments} segs — {problem}");
                }
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because(
                "after Fit the bar must never paint past its width; when the row cannot be "
                + "narrower every survivor must be down to a single cell.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Fit_KeepsTheFixedSegments_AtEveryWidth(bool wideRunes)
    {
        // "Nothing is dropped" for a status bar means the model and the mode hint
        // survive every width, in place, at the left edge — possibly cut down to
        // a cell, but never removed and never replaced by a flexible segment.
        var problems = new List<string>();
        foreach (int segments in new[] { 3, 6, 12, 24 })
        {
            string model = wideRunes ? "日本語モデル" : "kilocode/hilo3";
            string hint = wideRunes ? "⏸ 承認待ち" : "⏸ awaiting approval";
            foreach (int width in Widths)
            {
                StatusSeg[] row = wideRunes ? WideRuneRow(segments) : StandardRow(segments);
                int kept = StatusBarLayout.Fit(row, width);
                if (kept < 2)
                {
                    problems.Add($"{segments} segs @ {width}: kept {kept} — both fixed segments must survive");
                }
                else if (!row[0].FixedPriority
                    || !row[1].FixedPriority
                    || !model.StartsWith(row[0].Text, StringComparison.Ordinal)
                    || !hint.StartsWith(row[1].Text, StringComparison.Ordinal))
                {
                    problems.Add($"{segments} segs @ {width}: survivors no longer start with the fixed pair");
                }
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because("a fixed-priority segment is never a truncation victim; Fit clamps it, it does not drop it.");
    }

    [Test]
    public async Task Fit_DropsTheRightmostFlexibleFirst_LeavingAnExactPrefix()
    {
        // Width for the fixed pair plus three of the flexible run: the rest must
        // die, rightmost first, and the survivors must be the untouched leading
        // entries — the packing compacts in place, so a survivor that moved would
        // mean a victim was taken from the wrong end.
        StatusSeg[] row = StandardRow(6);
        int width = StatusBarLayout.TotalWidth(row.AsSpan()[..5]);
        string[] expected = row.Take(5).Select(s => s.Text).ToArray();

        int kept = StatusBarLayout.Fit(row, width);

        await Assert.That(kept).IsEqualTo(5);
        await Assert.That(StatusBarLayout.TotalWidth(row.AsSpan()[..kept])).IsEqualTo(width);
        for (int i = 0; i < kept; i++)
        {
            await Assert.That(row[i].Text).IsEqualTo(expected[i]);
        }
    }

    [Test]
    public async Task Fit_Terminates_OnARowThatCannotGiveAnotherCell()
    {
        // Pre-#487 this spun forever. Three one-cell fixed segments at width 1:
        // pass 1 has no flexible victim, pass 2 clamps the widest to one cell,
        // and the clamp is a no-op because it is already one cell — so the loop
        // re-clamped the same segment until the process was killed. Every width
        // below the number of surviving fixed segments reached it.
        var problems = new List<string>();
        foreach (int width in new[] { 0, 1, 2, 3 })
        {
            var row = new StatusSeg[]
            {
                new("a", StatusAccent.Accent, FixedPriority: true),
                new("b", StatusAccent.Accent, FixedPriority: true),
                new("c", StatusAccent.Accent, FixedPriority: true),
            };

            int kept = StatusBarLayout.Fit(row, width);
            if (kept != 3)
            {
                problems.Add($"width {width}: kept {kept}, a fixed segment was dropped");
            }

            string? overflow = OverflowProblem(row, kept, width);
            if (overflow is not null)
            {
                problems.Add(overflow);
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because(
                "an irreducible row must end in the documented state (one cell per fixed segment), "
                + "not in a hang and not in a dropped fixed segment.");
    }

    [Test]
    public async Task Fit_EmptySpan_IsANoOp()
    {
        var row = Array.Empty<StatusSeg>();
        await Assert.That(StatusBarLayout.Fit(row, 40)).IsEqualTo(0);
        await Assert.That(StatusBarLayout.Fit(row, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task Fit_NegativeWidth_Throws()
    {
        var row = StandardRow(4);
        await Assert.That(() => { _ = StatusBarLayout.Fit(row, -1); })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Fit_WideRow_KeepsTheRowWhole_WhenItAlreadyFits()
    {
        // The other direction: a rewrite that trims defensively would show up
        // here as a shortened row, which is a visible diff in the status bar.
        StatusSeg[] row = StandardRow(6);
        int natural = StatusBarLayout.TotalWidth(row);
        var texts = new string[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            texts[i] = row[i].Text;
        }

        int kept = StatusBarLayout.Fit(row, natural);

        await Assert.That(kept).IsEqualTo(row.Length);
        for (int i = 0; i < kept; i++)
        {
            await Assert.That(row[i].Text).IsEqualTo(texts[i]);
        }
    }
}
