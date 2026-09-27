using System.Text;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

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
        => StatusSegmentOrdering.Ordered(segments);
}