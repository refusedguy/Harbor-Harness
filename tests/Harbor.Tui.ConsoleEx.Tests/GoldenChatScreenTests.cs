using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Full chat-screen golden (CE-3 W2.4): timeline feed above, live composer
/// below, status footer at the bottom — one LayoutTree, one frame pipeline.
/// </summary>
public class GoldenChatScreenTests
{
    [Test]
    public async Task ChatScreen_ThreeZones_Golden()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new ScreenSession(writer, 64, 14);

        var composer = new ComposerController();
        composer.Buffer.InsertText("fix the bug in |");
        var status = new StatusViewModel
        {
            Model = "kilocode/hy3",
            Mode = StatusBarMode.Running,
        };
        status.SetContext(4300, 10_000);
        status.SetUsage(12_400, 5_200, 0.0021m);

        var screen = ChatScreen.Build(composer, status);
        var tl = screen.Timeline.Timeline;
        tl.Append(new UserBlock("please fix the parser"));
        var stream = new StreamingMarkdownBlock();
        stream.Push("Looking **at** it.\n- step one\n");
        tl.Append(stream);

        screen.Tree.Solve(session.CurrentCols, session.CurrentRows);
        _ = tl.PrepareFrame(64, screen.Timeline.Rect.Height);

        session.BeginFrame();
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(session.Back);
        }

        await session.FlushFrameAsync();

        string doc = GoldenDoc.Build("ce3-chat-screen", session.Back, backend);
        string expected = Golden.Verify("ce3-chat-screen", doc, GridDump.ToSvg(session.Back));
        await Assert.That(doc).IsEqualTo(expected);
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();

        // Zone sanity: prompt text on a lower row, model id on the bottom row.
        string art = GridDump.Art(session.Back);
        var rows = art.Split('\n');
        await Assert.That(rows.Any(r => r.Contains("fix the bug"))).IsTrue();          // composer zone
        await Assert.That(rows.Any(r => r.StartsWith('⠙') || r.StartsWith('⠸'))).IsTrue(); // spinner glyph
        await Assert.That(rows[^2].Contains("kilocode/hy3")).IsTrue();                 // status row
    }
}
