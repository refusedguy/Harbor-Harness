using System.Text;
using Harbor.Tui.Termina.Rendering;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using TerminaColor = Termina.Terminal.Color;

namespace Harbor.Tui.Termina.Views;

public sealed class StatusBarView
{
    /// <summary>Ordered (left→center→right) status segments with their styles.</summary>
    public IReadOnlyList<(string Text, UiSpanStyle Style)> BuildSegments(UiScreenModel screen)
        => OrderedSegments(screen.StatusBar.Segments)
            .Select(s => (s.Text, s.Style ?? UiSpanStyle.Default))
            .ToArray();

    public string Build(UiScreenModel screen)
    {
        var sb = new StringBuilder(128);
        sb.Append(TerminaMarkdownRenderer.Ansi(TerminaColor.DarkGray, " "));

        foreach (var segment in OrderedSegments(screen.StatusBar.Segments))
        {
            var color = MapColor(segment.Style);
            sb.Append(TerminaMarkdownRenderer.Ansi(color, segment.Text));
        }

        return sb.ToString();
    }

    /// <summary>Maps a projection span style to a native Termina color.</summary>
    public static TerminaColor MapColor(UiSpanStyle? style) => style switch
    {
        UiSpanStyle.Accent => TerminaColor.Cyan,
        UiSpanStyle.Dim => TerminaColor.DarkGray,
        UiSpanStyle.Danger => TerminaColor.Red,
        _ => TerminaColor.White
    };

    private static IReadOnlyList<UiStatusSegment> OrderedSegments(IReadOnlyList<UiStatusSegment> segments)
    {
        var left = segments.Where(s => s.Align == Alignment.Left).OrderBy(s => s.Importance).ToList();
        var center = segments.Where(s => s.Align == Alignment.Center).OrderBy(s => s.Importance).ToList();
        var right = segments.Where(s => s.Align == Alignment.Right).OrderByDescending(s => s.Importance).ToList();

        var result = new List<UiStatusSegment>();
        result.AddRange(left);
        result.AddRange(center);
        result.AddRange(right);
        return result;
    }
}