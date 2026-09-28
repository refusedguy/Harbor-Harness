using Harbor.Tui.CellForge.Streaming;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #489 — the newline rescan inside <see cref="StreamCoalescer"/>.
///
/// <c>Incoming</c> appended the delta to <c>_incoming</c> and then asked
/// <c>ContainsNewline(_incoming)</c>, which walked <b>every chunk from
/// position 0</b> on every delta. <c>_incoming</c> holds only the current
/// partial line, so for a streamed answer that has not reached a newline yet —
/// a single long line, the common case — one delta cost O(L) and a line cost
/// O(L·D). The fix scans the delta alone, which is sound only because
/// <c>_incoming</c> is newline-free on entry by construction: the drain keeps
/// the text after the LAST '\n' and enqueues everything up to it, so no '\n'
/// can survive in the buffer across calls.
///
/// That "no '\n' survives" property is state that could silently drift from
/// the text, so it is pinned here rather than trusted:
/// <list type="number">
///   <item><description>a '\n' arriving in a LATER chunk is still detected, and
///     the buffered partial line is merged into the same source line — the
///     exact case a rescan-free scan could get wrong;</description></item>
///   <item><description>chunking invariance: every split of the same text
///     (including every single cut point, and one char per delta) yields
///     byte-identical source;</description></item>
///   <item><description>the thinking stream keeps the same contract;</description></item>
///   <item><description>MEASUREMENT: scanned chars track streamed chars, not
///     buffer length, and the per-delta hot path stays inside its
///     allocation budget.</description></item>
/// </list>
/// </summary>
public class StreamCoalescerScanTests
{
    /// <summary>Ends on a newline so the tail buffer is empty at
    /// <c>FinishStream</c> — the committed source is then exactly the drained
    /// queue, in order.</summary>
    private const string Twin = "alpha line\nbeta line\ngamma line\n";

    private static (StreamCoalescer Coalescer, ChatTimelinePanel Panel) New()
    {
        var panel = new ChatTimelinePanel("chat", 40, 8);
        return (new StreamCoalescer(panel, new StatusViewModel()), panel);
    }

