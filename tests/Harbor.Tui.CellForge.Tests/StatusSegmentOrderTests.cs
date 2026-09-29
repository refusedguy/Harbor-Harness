using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #568 drift guard, part 2 — <b>one order, not two</b>.
/// </summary>
/// <remarks>
///     <para>
///         <c>StatusSegmentOrdering</c> exists, by its own doc comment, so that
///         "every backend sorts identically and cannot drift apart". Measured on
///         this tree, <c>StatusProjectorPanel.BuildSegments</c> does not call it:
///         it re-derives the row order by hand.
///     </para>
///     <para>
///         The two orders are not the same list. Today the projection sorts
///         right-aligned cells by descending importance — agent(3), tokens(2),
///         cost(1), scroll(0) — putting <b>scroll last</b>; the footer spells
///         scroll out <b>before</b> tokens and cost, because its truncation
///         contract is "tokens/cost rightmost, die first". So the same session
///         puts its scroll indicator in two different places depending on which
///         backend painted it, and
///         <c>StatusBarFactsParityTests</c> cannot see it: that test compares the
///         <i>set</i> of cells, never their sequence.
///     </para>
///     <para>
///         The test below compares sequences. It is red on this tree, at the
///         scroll cell.
///     </para>
/// </remarks>
public class StatusSegmentOrderTests
{
    private static UiState State(
        string status = "running",
        string provider = "prov",
        string model = "m",
        string agent = "code",
        long tokensIn = 1500,
        long tokensOut = 300,
        decimal costUsd = 0.0042m,
        int scrollOffset = 60,
        int viewportLines = 20,
        int totalLines = 100) => new()
        {
            Ui = TerminalUiState.Empty with
            {
                ScrollOffset = scrollOffset,
                ViewportLines = viewportLines,
                TotalLines = totalLines
            },
            Chat = ChatDomainState.Empty with
            {
                Status = status,
                Provider = provider,
                Model = model,
                AgentName = agent,
                Cost = new CostSnapshot(tokensIn, tokensOut, costUsd)
            }
        };

    /// <summary>The projected cells, in the order the one ordering function returns them.</summary>
    private static string[] Ordered(UiState state)
    {
        var ordered = StatusSegmentOrdering.Ordered(StatusProjector.ProjectStatusBar(state).Segments);
        var texts = new string[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            texts[i] = ordered[i].Text;
        }

        return texts;
    }

    /// <summary>
    ///     The footer's cells, minus the host-driven slots only the CellForge
    ///     row has (retry, the freshness pill, run duration) — those are not
    ///     part of the projection and are not what this test is about.
    /// </summary>
    private static string[] Footer(UiState state)
    {
        var workspace = new StatusSeg[StatusProjectorPanel.MaxSegments];
        int n = StatusProjectorPanel.BuildSegments(state, workspace);
        var texts = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            texts.Add(workspace[i].Text);
        }

        return texts.ToArray();
    }

    /// <summary>
    ///     The load-bearing assertion: the two surfaces must emit the same cells
    ///     <b>in the same sequence</b>.
    ///     <para>
    ///         Red today. The projection orders agent, tokens, cost, scroll; the
    ///         footer orders agent, scroll, tokens, cost. The first mismatch is
    ///         the scroll cell.
    ///     </para>
    /// </summary>
    [Test]
    public async Task BothSurfaces_EmitTheSameCells_InTheSameOrder()
    {
        var state = State();

        var ordered = Ordered(state);
        var footer = Footer(state);

        await Assert.That(string.Join(" | ", ordered))
            .IsEqualTo(string.Join(" | ", footer));
    }

    /// <summary>
    ///     The truncation contract, stated once and checked on both sides: the
    ///     rightmost flexible cells die first, so cost and tokens must sit at
    ///     the right edge of the row and scroll must not be to their right.
    ///     <para>
    ///         This is the half of the order that is a <i>decision</i> rather
    ///         than a convention — which is why it lives in the ordering data
    ///         and not in each renderer's control flow.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CostAndTokens_SitRightOfScroll_SoTheyTruncateFirst()
    {
        var state = State();

        var ordered = Ordered(state);
        int scroll = Array.IndexOf(ordered, "scroll 75%");
        int cost = Array.IndexOf(ordered, "$0.0042");
        int tokens = Array.IndexOf(ordered, "1.5K↑ 300↓");

        await Assert.That(scroll >= 0).IsTrue();
        await Assert.That(cost >= 0).IsTrue();
        await Assert.That(tokens >= 0).IsTrue();

        // "rightmost ⇒ dropped first" is what StatusSegmentBar.Fit implements;
        // a cell placed to the right of these outlives them, which is the
        // silent bug the hand-maintained order allowed.
        await Assert.That(scroll < cost).IsTrue();
        await Assert.That(scroll < tokens).IsTrue();
    }

    /// <summary>
    ///     The same contract on the CellForge row, asserted directly rather
    ///     than inferred from parity: <c>StatusBarLayout.Fit</c> drops
    ///     right-to-left, so this is the order that decides what a narrow
    ///     terminal still shows.
    /// </summary>
    [Test]
    public async Task Footer_KeepsCostAndTokens_RightOfScroll()
    {
        var state = State();

        var footer = Footer(state);
        int scroll = Array.IndexOf(footer, "scroll 75%");
        int cost = Array.IndexOf(footer, "$0.0042");
        int tokens = Array.IndexOf(footer, "1.5K↑ 300↓");

        await Assert.That(scroll < cost).IsTrue();
        await Assert.That(scroll < tokens).IsTrue();
    }
}
