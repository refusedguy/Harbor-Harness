namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #850: #740 bounded the hunk header's line number at
/// <see cref="int.MaxValue"/> and stopped there. The row counter that header
/// <em>seeds</em> is incremented once per body row — one step further, with
/// no check of its own.
///
/// <para>Why this is the parser's problem and not the format check's. The
/// input is not ours. <c>ToolCardTracker.TryExtractDiff</c> hands
/// <c>UnifiedDiffParser.Parse</c> <em>any</em> tool result whose first line
/// starts with <c>"--- "</c>, <c>"@@ -"</c> or <c>"diff --git"</c>, and an
/// MCP server's resource or prompt body reaches that gate verbatim
/// (<c>McpResourceTool</c>/<c>McpPromptTool</c> over an out-of-process
/// third-party program). <c>@@ -2147483647,1 +1,1 @@</c> is well formed
/// under #740's own rule — #740's suite positively asserts it is a hunk, in
/// <c>DiffHunkHeaderFormatTests.Parse_HunkHeaderBoundIsIntMaxValue_NotADigitCount</c>
/// — so the format check admits it and the increment one line later wraps.
/// The trigger is ~40 bytes of third-party tool output; the two-billion-line
/// file that #740's comment assumed this would need is not required, and is
/// supplied instead by #740's own bound test.</para>
///
/// <para>Why unchecked arithmetic is the defect rather than a rendering
/// detail. C# arithmetic is unchecked by default, so <c>++oldNo</c> at
/// <see cref="int.MaxValue"/> yields <see cref="int.MinValue"/> — no
/// exception, no diagnostic. A row numbered below zero contradicts the
/// header the parser had just accepted, and <c>DiffBlock.NumberField</c>
/// blanks anything &lt;= 0, so the cost is not a crash: it is a gutter that
/// goes silently blank on rows that do have a number.</para>
///
/// <para>What the guard deliberately does <em>not</em> assert: that large
/// line numbers are rejected. They are not, and #740 already lost one
/// argument about this — a four-digit cap is #737's width bug re-introduced
/// as a parse bug. The bound here is the <em>arithmetic</em> one, and the
/// anti-over-tightening case is asserted explicitly below.
/// </summary>
public class DiffLineNumberWrapTests
{
    /// <summary>
    /// The whole trigger, in one line of input: a header at the bound #740
    /// accepts, and the single context row underneath it.
    /// </summary>
    [Test]
    public async Task Parse_HeaderAtIntMaxValue_ContextRowDoesNotWrap()
    {
        var ctx = UnifiedDiffParser.Parse("@@ -2147483647,1 +2147483647,1 @@\n ctx\n")
            .Single(l => l.Kind == DiffLineKind.Context);

        await Assert.That(ctx.OldNo).IsGreaterThan(0)
            .Because("the old counter was seeded at int.MaxValue and this row consumed it — one past the bound must not wrap below zero");
        await Assert.That(ctx.NewNo).IsGreaterThan(0)
            .Because("the new counter was seeded at int.MaxValue and this row consumed it — one past the bound must not wrap below zero");
    }

    /// <summary>
    /// The whole run, not just the first row past the bound. Zero on one
    /// side is meaningful — it is how "n/a for this kind" is spelled, and it
    /// is what <c>DiffBlock.Gutter</c> renders blank — so the assertion is
    /// "never negative", not "always positive".
    /// </summary>
    [Test]
    public async Task Parse_HeaderAtIntMaxValue_NoRowBelowItCarriesANegativeNumber()
    {
        var lines = UnifiedDiffParser.Parse(
            "@@ -2147483647,1 +2147483647,1 @@\n ctx\n-removed\n+added\n ctx\n");

        var body = lines
            .Where(l => l.Kind is DiffLineKind.Context or DiffLineKind.Add or DiffLineKind.Delete)
            .ToList();

        await Assert.That(body.Count).IsEqualTo(4)
            .Because("every row under the header must be parsed, or the guard could pass by dropping the rows that wrapped");

        foreach (var l in body)
        {
            await Assert.That(l.OldNo >= 0 && l.NewNo >= 0).IsTrue()
                .Because($"a {l.Kind} row resolved to old={l.OldNo} new={l.NewNo}; only 0 may stand for 'n/a'");
        }
    }

