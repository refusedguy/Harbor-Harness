extern alias CompatWire;

using TUnit.Assertions;
using CompatSseLineKind = CompatWire::Harbor.Providers.Internal.SseLineKind;
using CompatSsePump = CompatWire::Harbor.Providers.Internal.SsePump;

namespace Harbor.Providers.Tests;

/// <summary>
/// Allocation-budget coverage for the SSE **wire decode** (#467) — the step
/// that sits above <c>OpenAiWire.TryParseChatChunkLine</c>, i.e. outside the
/// reach of the #186 chunk-parsing tripwire
/// <c>TryParseChatChunkLine_TextDelta_StaysBounded</c>, which starts below
/// <c>data:</c> and therefore cannot see it.
/// <para>
/// The pre-#467 decode was <c>line["data:".Length..].TrimStart()</c> plus
/// <c>data.Trim().Equals("[DONE]", Ordinal)</c>: two payload-sized copies for
/// the usual <c>data: {json}</c> line and a third whenever the server pads the
/// sentinel — kilobytes each for tool-call argument deltas, spent purely to
/// compare the payload with a six-character literal. <c>SsePump.DecodeDataLine</c>
/// trims and tests on spans, so the decode allocates nothing; the caller
/// materialises the payload string exactly once, for the chunk parser.
/// </para>
/// <para>
/// The gate asserts an exact zero rather than a ceiling, so
/// <c>LegacyDecode_StillCopiesPayload_TripwireIsNotVacuous</c> keeps the
/// pre-#467 chain around and proves the measured delta is real.
/// </para>
/// <para>
/// <see cref="NotInParallelAttribute" />: same reason as
/// <c>AgentLoopAllocationTests</c> — the counters are thread-scoped and
/// neighbour tests move them. Min-of-3 rounds, linux-only.
/// </para>
/// </summary>
[NotInParallel("alloc-tripwire")]
public class SseWireDecodeAllocationTests
{
    /// <summary>
    ///     Tool-call argument-delta sized payload (4 KB of chars) — the case
    ///     #467 calls out, and the size the old copies were measured against.
    /// </summary>
    private const int PayloadChars = 4 * 1024;

    private const int Lines = 500;

    /// <summary>One <c>data: {json}</c> line carrying <paramref name="payloadChars" /> of arguments.</summary>
    private static string DataLine(int payloadChars = PayloadChars) =>
        "data: {\"arguments\":\"" + new string('a', payloadChars) + "\"}";

    /// <summary>Classification only — a non-async seam, so no span outlives an await.</summary>
    private static CompatSseLineKind KindOf(string line) =>
        CompatSsePump.DecodeDataLine(line, out _);

    /// <summary>The decoded payload as a string (empty when the line carries none).</summary>
    private static string PayloadOf(string line) =>
        CompatSsePump.DecodeDataLine(line, out ReadOnlySpan<char> payload) == CompatSseLineKind.Payload
            ? payload.ToString()
            : string.Empty;

    [Test]
    public async Task DecodeDataLine_KilobytePayload_AllocatesNothing()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only: GC accounting varies across OS runtimes.

        string line = DataLine();
        await Assert.That(KindOf(line)).IsEqualTo(CompatSseLineKind.Payload);
        await Assert.That(PayloadOf(line).Length).IsGreaterThan(PayloadChars);

        // Warm up (JIT tiers, statics) before the counters start.
        for (int i = 0; i < 200; i++)
        {
            _ = CompatSsePump.DecodeDataLine(line, out _);
        }

        // Min-of-3 rounds: a single GC/JIT hiccup must not fail the gate.
        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Lines; i++)
            {
                _ = CompatSsePump.DecodeDataLine(line, out _);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < best)
            {
                best = allocated;
            }
        }

        Console.WriteLine($"sse-alloc: DecodeDataLine avg = {(double)best / Lines:F0} B over {Lines} 4 KB SSE lines (min of 3)");
        // Zero, not a budget: the whole point of #467 is that the decode no
        // longer copies the payload. The legacy chain in the sibling test
        // allocates ~2 payload copies per line here (~16 KB), so any revert
        // blows this by three orders of magnitude.
        await Assert.That(best).IsEqualTo(0);
    }

    [Test]
    public async Task LegacyDecode_StillCopiesPayload_TripwireIsNotVacuous()
    {
        if (!OperatingSystem.IsLinux())
            return; // Tripwire is linux-only (see above).

        string line = DataLine();

        // The pre-#467 chain, verbatim — substr, then TrimStart, then compare
        // the whole payload against "[DONE]". Kept so the zero-alloc gate
        // above cannot pass vacuously.
        long best = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Lines; i++)
            {
                string data = line["data:".Length..].TrimStart();
                _ = data.Trim().Equals("[DONE]", StringComparison.Ordinal);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < best)
            {
                best = allocated;
            }
        }

        Console.WriteLine($"sse-alloc: legacy decode avg = {(double)best / Lines:F0} B over {Lines} 4 KB SSE lines (min of 3)");
        // Two payload copies per line = 4 bytes per payload char (~16.5 KB
        // here); the gate above demands an exact zero, so even one surviving
        // copy (8 KB) fails it by three orders of magnitude. This bound only
        // has to prove the legacy chain really does allocate.
        await Assert.That(best).IsGreaterThan(Lines * PayloadChars * 2L);
    }

    [Test]
    public async Task DecodeDataLine_WireVariants_ClassifyWithoutCopying()
    {
        // `data:{...}` with no space is legal SSE (#86) — and the case where
        // TrimStart is a no-op, so the payload must arrive untouched.
        await Assert.That(KindOf("data:{ \"a\":1 }")).IsEqualTo(CompatSseLineKind.Payload);
        await Assert.That(PayloadOf("data:{ \"a\":1 }")).IsEqualTo("{ \"a\":1 }");

        // The prefix test is case-insensitive, as it has always been.
        await Assert.That(KindOf("DATA: {\"a\":1}")).IsEqualTo(CompatSseLineKind.Payload);

        // Heartbeats, comments and other fields are skipped, never parsed.
        await Assert.That(KindOf("data:")).IsEqualTo(CompatSseLineKind.Empty);
        await Assert.That(KindOf("data:    ")).IsEqualTo(CompatSseLineKind.Empty);
        await Assert.That(KindOf(": keep-alive")).IsEqualTo(CompatSseLineKind.NotData);
        await Assert.That(KindOf("event: message")).IsEqualTo(CompatSseLineKind.NotData);
        await Assert.That(KindOf("")).IsEqualTo(CompatSseLineKind.NotData);

        // Padded sentinel on both ends — the form the old `data.Trim()` copied
        // the whole payload for (#86).
        await Assert.That(KindOf("data: [DONE]   ")).IsEqualTo(CompatSseLineKind.Done);
        await Assert.That(KindOf("data:[DONE]")).IsEqualTo(CompatSseLineKind.Done);
        await Assert.That(KindOf("data:   [DONE]")).IsEqualTo(CompatSseLineKind.Done);

        // Only the sentinel itself ends the stream: a payload that merely
        // starts with it is still a payload.
        await Assert.That(KindOf("data: [DONE]x")).IsEqualTo(CompatSseLineKind.Payload);
        await Assert.That(PayloadOf("data: [DONE]x")).IsEqualTo("[DONE]x");
    }
}
