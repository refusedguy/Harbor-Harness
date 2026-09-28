using System.Globalization;
using System.Collections.Immutable;
using System.Linq;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Projection;

public static class StatusProjector
{
    public static UiStatusBarModel ProjectStatusBar(UiState state)
    {
        var segments = ImmutableArray.CreateBuilder<UiStatusSegment>();

        segments.Add(new UiStatusSegment(
            Text: $"{state.Chat.Provider}/{state.Chat.Model}",
            Align: Alignment.Left,
            Importance: 1,
            Style: UiSpanStyle.Default));

        string glyph = state.Chat.Status switch
        {
            "running" => "▌",
            "compacting" => "◐",
            "error" => "✗",
            _ => "○"
        };
        UiSpanStyle statusStyle = state.Chat.Status switch
        {
            "running" => UiSpanStyle.Accent,
            "error" => UiSpanStyle.Danger,
            _ => UiSpanStyle.Default
        };
        segments.Add(new UiStatusSegment(
            Text: $"{glyph} {state.Chat.Status}",
            Align: Alignment.Center,
            Importance: 2,
            Style: statusStyle));

        if (!string.IsNullOrEmpty(state.Chat.AgentName))
        {
            segments.Add(new UiStatusSegment(
                Text: $"agent {state.Chat.AgentName}",
                Align: Alignment.Right,
                Importance: 3,
                Style: UiSpanStyle.Default));
        }

        if (state.Chat.Cost.TokensIn > 0 || state.Chat.Cost.TokensOut > 0)
        {
            segments.Add(new UiStatusSegment(
                Text: $"{state.Chat.Cost.TokensIn}↑ {state.Chat.Cost.TokensOut}↓",
                Align: Alignment.Right,
                Importance: 2,
                Style: UiSpanStyle.Dim));
        }

        segments.Add(new UiStatusSegment(
            Text: state.Chat.Cost.CostUsd.ToString("F4", CultureInfo.InvariantCulture),
            Align: Alignment.Right,
            Importance: 1,
            Style: UiSpanStyle.Dim));

        int maxScroll = Math.Max(0, state.Ui.TotalLines - Math.Max(1, state.Ui.ViewportLines));
        string scrollText = maxScroll == 0 ? "live" : $"scroll {state.Ui.ScrollOffset * 100 / maxScroll}%";
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
        var ordered = StatusSegmentOrdering.Ordered(statusBar.Segments);
        var left = ordered.Where(s => s.Align == Alignment.Left);
        var center = ordered.Where(s => s.Align == Alignment.Center);
        var right = ordered.Where(s => s.Align == Alignment.Right);

        return string.Join("  ", left.Select(s => s.Text))
               + (center.Any() ? "  " + string.Join("  ", center.Select(s => s.Text)) : "")
               + (right.Any() ? "  " + string.Join("  ", right.Select(s => s.Text)) : "");
    }
}
