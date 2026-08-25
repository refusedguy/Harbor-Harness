using Harbor.Abstractions.Events;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Ui.Framework.State;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Streaming;

namespace Harbor.Tui.ConsoleEx.Tests;

public class CommitTickPacerTests
{
    private static QueueSnapshot Snap(int depth, double ageMs) =>
        new(depth, TimeSpan.FromMilliseconds(ageMs));

    [Test]
    public async Task LightQueue_StaysSmooth_Single()
    {
        var pacer = new CommitTickPacer();
        var plan = pacer.Decide(Snap(1, 5), nowMs: 100);
        await Assert.That(plan).IsEqualTo(DrainPlanKind.Single);
        await Assert.That(pacer.IsCatchUp).IsFalse();
    }

    [Test]
    public async Task DeepQueue_EntersCatchUp()
    {
        var pacer = new CommitTickPacer();
        var plan = pacer.Decide(Snap(8, 0), nowMs: 100);
        await Assert.That(plan).IsEqualTo(DrainPlanKind.Single); // decision tick itself is Single
        await Assert.That(pacer.IsCatchUp).IsTrue();

        var next = pacer.Decide(Snap(20, 10), nowMs: 110);
        await Assert.That(next).IsEqualTo(DrainPlanKind.BatchAll);
    }

    [Test]
    public async Task OldLine_TriggersEnter_EvenAtLowDepth()
    {
        var pacer = new CommitTickPacer();
        _ = pacer.Decide(Snap(2, 121), nowMs: 200);
        await Assert.That(pacer.IsCatchUp).IsTrue();
    }

    [Test]
    public async Task ExitHold_PreventsInstantExit()
    {
        var pacer = new CommitTickPacer();
        _ = pacer.Decide(Snap(8, 0), nowMs: 100); // enter

        // calm immediately but held < 250 ms → stays in CatchUp
        var early = pacer.Decide(Snap(1, 0), nowMs: 300);
        await Assert.That(pacer.IsCatchUp).IsTrue();
        await Assert.That(early).IsEqualTo(DrainPlanKind.BatchAll);

        var afterHold = pacer.Decide(Snap(1, 0), nowMs: 351);
        await Assert.That(afterHold).IsEqualTo(DrainPlanKind.BatchAll); // exit decision drains all once more
        await Assert.That(pacer.IsCatchUp).IsFalse();
    }

    [Test]
    public async Task ReenterHold_BlocksFlapping()
    {
        var pacer = new CommitTickPacer();
        _ = pacer.Decide(Snap(8, 0), nowMs: 0);   // enter at t=0
        _ = pacer.Decide(Snap(1, 0), nowMs: 260); // exit at t=260 (calm + hold passed)

        // pressure returns quickly (< 250 ms after exit) → must NOT re-enter
        _ = pacer.Decide(Snap(9, 0), nowMs: 400);
        await Assert.That(pacer.IsCatchUp).IsFalse();

        // after the re-entry hold it may enter again
        _ = pacer.Decide(Snap(9, 0), nowMs: 511);
        await Assert.That(pacer.IsCatchUp).IsTrue();
    }

    [Test]
    public async Task SevereAge_ForcesBatch_WithoutModeSwitch()
    {
        var pacer = new CommitTickPacer();
        var plan = pacer.Decide(Snap(2, 301), nowMs: 50);
        await Assert.That(plan).IsEqualTo(DrainPlanKind.BatchAll);
        await Assert.That(pacer.IsCatchUp).IsFalse(); // severe drain ≠ catch-up mode
    }
}

public class StreamBlockTests
{
    [Test]
    public async Task ShortStream_MaterializesImmediately_ByteExact()
    {
        var block = new StreamBlock();
        block.AppendDelta("Hello ");
        block.AppendDelta("World");

        // Below StreamingSync.ExactThresholdChars every delta flushes at once.
        await Assert.That(block.SyncedText).IsEqualTo("Hello World");
        await Assert.That(block.PendingLength).IsEqualTo(0);
    }

    [Test]
    public async Task LongStream_DefersMaterialization_UntilLagLimit()
    {
        var block = new StreamBlock();
        var chunk = new string('x', 400);
        for (int i = 0; i < 10; i++)
        {
            block.AppendDelta(chunk);
        }

        // synced=256+ triggers lag policy: pending stays below limit for a while.
        await Assert.That(block.SyncedText.Length >= StreamingSync.ExactThresholdChars).IsTrue();
        await Assert.That(block.PendingLength > 0 || block.SyncedText.Length == 4000).IsTrue();
    }

