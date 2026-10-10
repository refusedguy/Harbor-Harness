using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Dirty-rect invalidation for the streaming tail (#465).
///
/// <c>StreamCoalescer.DrainPaced</c> ran on every paced tick and reached
/// <see cref="VirtualizedChatTimeline.MarkLastDirty"/>, which set the
/// viewport-wide flag unconditionally. The host's damage-hint bridge
/// (<c>ReplLifecycle.ApplyFrameDamageHints</c>) then returned empty hints and the
/// engine fell back to the fused full scan (docs/BENCHMARKS.md: 0.317 ms hinted
/// vs 0.717 ms full on a 120×500 grid) on every frame of every stream — while
/// the only rows that can differ are the tail block's own rows plus whatever
/// reflows below them.
///
/// Three halves of the contract are pinned here:
/// <list type="number">
///   <item><description>the streaming frame takes the HINTED path —
///     <c>ConsumeFrameDamage</c> reports narrow rects, so the host emits hints
///     instead of an empty hint list;</description></item>
///   <item><description>the hinted output stays BYTE-IDENTICAL to the full scan
///     over scripted streaming traffic (twin sessions, golden contract);</description></item>
///   <item><description>frames that genuinely moved keep the viewport-wide flag —
///     a pinned tail re-homes every visible row, and narrowing THAT would be a
///     ghost farm.</description></item>
/// </list>
/// </summary>
public class TimelineDirtyRectTests
{
    private const int Cols = 120;
    private const int Rows = 40;

    /// <summary>What one frame's damage policy decided. <see cref="HasDirtyRect"/>
    /// is captured at consume time — the ledger is cleared there.</summary>
    private readonly record struct FrameOutcome(bool FullScan, int FxCount, bool HasDirtyRect);

    /// <summary>
    /// Minimal stand-in for the host frame: layout, paint, damage policy, flush —
    /// the same order as <c>CellForgeReplRunner.RenderFrameAsync</c> +
    /// <c>ApplyFrameDamageHints</c>. <paramref name="fx"/> selects the hinted
    /// damage policy; the reference session drops the ledger and full-scans.
    /// </summary>
    private sealed class Session
    {
        public readonly ScreenSession Screen;
        public readonly ChatScreen Chat;
        public readonly RecordingBackend Backend;
        public readonly Rect[] Fx = new Rect[VirtualizedChatTimeline.MaxFxDamage];

        private Session(ScreenSession screen, ChatScreen chat, RecordingBackend backend)
        {
            Screen = screen;
            Chat = chat;
            Backend = backend;
        }

        public VirtualizedChatTimeline Timeline => Chat.Timeline.Timeline;

        public static Session Create(int cols, int rows)
        {
            var backend = new RecordingBackend();
            var screen = new ScreenSession(new AnsiWriter(backend, syncUpdates: true), cols, rows);
            var chat = ChatScreen.Build(new ComposerController(), new StatusViewModel { Model = "kilocode/hy3" });
            chat.Status.Vm.Mode = StatusBarMode.Running; // spinner animates every frame
            return new Session(screen, chat, backend);
        }

        /// <summary>Feeds the timeline until it is <paramref name="multiple"/>× the
        /// viewport tall (so the tail can be scrolled far out of view), then opens
        /// a live stream block the way <c>StreamCoalescer.StartStream</c> does.
        /// Each append is followed by a layout pass — TotalHeight stays 0 until
        /// then, so the loop would never terminate without it.</summary>
        public StreamingMarkdownBlock PopulateAndStream(int cols, int rows, int multiple = 2)
        {
            var tl = Timeline;
            // Ring eviction mid-scenario would flag viewport-wide damage and mask
            // what these tests measure — give the feed room instead.
            tl.BudgetBytes = Math.Max(tl.BudgetBytes, 16L << 20);
            for (int i = 0; tl.TotalHeight < rows * multiple; i++)
            {
                tl.Append(new UserBlock($"user prompt {i} asking something reasonably long to wrap the row"));
                tl.Append(new AssistantMarkdownBlock($"## Answer {i}\n- alpha\n- beta\n`code` tail.\n"));
                _ = tl.PrepareFrame(cols, rows);
            }

            var stream = new StreamingMarkdownBlock();
            tl.Append(stream);
            return stream;
        }

        /// <summary>One frame. <see cref="FrameOutcome.FullScan"/> is what the
        /// engine had to do; the narrow ledger rides in <see cref="Fx"/>.</summary>
        public FrameOutcome Frame(int cols, int rows, bool fx)
        {
            Screen.CheckAutoSize();
            Chat.Tree.Solve(cols, rows);
            var tlRect = Chat.Timeline.Rect;
            _ = Timeline.PrepareFrame(tlRect.Width > 0 ? tlRect.Width : cols, Math.Max(0, tlRect.Height));

            Screen.BeginFrame();
            Chat.Tree.PaintAll(Screen.PaintBuffer);

            // Snapshot BEFORE consuming: ConsumeFrameDamage hands the ledger to
            // the host and clears it, so reading HasDirtyRect afterwards would
            // always report false no matter what the frame decided.
            bool hasDirtyRect = Timeline.HasDirtyRect;
            bool fullScan = Timeline.ConsumeFrameDamage(Fx, out int fxCount);
            if (fx && !fullScan)
            {
                // Status row (spinner, mascot, crossfades) is always hinted — one
                // row next to the hundreds the feed holds.
                var statusRect = Chat.Status.Rect;
                if (statusRect.Height > 0)
                {
                    Screen.Damage(new Rect(0, statusRect.Y, cols, statusRect.Height));
                }

                for (int i = 0; i < fxCount; i++)
                {
                    Screen.Damage(Fx[i]);
                }
            }

            Screen.FlushFrame();
            return new FrameOutcome(fullScan, fxCount, hasDirtyRect);
        }

        /// <summary>One paced stream tick: the coalescer pushes a line and marks
        /// the block that actually grew. <paramref name="mark"/> selects how:
        /// <c>false</c> reproduces the pre-#465 coalescer, which called
        /// <see cref="VirtualizedChatTimeline.MarkLastDirty"/> and paid a
        /// viewport-wide full scan on every frame.</summary>
        public void StreamTick(StreamingMarkdownBlock stream, string chunk, bool mark = true)
        {
            stream.Push(chunk);
            if (mark)
            {
                Timeline.MarkDirty(stream);
            }
            else
            {
                Timeline.MarkLastDirty();
            }
        }
    }

    /// <summary>
    /// The measured case: the tail has scrolled below the fold, so a growing
    /// stream changes nothing on screen. Before #465 every one of those frames
    /// ran the fused full scan; now the timeline reports narrow damage and the
    /// frame takes the hinted path.
    /// </summary>
    [Test]
    public async Task Stream_Tail_Below_Viewport_Takes_Hinted_Path_With_No_Timeline_Damage()
    {
        var probe = Session.Create(Cols, Rows);
        var stream = probe.PopulateAndStream(Cols, Rows);

        // Baseline + the scroll frame itself legitimately move the viewport.
        _ = probe.Frame(Cols, Rows, fx: false);
        probe.Timeline.ScrollToTop();
        _ = probe.Frame(Cols, Rows, fx: false);

        for (int tick = 0; tick < 12; tick++)
        {
            probe.StreamTick(stream, $"streamed line {tick} with a little text on it\n");

            var frame = probe.Frame(Cols, Rows, fx: true);

            await Assert.That(frame.FullScan).IsFalse();
            await Assert.That(frame.HasDirtyRect).IsFalse();
            await Assert.That(frame.FxCount).IsEqualTo(0);
            await Assert.That(probe.Screen.Engine.FrontMatches(probe.Screen.Back)).IsTrue();
        }
    }

    /// <summary>Tail inside the fold: the narrow rect is the block's own suffix —
    /// its top row through the bottom of the timeline, since every row below a
    /// height change reflows.</summary>
    [Test]
    public async Task Stream_Tail_Inside_Viewport_Hints_Only_Its_Suffix()
    {
        var probe = Session.Create(Cols, Rows);
        probe.Timeline.Append(new UserBlock("short prompt"));
        var stream = new StreamingMarkdownBlock();
        probe.Timeline.Append(stream);

        _ = probe.Frame(Cols, Rows, fx: false); // flush the appends

        for (int tick = 0; tick < 8; tick++)
        {
            probe.StreamTick(stream, $"streamed line {tick} with a little text on it\n");

            var frame = probe.Frame(Cols, Rows, fx: true);
            var timelineRect = probe.Chat.Timeline.Rect;

            await Assert.That(frame.FullScan).IsFalse();
            await Assert.That(frame.HasDirtyRect).IsTrue();
            await Assert.That(frame.FxCount).IsEqualTo(1);

            var dirty = probe.Fx[0];
            await Assert.That(dirty.X).IsEqualTo(timelineRect.X);
            await Assert.That(dirty.Width).IsEqualTo(timelineRect.Width);
            await Assert.That(dirty.Bottom).IsEqualTo(timelineRect.Bottom);
            await Assert.That(dirty.Y).IsGreaterThanOrEqualTo(timelineRect.Y);
            await Assert.That(probe.Screen.Engine.FrontMatches(probe.Screen.Back)).IsTrue();
        }
    }

    /// <summary>
    /// A pinned tail that grows takes the viewport with it: every visible row
    /// shows a different virtual row, so the full scan is not waste here. Pinned
    /// down so nobody "optimizes" this frame into dropped rows.
    /// </summary>
    [Test]
    public async Task Pinned_Tail_Growth_Keeps_FullScan_Damage()
    {
        var probe = Session.Create(Cols, Rows);
        var stream = probe.PopulateAndStream(Cols, Rows);

        _ = probe.Frame(Cols, Rows, fx: false);
        await Assert.That(probe.Timeline.FollowTail).IsTrue();

        for (int tick = 0; tick < 6; tick++)
        {
            probe.StreamTick(stream, $"streamed line {tick} with a little text on it\n");

            var frame = probe.Frame(Cols, Rows, fx: true);

            await Assert.That(frame.FullScan).IsTrue();
            await Assert.That(frame.FxCount).IsEqualTo(0);
            await Assert.That(probe.Screen.Engine.FrontMatches(probe.Screen.Back)).IsTrue();
        }
    }

    /// <summary>
    /// Golden contract: the hinted damage policy must serialize byte-identically
    /// to the fused full scan over the same scripted traffic — streaming appends,
    /// a mid-stream scroll-away, a scroll back to the pinned tail, a partially
    /// clipped tail, and an append that lands below the live stream block.
    /// </summary>
    [Test]
    public async Task Hinted_Tail_Stream_Is_ByteIdentical_To_FullScan()
    {
        var full = Session.Create(Cols, Rows);
        var hinted = Session.Create(Cols, Rows);
        var sessions = new[] { full, hinted };
        var streams = new StreamingMarkdownBlock[sessions.Length];
        int line = 0;

        for (int frame = 0; frame < 40; frame++)
        {
            bool inputDriven = false;

            if (frame == 0)
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    streams[i] = sessions[i].PopulateAndStream(Cols, Rows);
                }
            }
            else if (frame == 4)
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    sessions[i].Timeline.ScrollUp(9);
                }

                inputDriven = true;
            }
            else if (frame == 14)
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    sessions[i].Timeline.ScrollDown(9); // back to the pinned tail
                }

                inputDriven = true;
            }
            else if (frame == 22)
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    sessions[i].Timeline.ScrollUp(3); // tail half-clipped at the fold
                }

                inputDriven = true;
            }
            else if (frame == 30)
            {
                // An append below the live stream block shifts nothing the narrow
                // rect already covers, but it must still be broad.
                for (int i = 0; i < sessions.Length; i++)
                {
                    sessions[i].Timeline.Append(
                        new ToolCallBlock(new ToolCallInfo("t1", "read", "{\"path\":\"src/x.cs\"}")));
                }

                inputDriven = true;
            }

            if (!inputDriven)
            {
                var chunk = $"streamed line {line++} of the assistant answer\n";
                for (int i = 0; i < sessions.Length; i++)
                {
                    sessions[i].StreamTick(streams[i], chunk);
                }
            }

            // Reference session: full scan every frame. Hinted session: the
            // conservative damage contract.
            for (int i = 0; i < sessions.Length; i++)
            {
                _ = sessions[i].Frame(Cols, Rows, fx: false);
            }
        }

        await Assert.That(hinted.Backend.Text).IsEqualTo(full.Backend.Text);
        await Assert.That(hinted.Screen.Engine.FrontMatches(hinted.Screen.Back)).IsTrue();
        await Assert.That(full.Screen.Engine.FrontMatches(full.Screen.Back)).IsTrue();
    }

    /// <summary>
    /// The whole #465 claim, as a deterministic A/B instead of a stopwatch.
    ///
    /// Two sessions receive the identical scripted stream over an identical
    /// feed. One marks the block that grew (<see cref="VirtualizedChatTimeline.MarkDirty"/>),
    /// the other reproduces the pre-fix coalescer (<see cref="VirtualizedChatTimeline.MarkLastDirty"/>).
    /// Every frame of the old path must full-scan; every frame of the new one must
    /// not; and the painted bytes must match exactly.
    ///
    /// Pinning it this way means the regression cannot come back quietly: a
    /// wall-clock assertion would only fail on a machine slow enough to notice,
    /// whereas re-routing the streaming path to the broad mark fails here on any
    /// machine. The millisecond figures live in docs/BENCHMARKS.md, which is the
    /// single source of truth for every measured number in the repo.
    /// </summary>
    [Test]
    public async Task Stream_Frames_Take_Hinted_Path_Where_The_Old_Mark_FullScanned()
    {
        const int cols = 120;
        const int rows = 500;
        const int frames = 24;

        var named = Session.Create(cols, rows);
        var broad = Session.Create(cols, rows);

        // 20x the viewport so the streaming tail sits far below the fold: the
        // frame shape where a broad mark is pure waste.
        var namedStream = named.PopulateAndStream(cols, rows, multiple: 20);
        var broadStream = broad.PopulateAndStream(cols, rows, multiple: 20);

        named.Frame(cols, rows, fx: false);
        named.Timeline.ScrollToTop();
        named.Frame(cols, rows, fx: false);

        broad.Frame(cols, rows, fx: false);
        broad.Timeline.ScrollToTop();
        broad.Frame(cols, rows, fx: false);

        for (int i = 0; i < frames; i++)
        {
            string chunk = $"streamed line {i} of the answer, long enough to wrap at {cols} columns\n";

            named.StreamTick(namedStream, chunk);
            broad.StreamTick(broadStream, chunk, mark: false);

            var namedFrame = named.Frame(cols, rows, fx: true);
            var broadFrame = broad.Frame(cols, rows, fx: true);

            await Assert.That(broadFrame.FullScan)
                .IsTrue()
                .Because($"the pre-#465 broad mark full-scans on every streaming frame (frame {i})");
            await Assert.That(namedFrame.FullScan)
                .IsFalse()
                .Because($"the streaming frame names the block that grew and hints instead (frame {i})");
        }

        // Same damage policy, same bytes on the wire.
        await Assert.That(named.Backend.Text).IsEqualTo(broad.Backend.Text);
        await Assert.That(named.Screen.Engine.FrontMatches(named.Screen.Back)).IsTrue();
        await Assert.That(broad.Screen.Engine.FrontMatches(broad.Screen.Back)).IsTrue();
    }
}
