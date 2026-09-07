using Harbor.Ui.Framework.State;
using System.Collections.Immutable;
using System.Globalization;
namespace Harbor.Ui.Framework.Projection;

public static class StatusProjector
{
    public static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        var segments = ImmutableArray.CreateBuilder<UiStatusSegment>();

        segments.Add(new UiStatusSegment(
            $"{state.Provider}/{state.Model}",
            Alignment.Left,
            1,
            UiSpanStyle.Default));

        string glyph = state.Status switch
        {
            "running" => "▌",
            "compacting" => "◐",
            "error" => "✗",
            _ => "○"
        };
        var statusStyle = state.Status switch
        {
            "running" => UiSpanStyle.Accent,
            "error" => UiSpanStyle.Danger,
            _ => UiSpanStyle.Default
        };
        segments.Add(new UiStatusSegment(
            $"{glyph} {state.Status}",
            Alignment.Center,
            2,
            statusStyle));

        if (!string.IsNullOrEmpty(state.AgentName))
        {
            segments.Add(new UiStatusSegment(
                $"agent {state.AgentName}",
                Alignment.Right,
                3,
                UiSpanStyle.Default));
        }

        if (state.Cost.TokensIn > 0 || state.Cost.TokensOut > 0)
        {
            segments.Add(new UiStatusSegment(
                $"{state.Cost.TokensIn}↑ {state.Cost.TokensOut}↓",
                Alignment.Right,
                2,
                UiSpanStyle.Dim));
        }

        segments.Add(new UiStatusSegment(
            state.Cost.CostUsd.ToString("F4", CultureInfo.InvariantCulture),
            Alignment.Right,
            1,
            UiSpanStyle.Dim));

        int maxScroll = Math.Max(0, state.TotalLines - Math.Max(1, state.ViewportLines));
        string scrollText = maxScroll == 0 ? "live" : $"scroll {state.ScrollOffset * 100 / maxScroll}%";
        segments.Add(new UiStatusSegment(
            scrollText,
            Alignment.Right,
            0,
            UiSpanStyle.Dim));

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
