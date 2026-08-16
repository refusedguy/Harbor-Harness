using System.Globalization;
using System.Collections.Immutable;
using System.Linq;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

public static class StatusProjector
{
    public static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        var segments = ImmutableArray.CreateBuilder<UiStatusSegment>();

        segments.Add(new UiStatusSegment(
            Text: $"{state.Provider}/{state.Model}",
            Align: Alignment.Left,
            Importance: 1,
            Style: UiSpanStyle.Default));

        string glyph = state.Status switch
        {
            "running" => "▌",
            "compacting" => "◐",
            "error" => "✗",
            _ => "○"
        };
        UiSpanStyle statusStyle = state.Status switch
        {
            "running" => UiSpanStyle.Accent,
            "error" => UiSpanStyle.Danger,
            _ => UiSpanStyle.Default
        };
        segments.Add(new UiStatusSegment(
            Text: $"{glyph} {state.Status}",
            Align: Alignment.Center,
            Importance: 2,
            Style: statusStyle));

        if (!string.IsNullOrEmpty(state.AgentName))
        {
            segments.Add(new UiStatusSegment(
                Text: $"agent {state.AgentName}",
                Align: Alignment.Right,
                Importance: 3,
                Style: UiSpanStyle.Default));
        }

        if (state.Cost.TokensIn > 0 || state.Cost.TokensOut > 0)
        {
            segments.Add(new UiStatusSegment(
                Text: $"{state.Cost.TokensIn}↑ {state.Cost.TokensOut}↓",
                Align: Alignment.Right,
                Importance: 2,
                Style: UiSpanStyle.Dim));
        }

        segments.Add(new UiStatusSegment(
            Text: state.Cost.CostUsd.ToString("F4", CultureInfo.InvariantCulture),
            Align: Alignment.Right,
            Importance: 1,
            Style: UiSpanStyle.Dim));

        int maxScroll = Math.Max(0, state.TotalLines - Math.Max(1, state.ViewportLines));
        string scrollText = maxScroll == 0 ? "live" : $"scroll {state.ScrollOffset * 100 / maxScroll}%";
        segments.Add(new UiStatusSegment(
            Text: scrollText,
            Align: Alignment.Right,
            Importance: 0,
            Style: UiSpanStyle.Dim));

        return new UiStatusBarModel(Segments: segments.ToImmutable());
    }

    public static string ProjectFooter(UiState state)
    {
        var statusBar = ProjectStatusBar(state);
        var left = statusBar.Segments.Where(s => s.Align == Alignment.Left).OrderBy(s => s.Importance);
        var center = statusBar.Segments.Where(s => s.Align == Alignment.Center).OrderBy(s => s.Importance);
        var right = statusBar.Segments.Where(s => s.Align == Alignment.Right).OrderByDescending(s => s.Importance);

        return string.Join("  ", left.Select(s => s.Text))
               + (center.Any() ? "  " + string.Join("  ", center.Select(s => s.Text)) : "")
               + (right.Any() ? "  " + string.Join("  ", right.Select(s => s.Text)) : "");
    }
}
