using System.Collections.Concurrent;
using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Lock-free hot-swap runtime (renderer-moat T2): ScreenBuffer backends swap
/// under a running render loop via an atomic double-buffer handoff — no locks,
/// no torn frames. Theme swaps publish a new palette catalog mid-stream while
/// the pinned frame snapshot keeps every painted cell on one coherent palette.
/// </summary>
[NotInParallel]
public class HotSwapTests
{
    [After(Test)]
    public void RestoreDefaultTheme() => TerminalColorPalette.Apply(HarborTheme.HarborDark);

    // ── BufferSwapChain: pool + offer slot ─────────────────────────────────

    [Test]
    public async Task Rent_AfterReturn_ReusesPooledInstance()
    {
        var chain = new BufferSwapChain();
        var first = chain.Rent(40, 12);
        chain.Return(first);
        var second = chain.Rent(30, 10);

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(second.Cols).IsEqualTo(30);
        await Assert.That(second.Rows).IsEqualTo(10);
    }

    [Test]
    public async Task Rent_EmptyPool_AllocatesFresh()
    {
        var chain = new BufferSwapChain();
        var a = chain.Rent(10, 5);
        var b = chain.Rent(10, 5);

        await Assert.That(a).IsNotSameReferenceAs(b);
        await Assert.That(a.Cols).IsEqualTo(10);
    }

    [Test]
    public async Task Publish_Take_ReturnsSamePair_Once()
    {
        var chain = new BufferSwapChain();
        var back = new ScreenBuffer(20, 6);
        var front = new ScreenBuffer(20, 6);
        var offer = new BufferPair(back, front);

        chain.Publish(offer);
        var taken = chain.TryTake();

        await Assert.That(taken).IsSameReferenceAs(offer);
        await Assert.That(taken!.Back).IsSameReferenceAs(back);
        await Assert.That(taken!.Front).IsSameReferenceAs(front);
        await Assert.That(chain.TryTake()).IsNull(); // slot cleared — no double take
    }

    [Test]
    public async Task Publish_LastWriterWins()
    {
        var chain = new BufferSwapChain();
        var first = new BufferPair(new ScreenBuffer(10, 4), new ScreenBuffer(10, 4));
        var second = new BufferPair(new ScreenBuffer(12, 5), new ScreenBuffer(12, 5));

        chain.Publish(first);
        chain.Publish(second); // displaces the pending first offer

        var taken = chain.TryTake();
        await Assert.That(taken).IsSameReferenceAs(second);
    }

    // ── ScreenSession: frame-boundary adoption ─────────────────────────────

    [Test]
    public async Task OfferSwap_SameGeometry_AdoptsAtNextFrameBoundary()
    {
        var session = MakeSession(40, 12, out var backend);
        PaintIdleFrame(session);

        var chain = session.SwapChain;
        var newBack = chain.Rent(40, 12);
        var newFront = chain.Rent(40, 12);
        session.OfferSwap(newBack, newFront);

        session.BeginFrame(); // adoption point

        await Assert.That(session.Back).IsSameReferenceAs(newBack);
        await Assert.That(session.Front).IsSameReferenceAs(newFront);

        // Both grids invalidated → next flush is a clean full repaint; the
        // retired pair is back in the pool for the next renter.
        PaintIdleFrame(session);
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();

        var recycled = chain.Rent(40, 12);
        await Assert.That(recycled.Cols).IsEqualTo(40);
    }

    [Test]
    public async Task OfferSwap_Resize_AppliesGeometry_AndHorizontalShrinkErase()
    {
        var session = MakeSession(40, 12, out var backend);
        PaintIdleFrame(session);
        backend.ResetForTests();

        var chain = session.SwapChain;
        session.OfferSwap(chain.Rent(30, 10), chain.Rent(30, 10));
        session.BeginFrame();
        session.FlushFrame();

        await Assert.That(session.CurrentCols).IsEqualTo(30);
        await Assert.That(session.CurrentRows).IsEqualTo(10);
        // Horizontal shrink ⇒ Erase-in-display 2 before the frame (resize policy).
        await Assert.That(backend.Text.Contains("\x1B[2J")).IsTrue();
    }

