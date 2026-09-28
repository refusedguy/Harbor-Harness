using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #468 — the sync/async backend contract split. An async-only
/// <see cref="ITerminalBackend"/> used to compile against the sync flush paths
/// (<c>CellForgeRenderContext.Flush</c> → <c>AnsiWriter.FlushSync</c>,
/// <c>ScreenSession.FlushFrame</c> → <c>AnsiWriter.EndFrame</c>) and then threw
/// <see cref="NotSupportedException"/> at paint time. The split makes the sync
/// sinks require <see cref="ISyncTerminalBackend"/> and turns the residual
/// runtime case into a named, actionable failure — one that never corrupts
/// FRONT.
/// </summary>
public class SyncBackendCapabilityTests
{
    // ScreenSession.BeginFrame arms the thread-static frame palette pin. The
    // tests below deliberately throw out of the flush, and the runner may
    // schedule them onto a pooled thread — never hand a pinned render thread
    // to the next test.
    [After(Test)]
    public void UnpinFramePalette() => ChatPalette.UnpinFrame();

    // ── Contract shape: the sync member lives on the derived interface only ──

    [Test]
    public async Task ITerminalBackend_ExposesNoSyncWrite()
    {
        var members = typeof(ITerminalBackend)
            .GetMethods()
            .Where(m => m.Name == nameof(ISyncTerminalBackend.Write))
            .ToList();

        await Assert.That(members).IsEmpty();
    }

    [Test]
    public async Task ISyncTerminalBackend_Extends_ITerminalBackend()
    {
        await Assert.That(typeof(ITerminalBackend).IsAssignableFrom(typeof(ISyncTerminalBackend))).IsTrue();
    }

    // ── AnsiWriter capability flag ─────────────────────────────────────────

    [Test]
    public async Task AsyncOnlyBackend_WriterReportsNoSyncSupport()
    {
        var writer = new AnsiWriter(new AsyncOnlyBackend());

        await Assert.That(writer.SupportsSyncWrites).IsFalse();
    }

    [Test]
    public async Task SyncBackend_WriterReportsSyncSupport()
    {
        var writer = new AnsiWriter(new RecordingBackend());

        await Assert.That(writer.SupportsSyncWrites).IsTrue();
    }

