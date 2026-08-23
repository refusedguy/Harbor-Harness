using System.Text;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.TerminalGui.Views;

public sealed class StatusBarView
{
    public string Build(UiScreenModel screen)
    {
        var sb = new StringBuilder(128);

        foreach (var segment in OrderedSegments(screen.StatusBar.Segments))
        {
            sb.Append(segment.Text);
        }

        return sb.ToString();
    }

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