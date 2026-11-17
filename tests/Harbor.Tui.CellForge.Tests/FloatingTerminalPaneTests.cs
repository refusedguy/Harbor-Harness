using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #1491: floating terminal pane attach/detach over the existing overlay
/// and input axes. Every case is headless — no PTY, no engine edits, no new seam.
/// Keys are scripted through <see cref="VirtualInputSource"/> and drained into
/// the controller with the same engine→rendering key conversion the REPL uses.
/// </summary>
public sealed class FloatingTerminalPaneTests
{
    private static ChatScreen BuildScreen()
    {
        var screen = ChatScreen.Build(
            new ComposerController(),
            new StatusViewModel { Model = "m" },
            includeSidebar: false);
        screen.Tree.Solve(80, 24);
        return screen;
    }

    /// <summary>Engine key → rendering key, mirroring ReplInputLoop.ToRenderingKey.</summary>
    private static KeyEvent ToRenderingKey(in Harbor.Tui.CellForge.Input.KeyEvent key) => new(
        (KeyCode)(int)key.Key,
        key.Character,
        (KeyModifiers)(int)key.Modifiers,
        (KeyEventType)(int)key.EventType,
        key.IsKittyEncoded,
        key.Codepoint);

    /// <summary>Drains a scripted source into the controller, as the REPL cascade would.</summary>
    private static void DrainTo(VirtualInputSource source, FloatingTerminalController controller)
    {
        source.DrainTo(evt =>
        {
            if (evt.Kind == EngineInput.InputEventKind.Key)
            {
                controller.FeedKey(ToRenderingKey(evt.Key));
            }
        });
    }

    [Test]
    public async Task Attach_CreatesSessionOnce_AndSeatsModalLayerOnStack()
    {
        var screen = BuildScreen();

        await Assert.That(screen.FloatingTerminal.Session).IsNull();
        screen.FloatingTerminal.Attach();
        FloatingTerminalSession session = screen.FloatingTerminal.Session!;
        await Assert.That(session).IsNotNull();
        await Assert.That(session.IsAlive).IsTrue();
        await Assert.That(screen.FloatingTerminal.IsAttached).IsTrue();

        // Re-attaching while attached is a no-op: the session is not recreated.
        await Assert.That(screen.FloatingTerminal.Attach()).IsFalse();
        await Assert.That(ReferenceEquals(session, screen.FloatingTerminal.Session)).IsTrue();

        screen.SyncOverlays(new Rect(0, 0, 80, 24));
        await Assert.That(screen.Tree.Overlays.Count).IsEqualTo(1);
        IOverlayLayer layer = screen.Tree.Overlays.Layers[0];
        await Assert.That(layer.Id).IsEqualTo(FloatingTerminalOverlayLayer.LayerId);
        await Assert.That(layer.Visible).IsTrue();
        await Assert.That(layer.Opaque).IsTrue();
        await Assert.That(layer.IsModal).IsTrue();
    }

    [Test]
    public async Task Detach_KeepsLiveSession_AndReturnsFocusToComposer()
    {
        var screen = BuildScreen();
        screen.FloatingTerminal.Attach();
        FloatingTerminalSession session = screen.FloatingTerminal.Session!;

        var source = new VirtualInputSource();
        source.SendText("git status");
        DrainTo(source, screen.FloatingTerminal);
        await Assert.That(session.CurrentInput).IsEqualTo("git status");

        await Assert.That(screen.FloatingTerminal.Detach()).IsTrue();
        await Assert.That(screen.FloatingTerminal.IsAttached).IsFalse();
        await Assert.That(ReferenceEquals(session, screen.FloatingTerminal.Session)).IsTrue();
        await Assert.That(session.IsAlive).IsTrue();
        await Assert.That(session.CurrentInput).IsEqualTo("git status");

        // Focus returned: keys are no longer consumed by the pane, so the host
        // cascade hands them to the composer instead.
        var afterDetach = new VirtualInputSource();
        afterDetach.SendText("typed into composer");
        await Assert.That(screen.FloatingTerminal.FeedKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        DrainTo(afterDetach, screen.FloatingTerminal);
        await Assert.That(session.CurrentInput).IsEqualTo("git status");

        screen.SyncOverlays(new Rect(0, 0, 80, 24));
        await Assert.That(screen.Tree.Overlays.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Reattach_ReturnsSameSession_WithHistoryIntact()
    {
        var screen = BuildScreen();
        screen.FloatingTerminal.Attach();
        FloatingTerminalSession session = screen.FloatingTerminal.Session!;

        session.AppendOutput("note: session still alive");
        int linesBefore = session.LineCount;

        screen.FloatingTerminal.Detach();
        screen.FloatingTerminal.Attach();

        await Assert.That(ReferenceEquals(session, screen.FloatingTerminal.Session)).IsTrue();
        await Assert.That(session.LineCount).IsEqualTo(linesBefore);
        await Assert.That(session.IsAlive).IsTrue();

        // The live session keeps taking input after the round trip.
        var source = new VirtualInputSource();
        source.SendText("ls");
        DrainTo(source, screen.FloatingTerminal);
        await Assert.That(session.CurrentInput).IsEqualTo("ls");
    }

    [Test]
    public async Task VirtualInput_WhileAttached_FeedsSession_AndCommitsOnEnter()
    {
        var controller = new FloatingTerminalController();
        controller.Attach();
        var source = new VirtualInputSource();
        source.SendText("pwd");
        source.SendSimple(EngineInput.KeyCode.Enter);
        DrainTo(source, controller);

        FloatingTerminalSession session = controller.Session!;
        await Assert.That(string.Join("\n", session.Lines)).Contains("pwd");
        await Assert.That(session.CurrentInput).IsEmpty();
        await Assert.That(session.LineCount).IsEqualTo(3); // prompt, "pwd", new prompt
    }

    [Test]
    public async Task Layer_PaintsFloatingBox_AndHidesWhenDetached()
    {
        var controller = new FloatingTerminalController();
        var layer = new FloatingTerminalOverlayLayer(controller);
        layer.Sync(new Rect(0, 0, 60, 20));

        await Assert.That(layer.Visible).IsFalse();
        var detached = TestRender.Buffer(60, 20);
        layer.Paint(detached, new Rect(0, 0, 60, 20));
        await Assert.That(TestRender.Art(detached)).DoesNotContain("Floating terminal");

        controller.Attach();
        controller.Session!.AppendOutput("hello from pane");
        await Assert.That(layer.Visible).IsTrue();
        var attached = TestRender.Buffer(60, 20);
        layer.Paint(attached, new Rect(0, 0, 60, 20));
        string art = TestRender.Art(attached);
        await Assert.That(art).Contains("Floating terminal");
        await Assert.That(art).Contains("hello from pane");
        await Assert.That(art).Contains("╭");
    }

    [Test]
    public async Task Close_KillsSession_SoNextAttachStartsFresh()
    {
        var controller = new FloatingTerminalController();
        controller.Attach();
        FloatingTerminalSession old = controller.Session!;
        controller.Session!.AppendOutput("old history");
        controller.Close();

        await Assert.That(old.IsAlive).IsFalse();
        await Assert.That(controller.Session).IsNull();
        await Assert.That(controller.IsAttached).IsFalse();

        controller.Attach();
        await Assert.That(ReferenceEquals(old, controller.Session)).IsFalse();
        await Assert.That(controller.Session!.LineCount).IsEqualTo(1); // fresh prompt only
    }
}
