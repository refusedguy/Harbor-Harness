using System.Diagnostics;
using Harbor.Ipc.Protocol;

namespace Harbor.Ipc.Tests.Fuzz;

/// <summary>
///     Fuzz / robustness tests for the IPC framing read loop, asserting the
///     <b>policy</b> of <c>ResilientFrameReader</b> (committed static-D3 shape today,
///     instance-D2 shape once integration lands — see ResilientFrameReaderProbe):
///     malformed and zero-length frames are classified (not thrown), truncated frames
///     surface as stream-end, oversized declared lengths are rejected without
///     allocating the declared size, and the stream stays consumable for whatever
///     follows.
/// </summary>
/// <remarks>
///     These tests drive the reader directly over a <see cref="MemoryStream" /> — no
///     real pipes/sockets, so they cannot hit the named-pipe parallel-scheduling
///     deadlock that keeps this project out of the solution. The reader is reached via
///     <see cref="ResilientFrameReaderProbe" /> because it is internal; see that type's
///     remarks for why the tests compare outcome names instead of typed enums.
/// </remarks>
public class ResilientFrameReaderFuzzTests
{
    private static void AssertReaderAvailable()
    {
        if (!ResilientFrameReaderProbe.IsAvailable)
        {
            throw new InvalidOperationException(
                "ResilientFrameReader not found in Harbor.Ipc.Server assembly — "
                + "parallel D2/D3 change renamed it; update ResilientFrameReaderProbe.");
        }
    }

    // ── 1. Random bytes as frame payloads ─────────────────────────────────

    /// <summary>
    ///     Payloads whose first byte is 0xC1 (a byte MessagePack never uses as a
    ///     code) must classify as UndecodableFrame: no exception escapes, the error
    ///     is surfaced on the result, and the reader consumed exactly the frame.
    /// </summary>
    [Test]
    public async Task RandomGarbagePayloads_ClassifiedAsUndecodable_WithoutThrowing()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();
        var rng = new Random(20260823);