    [Test]
    public async Task SyncBackend_PropagatesAccessor()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);

        await Assert.That(writer.SyncBackend).IsSameReferenceAs(backend);
        await Assert.That(writer.Backend).IsSameReferenceAs(backend);
    }

    [Test]
    public async Task AsyncOnlyBackend_SyncBackendAccessor_ThrowsWithBackendName()
    {
        var writer = new AnsiWriter(new AsyncOnlyBackend());

        var ex = Assert.Throws<InvalidOperationException>(() => { _ = writer.SyncBackend; });

        await Assert.That(ex.Message).Contains(nameof(AsyncOnlyBackend));
    }

    // ── AnsiWriter sync flush on an async-only backend ─────────────────────

    [Test]
    public async Task EndFrame_AsyncOnlyBackend_ThrowsInvalidOperation()
    {
        var writer = new AnsiWriter(new AsyncOnlyBackend());
        writer.BeginFrame();
        writer.WriteText("hi");

        var ex = Assert.Throws<InvalidOperationException>(writer.EndFrame);

        await Assert.That(ex.Message).Contains(nameof(AsyncOnlyBackend));
    }

    [Test]
    public async Task FlushSync_AsyncOnlyBackend_ThrowsInvalidOperation()
    {
        var writer = new AnsiWriter(new AsyncOnlyBackend());
        writer.WriteText("hi");

        Assert.Throws<InvalidOperationException>(writer.FlushSync);
    }

    [Test]
    public async Task EndFrameAsync_AsyncOnlyBackend_StillWorks()
    {
        var backend = new AsyncOnlyBackend();
        var writer = new AnsiWriter(backend);
        writer.BeginFrame();
        writer.WriteText("hi");
        await writer.EndFrameAsync();

        await Assert.That(backend.Writes).IsEqualTo(1);
    }

    [Test]
    public async Task EndFrame_Throws_LeavesBufferShippableViaAsync()
    {
        // The guard resolves the sink before appending the sync-off wrapper, so
        // a caller that catches the capability error can fall back to
        // EndFrameAsync and ship the frame instead of losing it.
        var backend = new AsyncOnlyBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        writer.BeginFrame();
        writer.WriteText("hi");

        Assert.Throws<InvalidOperationException>(writer.EndFrame);
        await writer.EndFrameAsync();

        await Assert.That(backend.Writes).IsEqualTo(1);
    }

    [Test]
    public async Task EndFrame_EmptyFrame_AsyncOnlyBackend_DoesNotTouchTheBackend()
    {
        // The empty-frame short-circuit runs BEFORE the backend is reached, so
        // a frame that carried no content must stay a no-op instead of
        // tripping the capability guard — otherwise an async-only session that
        // legitimately never painted would start throwing on idle flushes.
        var writer = new AnsiWriter(new AsyncOnlyBackend());
        writer.BeginFrame();

        writer.EndFrame();

        await Assert.That(writer.SupportsSyncWrites).IsFalse();
    }

    [Test]
    public async Task EndFrame_SyncWrapperOnly_AsyncOnlyBackend_DoesNotTouchTheBackend()
    {
        // Same short-circuit with the synchronized-output wrapper armed: the
        // wrapper bytes alone are not content, so this must not throw either.
        var writer = new AnsiWriter(new AsyncOnlyBackend(), syncUpdates: true);
        writer.BeginFrame();

        writer.EndFrame();

        await Assert.That(writer.TrackedX).IsEqualTo(-1);
    }

    // ── ScreenSession.FlushFrame guard ─────────────────────────────────────

    [Test]
    public async Task ScreenSession_AsyncOnlyBackend_ReportsNoSyncFlush()
    {
        var session = new ScreenSession(new AnsiWriter(new AsyncOnlyBackend()), 20, 5);

        await Assert.That(session.SupportsSyncFlush).IsFalse();
    }

    [Test]
    public async Task ScreenSession_SyncBackend_ReportsSyncFlush()
    {
        var session = new ScreenSession(new AnsiWriter(new RecordingBackend()), 20, 5);

        await Assert.That(session.SupportsSyncFlush).IsTrue();
    }

    [Test]
    public async Task FlushFrame_AsyncOnlyBackend_ThrowsInvalidOperation()
    {
        var session = new ScreenSession(new AnsiWriter(new AsyncOnlyBackend()), 20, 5);
        session.Back.SetText(0, 0, "painted", CellStyle.Plain);
        session.BeginFrame();

        Assert.Throws<InvalidOperationException>(session.FlushFrame);
    }

    [Test]
    public async Task FlushFrame_AsyncOnlyBackend_DoesNotAdvanceFront()
    {
        // The regression that made the old default method dangerous: the diff
        // ran and copied BACK into FRONT, THEN the write threw — so the next
        // frame diffed against terminal state that never reached the tty and
        // the cell was never repainted. The guard must fire before the diff.
        var session = new ScreenSession(new AnsiWriter(new AsyncOnlyBackend()), 20, 5);
        session.Back.SetText(0, 0, "painted", CellStyle.Plain);
        session.BeginFrame();

        try
        {
            session.FlushFrame();
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        // Cell.Rune is the raw codepoint (int), and a never-painted FRONT cell
        // is Cell.Blank (a space) — not 'p'.
        var cell = session.Front.Get(0, 0);
        await Assert.That(cell.Rune).IsNotEqualTo((int)'p');
    }

    [Test]
    public async Task FlushFrame_AsyncOnlyBackend_SaysUseFlushFrameAsync()
    {
        var session = new ScreenSession(new AnsiWriter(new AsyncOnlyBackend()), 20, 5);
        session.BeginFrame();

        var ex = Assert.Throws<InvalidOperationException>(session.FlushFrame);

        await Assert.That(ex.Message).Contains("FlushFrameAsync");
    }

    [Test]
    public async Task FlushFrame_AsyncOnlyBackend_ReleasesTheFramePin()
    {
        // The guard throws from INSIDE the try, so the frame teardown (#458)
        // must still run: the palette pin is released. A leaked pin would
        // wedge every later frame on this render thread onto a stale color
        // catalog.
        var session = new ScreenSession(new AnsiWriter(new AsyncOnlyBackend()), 20, 5);
        session.BeginFrame();
        await Assert.That(ChatPalette.IsFramePinned).IsTrue();

        Assert.Throws<InvalidOperationException>(session.FlushFrame);

        await Assert.That(ChatPalette.IsFramePinned).IsFalse();
    }

    [Test]
    public async Task FlushFrame_AsyncOnlyBackend_NextFrameRepaintsWhatNeverShipped()
    {
        // Grids are invalidated by the teardown, not advanced: the next frame
        // is a clean full repaint rather than a diff against terminal state
        // that never existed.
        var session = new ScreenSession(new AnsiWriter(new AsyncOnlyBackend()), 20, 5);
        session.Back.SetText(0, 0, "painted", CellStyle.Plain);
        session.BeginFrame();

        Assert.Throws<InvalidOperationException>(session.FlushFrame);
        session.AbortFrame(); // must be a no-op — the frame already closed

        session.Back.SetText(0, 0, "painted", CellStyle.Plain);
        session.BeginFrame();
        await session.FlushFrameAsync();

        await Assert.That(session.Front.Get(0, 0).Rune).IsEqualTo((int)'p');
    }

    [Test]
    public async Task FlushFrameAsync_AsyncOnlyBackend_ShipsTheFrame()
    {
        var backend = new AsyncOnlyBackend();
        var session = new ScreenSession(new AnsiWriter(backend), 20, 5);
        session.Back.SetText(0, 0, "painted", CellStyle.Plain);
        session.BeginFrame();
        await session.FlushFrameAsync();

        await Assert.That(backend.Writes).IsEqualTo(1);
        await Assert.That(session.Front.Get(0, 0).Rune).IsEqualTo((int)'p');
    }

    // ── StdoutBackend: the production backend serves both contracts ────────

    [Test]
    public async Task StdoutBackend_IsSyncCapable()
    {
        await Assert.That(typeof(ISyncTerminalBackend).IsAssignableFrom(typeof(StdoutBackend))).IsTrue();
    }

    [Test]
    public async Task StdoutBackend_WriterReportsSyncSupport()
    {
        await Assert.That(new AnsiWriter(new StdoutBackend()).SupportsSyncWrites).IsTrue();
    }

    // ── The sync render context rejects async-only backends at compile time ──

    [Test]
    public async Task CellForgeRenderContext_RequiresSyncBackend()
    {
        // The ctor parameter type IS the fix: an async-only backend cannot be
        // passed, so ITuiRenderContext.Flush can no longer explode at runtime.
        var ctor = typeof(CellForgeRenderContext)
            .GetConstructor([typeof(ISyncTerminalBackend)]);

        await Assert.That(ctor).IsNotNull();
    }

    [Test]
    public async Task CellForgeRenderContext_HasNoAsyncOnlyCtor()
    {
        var ctor = typeof(CellForgeRenderContext)
            .GetConstructor([typeof(ITerminalBackend)]);

        await Assert.That(ctor).IsNull();
    }

    [Test]
    public async Task CellForgeRenderContext_FlushesThroughSyncBackend()
    {
        var backend = new RecordingBackend();
        var context = new CellForgeRenderContext(backend);

        context.WriteLine("hello");

        await Assert.That(backend.Text).Contains("hello");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────

    /// <summary>Implements the async contract only — the shape that used to
    /// compile against the sync sinks and throw at paint time.</summary>
    private sealed class AsyncOnlyBackend : ITerminalBackend
    {
        public int Writes { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
    }
}
