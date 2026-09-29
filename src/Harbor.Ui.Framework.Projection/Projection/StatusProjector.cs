using System.Collections.Immutable;
using System.Linq;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     Projects the chrome regions of <see cref="UiState" />. The status bar is
///     packed from <see cref="StatusBarFacts" /> — the one place the cells are
///     derived (#488) — so this class never formats a value itself and the
///     CellForge footer can read the same cells without going through here.
/// </summary>
/// <remarks>
///     <para>
///         <b>This method is the registration</b> (#568). Every cell a status bar
///         can show is named here, once, with everything that decides how it
///         renders: which third of the bar it sits in, where it sorts among its
///         neighbours, its style, and whether truncation is allowed to drop it.
///         A renderer that paints a cell therefore has nothing left to decide —
///         it maps <see cref="UiStatusSegment.Style" /> to its own accent and
///         reads <see cref="UiStatusSegment.FixedPriority" /> through.
///     </para>
///     <para>
///         That is what makes "add a segment" a one-file change. It used to be
///         one edit here, one in the CellForge footer's hand-written row (which
///         re-derived both the order and the priority), and one more in any
///         other surface that wanted the cell. The priority in particular had
///         nowhere to live: <see cref="UiStatusSegment" /> did not carry it, so
///         each renderer re-derived it and they did not agree.
///     </para>
/// </remarks>
public static class StatusProjector
{
    /// <summary>
    ///     The status-bar cells, in the order they are declared. Declaration
    ///     order is <i>not</i> paint order — <see cref="StatusSegmentOrdering" />
    ///     owns that, and a renderer that wants a specific sequence asks for it
    ///     rather than keeping its own.
    /// </summary>
    public static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        var facts = StatusBarFacts.Of(state);
        var segments = ImmutableArray.CreateBuilder<UiStatusSegment>();

        if (facts.Chrome is not null)
        {
            // Fixed: the session's identity. A truncated status bar that has
            // forgotten which provider and model it is talking about is worse
            // than one missing a number.
            //
            // Accent, not Default: that is the accent the CellForge footer has
            // always painted this cell, and the two disagreed — the projection
            // said Default while the shipped row said Accent. Declaring the
            // style the row actually renders is the point of this method.
            segments.Add(new UiStatusSegment(
                Text: facts.Chrome,
                Align: Alignment.Left,
                Importance: 1,
                Style: UiSpanStyle.Accent,
                FixedPriority: true));
        }

        segments.Add(new UiStatusSegment(
            Text: facts.Status,
            Align: Alignment.Center,
            Importance: 2,
            Style: facts.StatusStyle,
            // Fixed: what the run is doing right now.
            FixedPriority: true));

        if (facts.Agent is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Agent,
                Align: Alignment.Right,
                Importance: 6,
                Style: UiSpanStyle.Default));
        }

        // The right-hand group, in the order the row paints it:
        //   agent 6 › [retry 5] › [skills 5] › scroll 4 › [elapsed 4] › tokens 3 › cost 2
        // (brackets: the CellForge host slots, which this projection does not
        // own — see StatusProjectorPanel.BuildSegments.)
        //
        // `StatusSegmentOrdering` sorts right-aligned cells by DESCENDING
        // importance, so the highest number paints leftmost, and
        // `StatusSegmentBar.Fit` drops the RIGHTMOST flexible cell first. The
        // ranking is therefore the truncation order read right to left: cost
        // dies first, then tokens, then elapsed, then scroll, and the agent name
        // — the cell that says who is answering — outlives all of them.
        //
        // The projection used to rank scroll LAST among right-aligned cells,
        // which put it rightmost and made it the first thing a narrow terminal
        // dropped, while the CellForge footer ranked it above tokens and cost
        // and kept it. Same session, two answers. The numbers here are the one
        // ranking both surfaces read.
        segments.Add(new UiStatusSegment(
            Text: facts.Scroll,
            Align: Alignment.Right,
            Importance: 4,
            Style: UiSpanStyle.Dim));

        if (facts.Tokens is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Tokens,
                Align: Alignment.Right,
                Importance: 3,
                Style: UiSpanStyle.Dim));
        }

        if (facts.Cost is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Cost,
                Align: Alignment.Right,
                Importance: 2,
                Style: UiSpanStyle.Dim));
        }

        return new UiStatusBarModel(Segments: segments.ToImmutable());
    }

    public static string ProjectFooter(UiState state)
    {
        var statusBar = ProjectStatusBar(state);
        var ordered = StatusSegmentOrdering.Ordered(statusBar.Segments);

        // One join over the ordered cells, not three group joins concatenated:
        // #488 made the chrome cell nullable (no provider and no model ⇒ no
        // cell), and a per-group join would then leave a leading "  " behind.
        return string.Join("  ", ordered.Select(s => s.Text));
    }
}
