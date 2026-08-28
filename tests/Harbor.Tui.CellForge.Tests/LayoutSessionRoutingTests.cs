using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;

namespace Harbor.Tui.CellForge.Tests;

public class ScreenSessionResizeTests
{
    private static (ScreenSession Session, RecordingBackend Backend, AnsiWriter Writer) Make(int cols = 40, int rows = 10, bool sync = true)
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, sync);
        return (new ScreenSession(writer, cols, rows), backend, writer);
    }

    [Test]
    public async Task SameSize_NoOp()
    {
        var (session, _, _) = Make();
        session.Resize(40, 10);
        await Assert.That(session.CurrentCols).IsEqualTo(40);
        await Assert.That(session.CurrentRows).IsEqualTo(10);
    }

    [Test]
    public async Task HorizontalShrink_EmitsEd2_BeforeFrameContent()
    {
        var (session, backend, writer) = Make();
        session.BeginFrame();
        session.Back.SetText(0, 0, "content", CellStyle.Plain);
        await session.FlushFrameAsync();
        backend.ResetForTests();

        session.Resize(20, 10);
        session.BeginFrame(); // ED2 goes here, inside sync wrapper
        session.Back.SetText(0, 0, "content", CellStyle.Plain);
        await session.FlushFrameAsync();

        string frame = backend.Escaped;
        int syncOn = frame.IndexOf("\\e[?2026h", StringComparison.Ordinal);
        int ed2 = frame.IndexOf("\\e[0m\\e[2J", StringComparison.Ordinal);
        int text = frame.IndexOf("content", StringComparison.Ordinal);
        await Assert.That(syncOn >= 0 && ed2 > syncOn && text > ed2).IsTrue();
    }

    [Test]
    public async Task Grow_DoesNotEmitEd2()
    {
        var (session, backend, writer) = Make(10, 5);
        session.Resize(30, 12);
        session.BeginFrame();
        await session.FlushFrameAsync();

        await Assert.That(backend.Escaped.Contains("\\e[2J")).IsFalse();
    }

    [Test]
    public async Task Shrink_RepaintsFully_FrontMatchesBack()
    {
        var (session, _, writer) = Make(40, 8);
        session.Back.FillAll(Cell.From(new Rune('#'), CellStyle.Plain));
        session.BeginFrame();
        await session.FlushFrameAsync();

        session.Resize(15, 8);
        session.Back.Fill(new Rect(0, 0, 15, 8), Cell.From(new Rune('+'), CellStyle.Plain));
        session.BeginFrame();
        await session.FlushFrameAsync();

        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
    }

    [Test]
    public async Task AutoResize_PicksUpSourceChange()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        int cols = 40, rows = 10;
        var session = new ScreenSession(writer, cols, rows, sizeSource: () => (cols, rows));

        cols = 55;
        session.CheckAutoSize();
        await Assert.That(session.CurrentCols).IsEqualTo(55);
    }
}

public class LayoutTreeTests
{
    private sealed class StubPanel(string id, int minW, int minH, int priority = 0) : Panel(id, new Size(minW, minH), priority)
    {
        public override void Paint(ScreenBuffer buffer) { }
    }

    [Test]
    public async Task Solve_HonorsRatio()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("left", 1, 1));
        tree.Split("left", SplitDir.Horizontal, 0.75f, new StubPanel("right", 1, 1));
        tree.Solve(100, 10);

        await Assert.That(tree.Panels.First(p => p.Id == "left").Rect.Width).IsEqualTo(74); // round(99*0.75)=74
        await Assert.That(tree.Panels.First(p => p.Id == "right").Rect.Width).IsEqualTo(25);
    }

    [Test]
    public async Task GapColumn_IsNotAssigned()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        tree.Split("a", SplitDir.Horizontal, 0.5f, new StubPanel("b", 1, 1), gap: 1);
        tree.Solve(11, 5);

        var a = tree.Panels.First(p => p.Id == "a").Rect;
        var b = tree.Panels.First(p => p.Id == "b").Rect;
        await Assert.That(a.Width + b.Width).IsEqualTo(10); // 1 col reserved as gap
        await Assert.That(b.X).IsEqualTo(a.Right + 1);
    }

    [Test]
    public async Task Minimums_ClampRatio()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 60, 1)); // min width 60
        tree.Split("a", SplitDir.Horizontal, 0.1f, new StubPanel("b", 10, 1));
        tree.Solve(100, 5);

        await Assert.That(tree.Panels.First(p => p.Id == "a").Rect.Width).IsEqualTo(60);
        await Assert.That(tree.Panels.First(p => p.Id == "b").Rect.Width).IsEqualTo(39);
    }

    [Test]
    public async Task Collapse_SacrificesLowerPriority()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("status", 80, 1, priority: int.MaxValue));
        tree.Split("status", SplitDir.Vertical, 0.9f, new StubPanel("chat", 1, 50));
        // Height 30 < 50 + 1 → chat collapses, status survives.
        tree.Solve(100, 30);

        await Assert.That(tree.Panels.First(p => p.Id == "chat").Rect.Height).IsEqualTo(0);
        await Assert.That(tree.Panels.First(p => p.Id == "status").Rect.Height).IsEqualTo(30);
    }

    [Test]
    public async Task CacheHit_ReplaysSameRects()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        tree.Split("a", SplitDir.Horizontal, 0.5f, new StubPanel("b", 1, 1));
        tree.Solve(80, 24);
        var first = tree.Panels.ToDictionary(p => p.Id, p => p.Rect);

        tree.Solve(80, 24); // cache hit
        foreach (var panel in tree.Panels)
        {
            await Assert.That(panel.Rect).IsEqualTo(first[panel.Id]);
        }
    }

    [Test]
    public async Task Mutation_BumpsCacheVersion()
    {
        var tree = new LayoutTree();
        tree.AddRoot(new StubPanel("a", 1, 1));
        tree.Solve(80, 24);
        tree.Remove("a");
        tree.AddRoot(new StubPanel("z", 1, 1));
        tree.Solve(80, 24);
        await Assert.That(tree.Panels.Single().Id).IsEqualTo("z");
    }
}

