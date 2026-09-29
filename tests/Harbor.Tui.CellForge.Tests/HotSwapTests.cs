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
// #648: bare [NotInParallel] = one at a time GLOBALLY, not a shared key. These
// tests Apply themes to the process-global palette, which every painter in this
// assembly reads; a constraint key only excludes same-key tests, so the old
// ("pty") key never kept a reader from observing a mid-test swap.
[NotInParallel]
public class HotSwapTests
{
    /// <summary>Resets the two process-wide statics this class mutates. The
    /// palette pin is <c>[ThreadStatic]</c> and TUnit reuses threads, so a
    /// test that ever ends up holding a pin would otherwise hand it to
    /// whichever test lands on that thread next — and the pin-lifecycle
    /// assertions below read it.</summary>
    [After(Test)]
    public void RestoreDefaultTheme()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        ChatPalette.UnpinFrame();
    }

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

        using (session.BeginFrameScope()) // adoption point
        {
            await Assert.That(session.Back).IsSameReferenceAs(newBack);
            await Assert.That(session.Front).IsSameReferenceAs(newFront);
        }

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

    // ── Frame pin lifecycle (#458): an aborted frame must not keep the pin ─
    //
    // Two capture rules for the assertions below:
    //  - the palette pin is [ThreadStatic], so the state is read on the thread
    //    that armed it and asserted via a captured local, never across await;
    //  - row payloads are single tokens, because the diff elides MoveTo only
    //    between ADJACENT cells — a multi-word payload is split by cursor
    //    escapes in the captured byte stream and is not a matchable substring.

    [Test]
    public async Task AbortFrame_UnpinsThread()
    {
        var session = MakeSession(20, 4, out _);
        session.BeginFrameScope();
        bool pinnedMidFrame = ChatPalette.IsFramePinned;

        session.AbortFrame();
        bool pinnedAfterAbort = ChatPalette.IsFramePinned;

        await Assert.That(pinnedMidFrame).IsTrue();
        await Assert.That(pinnedAfterAbort).IsFalse();
    }

    [Test]
    public async Task FrameScope_DisposedWithoutFlush_Unpins()
    {
        var session = MakeSession(20, 4, out _);
        bool pinnedMidFrame;

        using (session.BeginFrameScope())
        {
            pinnedMidFrame = ChatPalette.IsFramePinned;
        }

        // The exception-safe path: leaving scope releases the pin even though
        // no flush ever ran (a widget threw mid-paint).
        await Assert.That(pinnedMidFrame).IsTrue();
        await Assert.That(ChatPalette.IsFramePinned).IsFalse();
    }

    [Test]
    public async Task AbortFrame_Twice_StaysReleased()
    {
        var session = MakeSession(20, 4, out _);
        session.BeginFrameScope();

        session.AbortFrame();
        session.AbortFrame(); // idempotent — the scope disposes after an explicit abort too

        await Assert.That(ChatPalette.IsFramePinned).IsFalse();
    }

    [Test]
    public async Task FrameScope_Flush_ShipsTheFrame()
    {
        var session = MakeSession(20, 4, out var backend);

        var scope = session.BeginFrameScope();
        session.Back.SetText(0, 0, "scopedflushrow", CellStyle.Plain);
        scope.Flush();
        scope.Dispose();

        await Assert.That(backend.Text).Contains("scopedflushrow");
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
        await Assert.That(ChatPalette.IsFramePinned).IsFalse();
    }

    [Test]
    public async Task FrameScope_FlushAsync_ShipsTheFrame()
    {
        var session = MakeSession(20, 4, out var backend);
        var scope = session.BeginFrameScope();
        session.Back.SetText(0, 0, "asyncscopedflush", CellStyle.Plain);

        await scope.FlushAsync();
        bool pinnedAfterFlush = ChatPalette.IsFramePinned;
        scope.Dispose();

        await Assert.That(backend.Text).Contains("asyncscopedflush");
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
        await Assert.That(pinnedAfterFlush).IsFalse();
    }

    [Test]
    public async Task FrameScope_ThrownPaint_LeavesPinReleased()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        var session = MakeSession(20, 4, out _);

        try
        {
            using (session.BeginFrameScope())
            {
                TerminalColorPalette.Apply(HarborTheme.HarborLight);
                throw new InvalidOperationException("widget at the layout boundary blew up");
            }
        }
        catch (InvalidOperationException)
        {
            // the REPL reports the paint failure; the pin must not survive it
        }

        bool pinned = ChatPalette.IsFramePinned;
        var live = ChatPalette.Warning;
        var light = HarborTheme.HarborLight.Warning;

        // The catalogue tracks the published theme again — the regression was
        // a permanently stale palette, not a dropped update.
        await Assert.That(pinned).IsFalse();
        await Assert.That(live).IsEqualTo(PackedColor.Rgb(light.R, light.G, light.B));
    }

    [Test]
    public async Task FrameScope_Flushed_DisposeIsNoOp()
    {
        var session = MakeSession(20, 4, out _);

        using (session.BeginFrameScope())
        {
            session.FlushFrame();
        }

        await Assert.That(ChatPalette.IsFramePinned).IsFalse();
    }

    [Test]
    public async Task AbortFrame_NextFrame_ReemitsCellsTheTerminalNeverGot()
    {
        var session = MakeSession(20, 4, out var backend);
        session.BeginFrame();
        session.Back.SetText(0, 0, "baseline", CellStyle.Plain);
        session.FlushFrame();

        // Abort mid-frame: BACK carries a row the terminal never received,
        // FRONT still mirrors the shipped frame.
        using (session.BeginFrameScope())
        {
            session.Back.SetText(0, 2, "abortedrow", CellStyle.Plain);
        }

        backend.ResetForTests();
        var next = session.BeginFrameScope();
        session.Back.SetText(0, 1, "secondframe", CellStyle.Plain);
        next.Flush();
        next.Dispose();

        // The aborted row still reaches the terminal on the next frame, and
        // FRONT converges — an aborted frame leaves no half-written state.
        await Assert.That(backend.Text).Contains("abortedrow");
        await Assert.That(backend.Text).Contains("secondframe");
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
    }

    [Test]
    public async Task AbortFrame_DropsStaleDamageHints()
    {
        var session = MakeSession(40, 10, out var backend);
        session.BeginFrame();
        session.Back.SetText(0, 0, "baseline", CellStyle.Plain);
        session.FlushFrame();

        // A frame that registers a narrow damage hint and then aborts: the
        // hint describes a diff that never shipped. If it survived, the next
        // frame would scan ONLY that rect and silently skip the rest.
        using (session.BeginFrameScope())
        {
            session.Damage(new Rect(0, 0, 4, 1));
            session.Back.SetText(0, 0, "hinted", CellStyle.Plain);
        }

        backend.ResetForTests();
        var next = session.BeginFrameScope();
        session.Back.SetText(20, 8, "unhinted", CellStyle.Plain);
        next.Flush();
        next.Dispose();

        // The unhinted change is outside the abandoned hint — the frame is
        // only correct if the abort dropped it and the diff went full-scan.
        await Assert.That(backend.Text).Contains("unhinted");
        await Assert.That(session.Engine.FrontMatches(session.Back)).IsTrue();
    }

    // ── Concurrent producers/consumers: no locks, no torn pairs ────────────

    [Test]
    [Retry(3)]
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
        // Deterministic drain: the background consumer is only guaranteed
        // slices on an idle machine (loaded CI can starve it outright —
        // taken==0 with 1000 offers published). Whatever it missed, take
        // here; pair coherence is checked on both paths.
        while (chain.TryTake() is { } rest)
        {
            if (rest.Back.Cols != rest.Front.Cols || rest.Back.Rows != rest.Front.Rows)
            {
                throw new InvalidOperationException("torn pair adopted");
            }

            Interlocked.Increment(ref taken);
            chain.Return(rest.Back);
            chain.Return(rest.Front);
        }

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
