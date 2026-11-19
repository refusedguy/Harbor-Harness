// TerminalTitleActivityTests.cs — issue #3102, atomic slice 1.
//
// TerminalTitleWriter already composed the OSC title and the activity
// indicator, but nothing under src/ or apps/ ever called it: the user never
// saw agent activity in the window title. The REPL event loop now projects
// run boundaries onto ScreenSession.SetTerminalTitle, and the bytes ride the
// next frame. This test drives that mapping with real events and reads the
// bytes a terminal would receive.

using Harbor.Abstractions.Events;
using Harbor.App.Cli.Repl;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;

namespace Harbor.App.Cli.Tests;

public class TerminalTitleActivityTests
{
    [Test]
    public async Task AgentStartEvent_WritesBusyTitleIntoNextFrame()
    {
        var backend = new RecordingBackend();
        var session = new ScreenSession(new AnsiWriter(backend, syncUpdates: true), 80, 24);

        ReplLifecycle.SetAgentTitle(session, new AgentStartEvent("session-1", []));
        session.BeginFrame();
        session.FlushFrame();

        string written = backend.Text;
        await Assert.That(written).Contains("\u001B]0;");
        await Assert.That(written).Contains("● busy Harbor");
        await Assert.That(written).Contains("\u0007");

        // The title is staged once: a following frame with no new activity
        // must not repeat the same OSC sequence.
        backend.Reset();
        session.BeginFrame();
        session.FlushFrame();
        await Assert.That(backend.Text).DoesNotContain("busy Harbor");

        ReplLifecycle.SetAgentTitle(session, new AgentEndEvent([]));
        session.BeginFrame();
        session.FlushFrame();
        await Assert.That(backend.Text).Contains("✓ done Harbor");
    }
}
