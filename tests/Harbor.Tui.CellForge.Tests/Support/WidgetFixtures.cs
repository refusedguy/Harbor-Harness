using System.Collections.Immutable;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Reads the CellForge widgets back into the shapes a test asserts on.
/// </summary>
/// <remarks>
///     <para>
///         A panel's content is a widget, and a widget is a string, a list of
///         rows, or something else entirely. The suites that assert on it each
///         wrote the same unwrapping switch, the same row reader and the same
///         message builders.
///     </para>
/// </remarks>
internal static class WidgetFixtures
{
    internal static IReadOnlyList<string> Rows(object? widget) => widget switch
    {
        null => Array.Empty<string>(),
        string s => s.Split('\n'),
        IReadOnlyList<string> rows => rows,
        IEnumerable<string> lines => lines.ToArray(),
        _ => new[] { widget.ToString() ?? string.Empty },
    };

    internal static string Joined(object? widget) => string.Join("\n", Rows(widget));

    internal static TabStripState Strip(int activeIndex, params SessionTab[] tabs) =>
        new()
        {
            Tabs = tabs.ToImmutableArray(),
            ActiveTabId = activeIndex >= 0 ? tabs[activeIndex].SessionId : null
        };

    internal static SessionTabStripPanel StripPanel(TabStripState strip)
    {
        var panel = new SessionTabStripPanel();
        panel.Strip = strip;
        return panel;
    }

    internal static SessionInfo Info(string id, string title) => new(
        SessionId.Create(id),
        title,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "active");

    internal static int Find(StatusSeg[] workspace, int count, string text)
    {
        for (int i = 0; i < count; i++)
        {
            if (workspace[i].Text == text)
            {
                return i;
            }
        }

        return -1;
    }

    internal static UserMessage UserMsg(string sessionId, string content) => new(
        Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow,
        content, "code", "test-model");

    internal static AssistantMessage AssistantMsg(string sessionId, params string[] texts) => new(
        Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow,
        [.. texts.Select(t => new TextPart(t))], StopReason.Stop, new Usage(0, 0), "test-model");
}