    [Test]
    public async Task OfferSwap_MismatchedPairGeometry_Rejected()
    {
        var session = MakeSession(40, 12, out _);
        var chain = session.SwapChain;

        await Assert.That(() => session.OfferSwap(new ScreenBuffer(30, 10), new ScreenBuffer(32, 10)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(chain.TryTake()).IsNull(); // nothing published
    }

    [Test]
    public async Task Engine_SwapFront_TerminalMirrorFollows()
    {
        var engine = new DiffEngine(10, 2);
        var replacement = new ScreenBuffer(10, 2);
        replacement.SetText(0, 0, "swapped", CellStyle.Plain);

        engine.SwapFront(replacement);
        var writer = new AnsiWriter(new RecordingBackend());
        writer.BeginFrame();
        engine.Flush(new ScreenBuffer(10, 2), writer); // blank BACK vs new FRONT — pure mirror swap, no emission

        await Assert.That(engine.Front).IsSameReferenceAs(replacement);
        await Assert.That(engine.FrontMatches(replacement)).IsTrue();
    }

    // ── Theme swap mid-stream: pinned frame, no torn cells ─────────────────

    [Test]
    public async Task ThemeSwap_MidFrame_DoesNotTearPinnedPaint()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        var pinnedDark = ChatPalette.Warning; // dark: #FFB454

        var session = MakeSession(20, 4, out _);
        session.BeginFrame(); // pins the dark catalog for this frame

        TerminalColorPalette.Apply(HarborTheme.HarborLight); // publish mid-frame

        session.Back.SetText(0, 1, "warn", new CellStyle(ChatPalette.Warning, attrs: StyleAttr.Bold));
        var midFrameWarning = ChatPalette.Warning;
        session.FlushFrame();
        ChatPalette.UnpinFrame();

        await Assert.That(midFrameWarning).IsEqualTo(pinnedDark); // frame stayed coherent

        session.BeginFrame(); // next frame adopts the published light catalog
        var nextFrameWarning = ChatPalette.Warning;
        session.FlushFrame();
        ChatPalette.UnpinFrame();

        var lightWarning = HarborTheme.HarborLight.Warning;
        await Assert.That(nextFrameWarning).IsEqualTo(PackedColor.Rgb(lightWarning.R, lightWarning.G, lightWarning.B));
    }

    // ── Concurrent producers/consumers: no locks, no torn pairs ────────────

    [Test]
    public async Task SwapChain_ConcurrentPublishTake_NeverTearsPairs()
    {
        // Distinct geometry per producer: a torn handoff (back from one offer,
        // front from another) would surface as mismatched pair dimensions.
        const int producers = 4;
        const int offersPerProducer = 250;
        const int totalOffers = producers * offersPerProducer;
        var chain = new BufferSwapChain();
        int taken = 0;
        long published = 0;
        var drained = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();

        var consumer = Task.Run(() =>
        {
            try
            {
                while (!drained.IsSet)
                {
                    if (chain.TryTake() is { } offer)
                    {
                        // Pair coherence: the two grids travel as one unit.
                        if (offer.Back.Cols != offer.Front.Cols || offer.Back.Rows != offer.Front.Rows)
                        {
                            throw new InvalidOperationException("torn pair adopted");
                        }

                        Interlocked.Increment(ref taken);
                        chain.Return(offer.Back);
                        chain.Return(offer.Front);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        var producerTasks = Enumerable.Range(0, producers)
            .Select(p => Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < offersPerProducer; i++)
                    {
                        int cols = 40 + p; // per-producer geometry tag
                        chain.Publish(new BufferPair(new ScreenBuffer(cols, 20), new ScreenBuffer(cols, 20)));
                        Interlocked.Increment(ref published);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }))
            .ToArray();

        await Task.WhenAll(producerTasks);
        drained.Set();
        await consumer;

        await Assert.That(errors.IsEmpty).IsTrue();
        await Assert.That(Volatile.Read(ref taken)).IsGreaterThan(0); // consumer made progress — no lock starvation
        await Assert.That(Volatile.Read(ref published)).IsEqualTo(totalOffers);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ScreenSession MakeSession(int cols, int rows, out RecordingBackend backend)
    {
        backend = new RecordingBackend();
        return new ScreenSession(new AnsiWriter(backend, syncUpdates: false), cols, rows);
    }

    private static void PaintIdleFrame(ScreenSession session)
    {
        session.BeginFrame();
        session.FlushFrame();
    }
}
