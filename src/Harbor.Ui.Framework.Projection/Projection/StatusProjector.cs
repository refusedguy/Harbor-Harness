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
public static class StatusProjector
{
    public static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        var facts = StatusBarFacts.Of(state);
        var segments = ImmutableArray.CreateBuilder<UiStatusSegment>();

        if (facts.Chrome is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Chrome,
                Align: Alignment.Left,
                Importance: 1,
                Style: UiSpanStyle.Default));
        }

        segments.Add(new UiStatusSegment(
            Text: facts.Status,
            Align: Alignment.Center,
            Importance: 2,
            Style: facts.StatusStyle));

        if (facts.Agent is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Agent,
                Align: Alignment.Right,
                Importance: 3,
                Style: UiSpanStyle.Default));
        }

        if (facts.Tokens is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Tokens,
                Align: Alignment.Right,
                Importance: 2,
                Style: UiSpanStyle.Dim));
        }

        if (facts.Cost is not null)
        {
            segments.Add(new UiStatusSegment(
                Text: facts.Cost,
                Align: Alignment.Right,
                Importance: 1,
                Style: UiSpanStyle.Dim));
        }

        segments.Add(new UiStatusSegment(
            Text: facts.Scroll,
            Align: Alignment.Right,
            Importance: 0,
            Style: UiSpanStyle.Dim));

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