    /// <summary>Streams every chunk, then commits. The committed
    /// <c>AssistantMarkdownBlock</c> carries the exact accumulated source
    /// (<c>RawText()</c> is that source, unrendered).</summary>
    private static string Commit(StreamCoalescer coalescer, ChatTimelinePanel panel, params string[] chunks)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            coalescer.Incoming(chunks[i]);
        }

        coalescer.FinishStream();
        return ((AssistantMarkdownBlock)panel.Timeline.BlockAt(0)).RawText();
    }

    /// <summary>
    /// Ticks the pacer until the paced queue is empty, so the later
    /// <c>FinishStream</c> only has the unterminated tail to flush and the
    /// committed source comes out in arrival order. Smooth mode reveals one
    /// line per tick; the extra ticks are the safety margin.
    /// </summary>
    private static void DrainQueue(StreamCoalescer coalescer, int lines)
    {
        for (int tick = 0; tick <= lines + 8; tick++)
        {
            coalescer.DrainPaced(nowMs: tick + 1);
        }
    }

    [Test]
    public async Task Newline_In_A_LaterChunk_Is_Detected_And_Merges_The_PartialLine()
    {
        var (coalescer, panel) = New();

        // Two newline-free chunks buffer "abcde", then a third carries the '\n'
        // plus a new partial tail. A delta-local scan must still see it, and the
        // buffered prefix must join the SAME source line — not become one.
        coalescer.Incoming("abc");
        coalescer.Incoming("de");
        coalescer.Incoming("f\nghi");

        // Reveal the paced line, then commit the unterminated tail.
        DrainQueue(coalescer, 1);
        coalescer.FinishStream();

        await Assert.That(((AssistantMarkdownBlock)panel.Timeline.BlockAt(0)).RawText())
            .IsEqualTo("abcdef\nghi");
    }

    [Test]
    public async Task Newline_As_The_Last_Char_Of_A_LaterChunk_Closes_The_Line()
    {
        var (coalescer, panel) = New();

        string source = Commit(coalescer, panel, "no break yet", " then the break\n");

        await Assert.That(source).IsEqualTo("no break yet then the break\n");
    }

    [Test]
    public async Task Every_Single_Cut_Point_Produces_The_Identical_Source()
    {
        // Exhaustive twin over the whole split space: the scan cannot depend on
        // where the provider happened to cut a delta.
        for (int cut = 0; cut <= Twin.Length; cut++)
        {
            var (coalescer, panel) = New();
            string source = Commit(coalescer, panel, Twin[..cut], Twin[cut..]);
            await Assert.That(source).IsEqualTo(Twin);
        }
    }

    [Test]
    public async Task One_Char_Per_Delta_Produces_The_Identical_Source()
    {
        // Many times the deltas of the single-delta case (the O(L·D) shape the
        // issue reported), byte-identical result.
        var (single, singlePanel) = New();
        string reference = Commit(single, singlePanel, Twin);

        var (perChar, perCharPanel) = New();
        for (int i = 0; i < Twin.Length; i++)
        {
            perChar.Incoming(Twin[i].ToString());
        }

        perChar.FinishStream();
        await Assert.That(((AssistantMarkdownBlock)perCharPanel.Timeline.BlockAt(0)).RawText())
            .IsEqualTo(reference);
    }

    [Test]
    public async Task Newlines_In_One_Delta_Segment_Once_Per_Line()
    {
        var (coalescer, panel) = New();

        // A burst delta carrying several lines plus a trailing partial: every
        // line is committed, the partial is not.
        coalescer.Incoming("l1\nl2\nl3\ntail");
        DrainQueue(coalescer, 3);
        coalescer.FinishStream();

        await Assert.That(((AssistantMarkdownBlock)panel.Timeline.BlockAt(0)).RawText())
            .IsEqualTo("l1\nl2\nl3\ntail");
    }

    [Test]
    public async Task Empty_Deltas_Are_NoOps()
    {
        var (coalescer, panel) = New();

        await Assert.That(Commit(coalescer, panel, "", "text", "")).IsEqualTo("text");
    }

    [Test]
    public async Task Thinking_Stream_Detects_A_Newline_In_A_LaterChunk()
    {
        var (coalescer, panel) = New();

        coalescer.IncomingThinking("reasoning so far");
        coalescer.IncomingThinking(" and more\nnext thought");
        coalescer.IncomingThinking(" tail");

        var block = (StreamingThinkingBlock)panel.Timeline.BlockAt(0);
        await Assert.That(block.RawText()).IsEqualTo("reasoning so far and more\n");
    }

    [Test]
    public async Task ScanChars_Track_Streamed_Chars_Not_Buffer_Length()
    {
        var (coalescer, _) = New();
        coalescer.StartStream();

        // The issue's shape: a 200-char SINGLE line, one char per delta. The
        // old rescan walked 1+2+...+200 = 20_100 chars; the delta-local scan
        // inspects each delta exactly once.
        for (int i = 0; i < 200; i++)
        {
            coalescer.Incoming("x");
        }

        await Assert.That(coalescer.NewlineScanChars).IsEqualTo(200);
    }

    [Test]
    public async Task ScanChars_Meter_Resets_Per_Stream()
    {
        var (coalescer, _) = New();
        coalescer.StartStream();
        coalescer.Incoming("abcdefghij");
        await Assert.That(coalescer.NewlineScanChars).IsEqualTo(10);

        coalescer.StartStream();
        await Assert.That(coalescer.NewlineScanChars).IsEqualTo(0);
    }

    [Test]
    public async Task ScanChars_Stay_Streamed_Length_When_Lines_Complete()
    {
        var (coalescer, _) = New();
        coalescer.StartStream();

        // Newlines do not buy free rescans: the meter tracks input bytes only.
        const string chunk = "0123456789\n";
        for (int i = 0; i < 25; i++)
        {
            coalescer.Incoming(chunk);
        }

        await Assert.That(coalescer.NewlineScanChars).IsEqualTo(chunk.Length * 25);
    }

    /// <summary>
    /// Allocation budget for the partial-line hot path. Honest scope: the OLD
    /// code did not allocate here either — <c>ContainsNewline</c> walks spans —
    /// so the #489 win on this path is CPU, and CPU is what
    /// <see cref="ScanChars_Track_Streamed_Chars_Not_Buffer_Length"/>
    /// measures. This gate exists so the hot path cannot drift INTO an
    /// allocation (a per-delta <c>ToString()</c>/<c>SnapshotText()</c> shows up
    /// here at once, and grows with the line length); the only permitted bytes
    /// are the one-off StringBuilder chunk growth.
    /// </summary>
    [Test]
    public async Task PartialLine_HotPath_Stays_Within_Allocation_Budget()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies 4x across OS runtimes.

        const int deltas = 200;
        var (coalescer, _) = New();

        // Warm past the JIT tier-up threshold (a tier transition charges one-off
        // bookkeeping bytes to the measuring thread) and grow the builder chunks
        // once, so the measured window contains stream work and nothing else.
        for (int i = 0; i < 5_000; i++)
        {
            coalescer.Incoming("x");
        }

        coalescer.FinishStream();

        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            coalescer.StartStream(); // outside the window: it allocates the block
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < deltas; i++)
            {
                coalescer.Incoming("x");
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            coalescer.FinishStream();
            if (allocated < best)
            {
                best = allocated;
            }
        }

        Console.WriteLine(
            $"coalescer-alloc: {deltas} partial-line deltas = {best} B (min of 3, StringBuilder chunk growth only)");
        // 200 one-char deltas grow a (freshly cleared) builder through ~4
        // chunks: well under 1 KB. 2 KB leaves CI headroom and still fails on
        // any per-delta materialization, which is both >= 2 B/delta and grows
        // with the line length.
        await Assert.That(best).IsLessThanOrEqualTo(2 * 1024L);
    }
}