    /// <summary>
    /// One side at the bound is enough. The two counters are independent, so
    /// a case that put both at the bound could be passed by a fix guarding
    /// only one of them.
    /// </summary>
    [Test]
    [Arguments("@@ -1,1 +2147483647,1 @@\n+added\n")]      // new side only
    [Arguments("@@ -2147483647,1 +1,1 @@\n-removed\n")]    // old side only
    public async Task Parse_HeaderAtIntMaxValueOnOneSide_ThatSideDoesNotWrap(string diff)
    {
        var row = UnifiedDiffParser.Parse(diff)
            .Single(l => l.Kind is DiffLineKind.Add or DiffLineKind.Delete);

        await Assert.That(row.OldNo >= 0 && row.NewNo >= 0).IsTrue()
            .Because($"the side seeded at int.MaxValue produced old={row.OldNo} new={row.NewNo}");
    }

    /// <summary>
    /// The run past the first row. Whatever the arithmetic does at the bound,
    /// a row number advances with the file: it must stay positive and must
    /// never come back <em>smaller</em> than the row before it. Asserted as
    /// monotonicity rather than as a specific sentinel so the guard states the
    /// invariant, not one way of meeting it.
    /// </summary>
    [Test]
    public async Task Parse_HeaderAtIntMaxValue_SecondRowStaysPositiveAndNeverGoesBackwards()
    {
        var dels = UnifiedDiffParser.Parse("@@ -2147483647,1 +1,1 @@\n-removed\n-removed\n")
            .Where(l => l.Kind == DiffLineKind.Delete)
            .ToList();

        await Assert.That(dels.Count).IsEqualTo(2)
            .Because("both rows under the header must be parsed");

        await Assert.That(dels[1].OldNo).IsGreaterThan(0)
            .Because("the second row is two past the bound, so it must not be a wrapped value either");
        await Assert.That(dels[1].OldNo).IsGreaterThanOrEqualTo(dels[0].OldNo)
            .Because("row numbers advance with the file; a row must never be numbered below the row before it");
    }

    /// <summary>
    /// The cost, stated the way a user meets it: the gutter. A number the
    /// parser resolved has to render as a number.
    /// <c>DiffBlock.NumberField</c> blanks anything &lt;= 0, so a wrapped
    /// counter is not a wrong digit in the gutter — it is a row with no
    /// number at all, indistinguishable from a diff that has none.
    /// </summary>
    [Test]
    public async Task Parse_HeaderAtIntMaxValue_EveryResolvedNumberStillReachesTheGutter()
    {
        var lines = UnifiedDiffParser.Parse(
            "@@ -2147483647,1 +2147483647,1 @@\n ctx\n-removed\n+added\n");

        foreach (var l in lines.Where(l => l.Kind is DiffLineKind.Context or DiffLineKind.Add or DiffLineKind.Delete))
        {
            foreach (var (no, side) in new[] { (l.OldNo, "old"), (l.NewNo, "new") })
            {
                if (no == 0)
                {
                    continue; // n/a for this row's kind — legitimately blank
                }

                await Assert.That(DiffBlock.NumberField(no).Trim()).IsNotEmpty()
                    .Because($"the {side} counter of a {l.Kind} row resolved to {no}, which the gutter paints as an empty field");
            }
        }
    }

    /// <summary>
    /// The anti-over-tightening half, and the one that stays green before and
    /// after the fix: a number one below the bound still increments
    /// normally. The bound belongs to the arithmetic, not to the file size —
    /// #740 learned that once already, and a fix that clamps at
    /// <see cref="int.MaxValue"/> − 1 would break it.
    /// </summary>
    [Test]
    public async Task Parse_HeaderJustBelowIntMaxValue_StillIncrementsNormally()
    {
        var ctx = UnifiedDiffParser.Parse("@@ -2147483646,1 +2147483646,1 @@\n ctx\n")
            .Single(l => l.Kind == DiffLineKind.Context);

        await Assert.That(ctx.OldNo).IsEqualTo(int.MaxValue)
            .Because("one below the bound is a line number like any other and must increment to the bound itself");
        await Assert.That(ctx.NewNo).IsEqualTo(int.MaxValue);
    }
}