    [Test]
    public async Task Tick_RevealsOneLinePerTick_InSmoothMode()
    {
        var block = new StreamBlock(initialNowMs: 0);
        block.AppendDelta("l1\nl2\nl3\n");

        var first = block.Tick(nowMs: 10);
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0]).IsEqualTo("l1");

        var second = block.Tick(nowMs: 20);
        await Assert.That(second[0]).IsEqualTo("l2");
    }

    [Test]
    public async Task Tick_BatchesAll_AfterHysteresis()
    {
        var block = new StreamBlock(initialNowMs: 0);
        for (int i = 0; i < 12; i++)
        {
            block.AppendDelta($"line{i}\n");
        }

        _ = block.Tick(nowMs: 0);           // enters CatchUp on depth ≥ 8 (Single this tick)
        var drained = block.Tick(nowMs: 1); // BatchAll drains the rest
        await Assert.That(drained.Count).IsEqualTo(11);
        await Assert.That(block.LinesConsumed).IsEqualTo(12);
        await Assert.That(block.QueuedDepth).IsEqualTo(0);
    }

    [Test]
    public async Task Complete_RevealsEverythingIncludingUnterminatedTail()
    {
        var block = new StreamBlock(initialNowMs: 5);
        block.AppendDelta("alpha\nbeta gamma");
        block.Complete();

        var revealed = block.Tick(nowMs: 6);
        await Assert.That(revealed.Count).IsEqualTo(2);
        await Assert.That(revealed[1]).IsEqualTo("beta gamma");
        await Assert.That(block.PartialTail().IsEmpty).IsTrue();
    }

    [Test]
    public async Task PartialTail_TracksRevealedBoundary()
    {
        var block = new StreamBlock();
        block.AppendDelta("one\ntwo\nthree-part");
        _ = block.Tick(nowMs: 1);
        _ = block.Tick(nowMs: 2);

        var tail = block.PartialTail();
        await Assert.That(tail.ToString()).IsEqualTo("three-part");
    }

    [Test]
    public async Task DeltasAfterComplete_AreIgnored()
    {
        var block = new StreamBlock();
        block.AppendDelta("a");
        block.Complete();
        block.AppendDelta("b");
        await Assert.That(block.SyncedText).IsEqualTo("a");
    }
}

/// <summary>Minimal synchronous IEventBus stub for bridge tests.</summary>
internal sealed class FakeEventBus : IEventBus
{
    private Func<AgentEvent, CancellationToken, ValueTask>? _handler;

    public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
    {
        if (_handler is not null)
        {
            await _handler(@event, ct);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        _handler = handler;
        return new Disposer(() => _handler = null);
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : AgentEvent =>
        Subscribe((e, ct) => e is TEvent typed ? handler(typed, ct) : ValueTask.CompletedTask);

    public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => [];

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public class InlineAgentStreamBridgeTests
{
    private static (InlineAgentStreamBridge Bridge, RecordingBackend Backend, InlineSession Session, ComposerController Composer, FakeEventBus Bus) Make(bool sync = false)
    {
        var bus = new FakeEventBus();
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, sync);
        var session = new InlineSession(writer);
        var composer = new ComposerController();
        var bridge = new InlineAgentStreamBridge(bus, writer, session, composer);
        return (bridge, backend, session, composer, bus);
    }

    private static AgentEvent Update(TextDeltaEvent td) => new MessageUpdateEvent(td, null!);

    [Test]
    public async Task DeltaStream_RendersInLiveRegion_AndCommitsOnEnd()
    {
        var (bridge, backend, session, _, bus) = Make();

        await bus.PublishAsync(new MessageStartEvent(null!));
        await bus.PublishAsync(Update(new TextDeltaEvent("t1", "line-one\n")));
        bridge.Tick(nowMs: 1);
        _ = bridge.RenderLiveRegion();
        await bridge.FlushAsync();

        string live = backend.Escaped;
        await Assert.That(live.Contains("line-one")).IsTrue();
        await Assert.That(session.LiveLines >= 2).IsTrue(); // stream line + prompt row

        await bus.PublishAsync(new MessageEndEvent(null!));
        _ = bridge.RenderLiveRegion();
        await bridge.FlushAsync();

        // After end: content committed with trailing CR+LF, live region reset to prompt only.
        string after = backend.Escaped;
        await Assert.That(after.Contains("line-one\\r\\n")).IsTrue();
        await Assert.That(session.LiveLines).IsEqualTo(1);
    }

    [Test]
    public async Task ToolCall_CommitsDimHeader_AndClosesStream()
    {
        var (bridge, backend, _, _, bus) = Make();

        await bus.PublishAsync(new MessageStartEvent(null!));
        await bus.PublishAsync(Update(new TextDeltaEvent("t", "thinking out")));
        await bus.PublishAsync(new ToolExecutionStartEvent("tc1", "read", default));

        await bridge.FlushAsync();
        await Assert.That(backend.Escaped.Contains("\\e[2m⚙ read\\r\\n")).IsTrue();
    }

    [Test]
    public async Task AgentError_CommitsBoldLine()
    {
        var (bridge, backend, _, _, bus) = Make();
        await bus.PublishAsync(new AgentErrorEvent("boom"));
        await bridge.FlushAsync();

        await Assert.That(backend.Escaped.Contains("! boom")).IsTrue();
    }

    [Test]
    public async Task Prompt_SubmitPath_RemainsIndependentOfStream()
    {
        var (bridge, _, _, composer, _) = Make();
        _ = composer.Buffer.InsertText("user question");
        var action = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(action).IsEqualTo(ComposerAction.Submitted);
        // Caller owns the submit path: take the text, then the buffer is empty.
        string submitted = composer.Buffer.TakeText();
        await Assert.That(submitted).IsEqualTo("user question");
        await Assert.That(composer.Buffer.IsEmpty).IsTrue();
        bridge.Dispose();
    }
}