        for (int i = 0; i < 25; i++)
        {
            int length = rng.Next(1, 128);
            byte[] payload = new byte[length];
            rng.NextBytes(payload);
            payload[0] = 0xC1; // never-valid msgpack first byte → deterministic decode failure

            var ms = new MemoryStream();
            ResilientFrameReaderProbe.AppendFrame(ms, payload);
            long total = ms.Length;
            ms.Position = 0;

            var probe = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

            await Assert.That(probe.Outcome).IsEqualTo(ResilientFrameReaderProbe.UndecodableFrame);
            await Assert.That(probe.RequestId).IsNull();
            await Assert.That(probe.ErrorText).IsNotNull();
            await Assert.That(ms.Position).IsEqualTo(total);
        }
    }

    /// <summary>
    ///     Purely random payloads must never throw out of the reader: each attempt is
    ///     classified as either UndecodableFrame (decode failure) or Request (the
    ///     bytes happened to be a valid union payload) — anything else fails.
    /// </summary>
    [Test]
    public async Task TrulyRandomPayloads_NeverThrow_ClassifiedSensibly()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();
        var rng = new Random(424242);
        string[] allowed = [ResilientFrameReaderProbe.UndecodableFrame, ResilientFrameReaderProbe.Request];

        for (int i = 0; i < 50; i++)
        {
            int length = rng.Next(1, 256);
            byte[] payload = new byte[length];
            rng.NextBytes(payload);

            var ms = new MemoryStream();
            ResilientFrameReaderProbe.AppendFrame(ms, payload);
            long total = ms.Length;
            ms.Position = 0;

            var probe = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

            await Assert.That(allowed).Contains(probe.Outcome);
            if (probe.Outcome == ResilientFrameReaderProbe.UndecodableFrame)
                await Assert.That(probe.ErrorText).IsNotNull();
            else
                await Assert.That(probe.RequestId).IsNotNull();
            await Assert.That(ms.Position).IsEqualTo(total);
        }
    }

    // ── 2. Truncated frames ───────────────────────────────────────────────

    /// <summary>A declared length N with only M&lt;N payload bytes → StreamEnded, no throw.</summary>
    [Test]
    public async Task TruncatedPayload_ReturnsStreamEnded_Gracefully()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();

        var ms = new MemoryStream();
        ResilientFrameReaderProbe.AppendHeader(ms, 100);
        ms.Write(new byte[40]); // peer dies mid-payload
        ms.Position = 0;

        var probe = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

        await Assert.That(probe.Outcome).IsEqualTo(ResilientFrameReaderProbe.StreamEnded);
        await Assert.That(probe.RequestId).IsNull();
        await Assert.That(probe.ErrorText).IsNull();
        await Assert.That(ms.Position).IsEqualTo(44L);
    }

    /// <summary>Fewer than 4 header bytes then EOF → StreamEnded.</summary>
    [Test]
    public async Task TruncatedHeader_ReturnsStreamEnded()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();

        var ms = new MemoryStream([0x00, 0x00]);
        ms.Position = 0;

        var probe = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

        await Assert.That(probe.Outcome).IsEqualTo(ResilientFrameReaderProbe.StreamEnded);
        await Assert.That(ms.Position).IsEqualTo(2L);
    }

    /// <summary>Empty stream (clean EOF at frame boundary) → StreamEnded.</summary>
    [Test]
    public async Task EmptyStream_ReturnsStreamEnded()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();

        var probe = await ResilientFrameReaderProbe.ReadAsync(reader, new MemoryStream());

        await Assert.That(probe.Outcome).IsEqualTo(ResilientFrameReaderProbe.StreamEnded);
    }

    // ── 3. Zero-length frames ─────────────────────────────────────────────

    /// <summary>
    ///     A zero-length frame classifies as EmptyFrame, consumes only its 4-byte
    ///     header (stream stays in sync), and the next valid frame still parses.
    /// </summary>
    [Test]
    public async Task ZeroLengthFrame_Skipped_AndSubsequentValidFrameStillParsed()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();

        var request = new SendPromptRequest("hello after garbage");
        var ms = new MemoryStream();
        ResilientFrameReaderProbe.AppendHeader(ms, 0);          // keep-alive no-op frame
        ResilientFrameReaderProbe.AppendFrame(ms, ResilientFrameReaderProbe.SerializeRequest(request));
        ms.Position = 0;

        var first = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

        await Assert.That(first.Outcome).IsEqualTo(ResilientFrameReaderProbe.EmptyFrame);
        await Assert.That(first.RequestId).IsNull();
        await Assert.That(first.ErrorText).IsNull();
        await Assert.That(ms.Position).IsEqualTo(4L);           // nothing past the header consumed

        var second = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

        await Assert.That(second.Outcome).IsEqualTo(ResilientFrameReaderProbe.Request);
        await Assert.That(second.RequestId).IsEqualTo(request.RequestId);
        await Assert.That(second.ErrorText).IsNull();
    }

    // ── 4. Oversized declared headers ×10 — policy rejection without OOM ──

    /// <summary>
    ///     Ten consecutive over-cap declared lengths (cap+1 and uint.MaxValue) must all
    ///     classify as OversizedFrame quickly, consume nothing but the 4-byte header,
    ///     and never allocate the declared size (an OOM/overflow would fail the test).
    /// </summary>
    [Test]
    public async Task TenConsecutiveOversizedHeaders_RejectedByPolicy_WithoutAllocatingDeclaredSize()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();
        uint capPlusOne = checked((uint)(ResilientFrameReaderProbe.ActiveMaxFrameBytes + 1));

        var ms = new MemoryStream();
        for (int i = 0; i < 10; i++)
            ResilientFrameReaderProbe.AppendHeader(ms, i % 2 == 0 ? capPlusOne : uint.MaxValue);
        ms.Write(new byte[8]); // junk proving the (never-consumed) payloads aren't awaited
        ms.Position = 0;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            var probe = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

            await Assert.That(probe.Outcome).IsEqualTo(ResilientFrameReaderProbe.OversizedFrame);
            await Assert.That(probe.ErrorText).Contains("exceeds");
            await Assert.That(ms.Position).IsEqualTo(4L * (i + 1)); // payload NOT consumed
        }

        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThanOrEqualTo(5000);
    }

    /// <summary>
    ///     Boundary semantics: a declared length exactly equal to the cap is allowed by
    ///     policy (only strictly-greater is oversized); truncated at the cap it surfaces
    ///     as StreamEnded. Also proves a full cap-sized allocation attempt does not OOM.
    /// </summary>
    [Test]
    public async Task DeclaredLengthExactlyAtCap_IsNotOversized_TruncatesAsStreamEnded()
    {
        AssertReaderAvailable();
        object? reader = ResilientFrameReaderProbe.CreateReader();

        var ms = new MemoryStream();
        ResilientFrameReaderProbe.AppendHeader(ms, checked((uint)ResilientFrameReaderProbe.ActiveMaxFrameBytes));
        ms.Write(new byte[10]);
        ms.Position = 0;

        var probe = await ResilientFrameReaderProbe.ReadAsync(reader, ms);

        await Assert.That(probe.Outcome).IsEqualTo(ResilientFrameReaderProbe.StreamEnded);
        await Assert.That(ms.Position).IsEqualTo(14L);
    }
}