public class BorderPanelTests
{
    [Test]
    public async Task Paint_DrawsFrameAndTitle()
    {
        var buf = new ScreenBuffer(20, 6);
        var panel = new BorderPanel("p", 4, 2, title: "Chat") { Rect = new Rect(0, 0, 20, 6) };
        panel.Paint(buf);

        await Assert.That(buf.Get(0, 0).Rune).IsEqualTo('┌');
        await Assert.That(buf.Get(19, 0).Rune).IsEqualTo('┐');
        await Assert.That(buf.Get(0, 5).Rune).IsEqualTo('└');
        await Assert.That(buf.Get(19, 5).Rune).IsEqualTo('┘');
        await Assert.That(buf.Get(10, 0).Rune).IsEqualTo('─');
        await Assert.That(buf.Get(2, 0).Rune).IsEqualTo('C'); // title start
    }
}

public class FocusRouterTests
{
    private sealed class Target(string id) : IFocusTarget
    {
        public string Id { get; } = id;
        public bool Focused { get; private set; }
        public void OnFocusChanged(bool focused) => Focused = focused;
    }

    [Test]
    public async Task Tab_WrapsAround()
    {
        var router = new FocusRouter();
        var a = new Target("a");
        var b = new Target("b");
        router.Add(a);
        router.Add(b);
        _ = router.Jump(0);
        _ = router.Next();

        await Assert.That(router.Current!.Id).IsEqualTo("b");
        _ = router.Next();
        await Assert.That(router.Current!.Id).IsEqualTo("a"); // wrapped

        await Assert.That(a.Focused).IsTrue();
        await Assert.That(b.Focused).IsFalse();
    }

    [Test]
    public async Task ShiftTab_MovesBackward()
    {
        var router = new FocusRouter();
        router.Add(new Target("a"));
        router.Add(new Target("b"));
        _ = router.Previous();
        await Assert.That(router.Current!.Id).IsEqualTo("b"); // wraps to last
    }

    [Test]
    public async Task Jump_OutOfRange_ReturnsFalse()
    {
        var router = new FocusRouter();
        router.Add(new Target("only"));
        await Assert.That(router.Jump(5)).IsFalse();
        await Assert.That(router.FocusById("missing")).IsFalse();
    }

    [Test]
    public async Task FocusById_NotifiesBothSides()
    {
        var router = new FocusRouter();
        var a = new Target("a");
        var b = new Target("b");
        router.Add(a);
        router.Add(b);
        _ = router.FocusById("b");

        await Assert.That(a.Focused).IsFalse();
        await Assert.That(b.Focused).IsTrue();
    }
}

public class MouseRouterTests
{
    private sealed class Sink(string id) : IPointerTarget
    {
        public string Id { get; } = id;
        public List<string> Events { get; } = [];
        public void OnPress(int col, int row) => Events.Add($"press {col},{row}");
        public void OnRelease(int col, int row) => Events.Add($"release {col},{row}");
        public void OnWheel(int col, int row, int delta) => Events.Add($"wheel {col},{row} {delta}");
    }

    [Test]
    public async Task Press_DispatchesToLocalCoordinates()
    {
        var router = new MouseRouter();
        var sink = new Sink("panel");
        router.Bind(sink, new Rect(10, 5, 20, 8));

        router.Press(12, 7);
        await Assert.That(sink.Events).IsEquivalentTo(["press 2,2"]);
    }

    [Test]
    public async Task ReleaseOutsideScreen_ClampsBeforeHitTest()
    {
        var router = new MouseRouter(screenCols: 80, screenRows: 24);
        var sink = new Sink("panel");
        router.Bind(sink, new Rect(70, 0, 10, 8)); // panel hugging the right edge

        router.Release(500, -3); // SGR may report raw coords beyond the window
        await Assert.That(sink.Events).IsEquivalentTo(["release 9,0"]); // clamped to (79,0)
    }

    [Test]
    public async Task Miss_NoDispatch()
    {
        var router = new MouseRouter();
        var sink = new Sink("panel");
        router.Bind(sink, new Rect(10, 10, 5, 5));

        router.Press(0, 0);
        router.Wheel(20, 20, -3);
        await Assert.That(sink.Events).IsEmpty();
    }

    [Test]
    public async Task Wheel_PassesDelta()
    {
        var router = new MouseRouter();
        var sink = new Sink("panel");
        router.Bind(sink, new Rect(0, 0, 5, 5));
        router.Wheel(1, 1, 3);
        await Assert.That(sink.Events).IsEquivalentTo(["wheel 1,1 3"]);
    }
}
