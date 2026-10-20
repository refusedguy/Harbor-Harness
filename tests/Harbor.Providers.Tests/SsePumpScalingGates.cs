extern alias CompatWire;

using System.Diagnostics;
using System.Text;
using TUnit.Assertions;
using Harbor.TestKit;
using CompatSseLineKind = CompatWire::Harbor.Providers.Internal.SseLineKind;
using CompatSsePump = CompatWire::Harbor.Providers.Internal.SsePump;

namespace Harbor.Providers.Tests;

/// <summary>
/// Issue #1526 (hot-path slice of #97): the relative cost gate for the
/// pump-level rework — <c>StreamReader.ReadLineAsync</c> (one whole-line
/// <c>string</c> per raw SSE/NDJSON line, plus a per-line <c>Task</c>) became a
/// PipeReader + SequenceReader loop whose only per-unit state is pooled:
/// pipe buffers and the byte/char decode windows, grown to the largest line
/// the stream has actually carried and returned afterwards.
/// <para>
/// The measured scale claim is RELATIVE — no absolute budgets (#410/#1373
/// shape): the pump over a 2N-line stream must cost at most 3× the time and
/// 3× the allocations of the same pump over the N-line stream, both measured
/// in the same run, so runner speed cancels in the ratio and a quadratic or
/// per-unit-leaking regression (4× at O(N²)) cannot hide. Absolute columns
/// print as info only. The leg is the WHOLE <see cref="SsePump.PumpLinesAsync" />
/// pass over a synthetic mixed stream (Anthropic-style 2 control lines + 1
/// blank per 4 KB payload, <c>[DONE]</c> sentinel at the end) — the handler
/// inside the leg materialises the one payload string per <c>data:</c> line,
/// which is the pump's documented allowance (#1526: "copy only the meaningful
/// part"), so per-line allocation claims stay honest.
/// </para>
/// <para>
/// ALLOCATION PROBE — the leg spans <c>await</c>s (the pump is async even when
/// every underlying read completes synchronously), so the per-thread counter
/// the #467 tripwire uses would misread on a thread hop. The probe is instead
/// <c>GC.GetTotalAllocatedBytes(precise: true)</c> — the process-wide counter
/// — which is where an await-window gate belongs; the BARE
/// <see cref="NotInParallelAttribute" /> (the StorePathScalingGates lesson:
/// the keyed form only de-conflicts itself) keeps two measured legs free of
/// neighbour tests, and the counter is built for exactly that reading.
/// Linux-only: GC accounting varies across OS runtimes.
/// </para>
/// </summary>
[NotInParallel]
public class SsePumpScalingGates
{
    /// <summary>Growth allowed for a doubled stream, on time and allocations (#410 shape).</summary>
    /// <summary>
    ///     Data lines in the stream the gate MEASURES at - twice
    ///     <see cref="SmallDataLines" />, because a per-pass cost that grows
    ///     with the stream length must land in the measurement instead of
    ///     hiding behind the smaller stream.
    /// </summary>
    private const int MeasuredDataLines = LargeDataLines;

    /// <summary>
    ///     Pump passes per measured leg - the FIXED amount of work. 100 passes
    ///     over the 192-data-line stream is about 37 ms of real work on the
    ///     CI runner.
    /// </summary>
    private const int PassesPerLeg = 100;

    /// <summary>
    ///     Allocation budget for ONE pump pass over the 192-data-line stream,
    ///     in bytes. Machine-independent, so it is compared as measured, with
    ///     no band. Pinned from the CI measurement of the ratio gate this
    ///     replaced (run 39423151864): 885 503 064 B over 559 passes =
    ///     1 583 905 B per pass. The budget leaves ~1.45x headroom.
    /// </summary>
    private const long MaxBytesPerPass = 2_300_000;

    /// <summary>
    ///     Time budget for ONE pump pass, in PROBE UNITS (see
    ///     <see cref="Harbor.TestKit.PerfGate" />). Machine-scaled and SOFT.
    ///     Pinned from the same run: 208.811 ms over 559 passes = 373 us per
    ///     pass against a probe that costs 0.000380 ms/unit, i.e. ~980 probe
    ///     units per pass. The budget leaves ~15x headroom, because the
    ///     probe's OWN measured cost moved 5.3x between two CI runs of the
    ///     same tree (0.000380 ms/unit in run 39423151864, 0.000072 ms/unit in
    ///     run 39436405496) - the BYTE budget is the branch that can fail on
    ///     any runner.
    /// </summary>
    private const double MaxProbeUnitsPerPass = 15_000.0;

    /// <summary>Data lines per small leg — 4 KB payload each, control lines per data line.</summary>
    private const int SmallDataLines = 96;

    /// <summary>The large leg doubles the stream.</summary>
    private const int LargeDataLines = SmallDataLines * 2;

    /// <summary>Bytes of tool-call arguments per payload — the #467/#186 measurement size.</summary>
    private const int PayloadBytes = 4 * 1024;

    private static readonly Task<bool> KeepStreaming = Task.FromResult(true);

    private static int _payloads;
    private static int _control;

    /// <summary>The one payload materialisation the pump allows (the SseLineHandler contract).</summary>
    private static string? _held;

    /// <summary>
    ///     THE GATE IS A BUDGET PER UNIT OF WORK (#1948), not a ratio. It used
    ///     to be <c>pump(2N)/pump(N) &lt;= 3</c> for time and allocations
    ///     over a 96-data-line against a 192-data-line stream, decided by the
    ///     shared TestScalingGate (#1652). It is now:
    ///     <code>
    ///     bytes per pump pass   &lt;= 2 300 000   (machine-independent, HARD)
    ///     probe units per pass  &lt;= 15 000       (machine-scaled time, SOFT)
    ///     </code>
    ///     measured over a FIXED amount of work (100 pump passes over the
    ///     192-data-line stream - the LARGER one, so a per-pass cost that
    ///     grows with the stream length lands in the measurement) on the SAME
    ///     runner in the SAME run. The allocation branch is machine-independent
    ///     and is asserted hard; the time branch is in PROBE UNITS and stands
    ///     down when the rounds do not reproduce each other. The report prints
    ///     on every run, pass or fail.
    /// </summary>
    [Test]
    [Timeout(300_000)]
    public async Task Pump_CostPerPass_StaysWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes (SseWireDecodeAllocationTests convention).

        ReplayableStream small = new(BuildBody(SmallDataLines));
        ReplayableStream large = new(BuildBody(MeasuredDataLines));

        // Non-vacuity, warmed pass: one pump over the small body really did
        // hand every payload to the handler before any budget is decided -
        // a gate over a stream the pump silently skips is the #591 shape.
        small.Reset();
        await CompatSsePump.PumpLinesAsync(small, LineAsync, CancellationToken.None);
        await Assert.That(_payloads).IsEqualTo(SmallDataLines);
        await Assert.That(_held).IsNotNull();

        PerfGateResult result = await PerfGate.MeasureAsync(
            work: passes => MeasureLeg(large, MeasuredDataLines, passes),
            units: PassesPerLeg,
            options: new PerfGateOptions
            {
                Label = "sse-pump-cost",
                MaxBytesPerUnit = MaxBytesPerPass,
                MaxProbeUnitsPerUnit = MaxProbeUnitsPerPass,
            });

        await result.AssertWithinBudgetsAsync("sse pump pass over a 192-data-line stream");
    }

    /// <summary>
    ///     One measured leg: <paramref name="units" /> whole pump passes over
    ///     the SAME body — stream position reset between passes so every unit
    ///     re-walks the full <paramref name="dataLines" />. Counters prove per
    ///     leg that the pump still serves every payload, every control line
    ///     and the sentinel (exactly <c>2*N + 1</c> control lines: event +
    ///     keep-alive per data line, blank lines are skipped, one <c>[DONE]</c>).
    /// </summary>
    private static async Task<PerfGateLeg> MeasureLeg(ReplayableStream stream, int dataLines, int units)
    {
        _payloads = 0;
        _control = 0;

        // The helper fences GC directly before this call; the counters below
        // ARE the alloc bookkeeping, so no second fence is needed.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (int unit = 0; unit < units; unit++)
        {
            stream.Reset();
            await CompatSsePump.PumpLinesAsync(stream, LineAsync, CancellationToken.None).ConfigureAwait(false);
        }

        sw.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        int expectedPayloads = units * dataLines;
        int expectedControl = units * ((2 * dataLines) + 1);
        if (_payloads != expectedPayloads || _control != expectedControl)
        {
            throw new InvalidOperationException(
                $"[sse-pump-scaling] non-vacuity failed: payloads {_payloads} != {expectedPayloads}"
                + $" or control {_control} != {expectedControl} over {units} units of {dataLines} lines — "
                + "the pump skipped part of the stream, so the ratio gates a walk that never happened.");
        }

        return new PerfGateLeg(sw.Elapsed.TotalMilliseconds, allocated);
    }

    private static Task<bool> LineAsync(ReadOnlySpan<char> line, CancellationToken ct)
    {
        if (CompatSsePump.DecodeDataLine(line, out ReadOnlySpan<char> payload) == CompatSseLineKind.Payload)
        {
            _held = new string(payload); // The pump's documented allowance — one payload copy per data line.
            _payloads++;
        }
        else
        {
            _control++;
        }

        return KeepStreaming;
    }

    /// <summary>
    ///     The synthetic mixed body: per payload line an <c>event:</c> line, a
    ///     comment, and a blank (the Anthropic stream shape), the sentinel at
    ///     the very end. Written once per leg OUTSIDE every measured window.
    /// </summary>
    private static byte[] BuildBody(int dataLines)
    {
        string payload = "{\"arguments\":\"" + new string('a', PayloadBytes) + "\"}";
        string dataLine = $"data: {payload}\n";
        const string eventLine = "event: content_block_delta\n";
        const string keepAlive = ": keep-alive\n";
        const string blank = "\n";
        const string sentinel = "data: [DONE]\n";

        var body = new StringBuilder(dataLines * (dataLine.Length + eventLine.Length + keepAlive.Length + blank.Length) + sentinel.Length);
        for (int i = 0; i < dataLines; i++)
        {
            body.Append(eventLine).Append(keepAlive).Append(blank).Append(dataLine);
        }

        body.Append(sentinel);
        return Encoding.UTF8.GetBytes(body.ToString());
    }

    /// <summary>
    ///     A fully synchronous, seek-free stream: <c>ReadAsync(Memory)</c> is
    ///     overridden so the pump's every read completes inline — no
    ///     ThreadPool hops, no per-leg scheduling noise in the TIME leg; the
    ///     allocation probe stays honest against rescheduled continuations
    ///     regardless. Position resets between units; the stream itself is
    ///     reused so per-leg state is the pump's alone.
    /// </summary>
    private sealed class ReplayableStream(byte[] body) : Stream
    {
        private int _position;

        public void Reset() => _position = 0;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadInline(buffer.Span));

        private int ReadInline(Span<byte> sink)
        {
            int take = Math.Min(sink.Length, body.Length - _position);
            if (take > 0)
            {
                body.AsSpan(_position, take).CopyTo(sink);
                _position += take;
            }

            return take;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadInline(buffer.AsSpan(offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => body.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Pump-level behaviour probes for the #1526 window-pump — the parity claims
/// the relative gate's legs do not decide: terminator handling (LF/CR/CR-LF),
/// blank-line skip, EOF trailing line, multi-segment window growth, BOM
/// parity with the retired <c>StreamReader</c> path, and the sentinel stop.
/// These ride <see cref="SsePump.PumpLinesAsync" /> directly, through the
/// same capture-handler shape the gate uses.
/// </summary>
public class SsePumpLineLoopTests
{
    [Test]
    public async Task PumpLinesAsync_MixedStream_HandsOnlyMeaningfulLines()
    {
        // Anthropic-shaped: event line, comment, blank tails around payloads;
        // CR-LF and LF terminators both appear; blank lines are never served.
        byte[] body = Encoding.UTF8.GetBytes(
            "event: content_block_delta\r\n"
            + ": keep-alive\n"
            + "\n"
            + "data: {\"n\":1}\n"
            + "\n"
            + "data: [DONE]\n");

        List<string> lines = await PumpAsync(body);

        await Assert.That(lines.Count).IsEqualTo(4);
        await Assert.That(lines[0]).IsEqualTo("event: content_block_delta");
        await Assert.That(lines[1]).IsEqualTo(": keep-alive");
        await Assert.That(lines[2]).IsEqualTo("data: {\"n\":1}");
        await Assert.That(lines[3]).IsEqualTo("data: [DONE]");
    }

    [Test]
    public async Task PumpLinesAsync_TrailingUnterminatedLine_ReachesHandler()
    {
        // EOF without a terminator: ReadLineAsync returned the last partial
        // line and Ollama NDJSON servers end the final chunk exactly that way.
        byte[] body = Encoding.UTF8.GetBytes("data: {\"n\":1}\n" + "{\"done\":true}");

        List<string> lines = await PumpAsync(body);

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[1]).IsEqualTo("{\"done\":true}");
    }

    [Test]
    public async Task PumpLinesAsync_MultibytePayload_DecodesUtf8Exact()
    {
        // Non-ASCII deltas decode byte-for-byte into the pooled window —
        // no ASCII shortcut leaking mojibake into the payload string.
        string payload = "data: {\"delta\":\"привет-мир，你好\"}";
        byte[] body = Encoding.UTF8.GetBytes(payload + "\n");

        List<string> lines = await PumpAsync(body);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo(payload);
    }

    [Test]
    public async Task PumpLinesAsync_LargeLineThenSmallLine_WindowGrowsAndSurvives()
    {
        // A 64 KB line forces both windows to outgrow the initial 1 KB rent
        // (and with the default 4 KB pipe buffer, forces the multi-segment
        // stitch path); the NEXT small line must arrive intact — the reuse
        // window contract.
        string big = "data: \"" + new string('x', 64 * 1024) + "\"";
        byte[] body = Encoding.UTF8.GetBytes(big + "\ndata: {\"small\":true}\n");

        List<string> lines = await PumpAsync(body);

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo(big);
        await Assert.That(lines[1]).IsEqualTo("data: {\"small\":true}");
    }

    [Test]
    public async Task PumpLinesAsync_HandlerStops_TailNotServed()
    {
        // Sentinel stop: false from the handler must stop the pump before
        // the tail lines of the stream reach it (the #1104 read-order shape).
        byte[] body = Encoding.UTF8.GetBytes("data: first\ndata: second\ndata: third\n");

        List<string> lines = await PumpAsync(body, stopWhen: "data: first");

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("data: first");
    }

    [Test]
    public async Task PumpLinesAsync_Utf8Bom_IsStrippedLikeReadLineAsync()
    {
        // StreamReader stripped the preamble; so does the window-pump —
        // the first line still classifies as data (no U+FEFF prefix).
        byte[] rest = Encoding.UTF8.GetBytes("data: {\"n\":1}\n");
        byte[] body = [0xEF, 0xBB, 0xBF, .. rest];

        List<string> lines = await PumpAsync(body);

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("data: {\"n\":1}");
    }

    /// <summary>
    ///     Cold-path capture handler: every served line, as a materialised
    ///     string. Pump contract (<c>SseSpanLineHandler</c>): <c>true</c>
    ///     keeps streaming, <c>false</c> stops — so with no sentinel the walk
    ///     continues over every line, and with one it stops exactly on the
    ///     sentinel line (captured, tail unserved).
    /// </summary>
    private sealed class CaptureHandler(string? stopWhen = null)
    {
        public List<string> Captured { get; } = [];

        public Task<bool> Handle(ReadOnlySpan<char> line, CancellationToken ct)
        {
            string s = new string(line);
            Captured.Add(s);
            return Task.FromResult(stopWhen is null || !string.Equals(s, stopWhen, StringComparison.Ordinal));
        }
    }

    private static async Task<List<string>> PumpAsync(byte[] body, string? stopWhen = null)
    {
        var capture = new CaptureHandler(stopWhen);
        using var stream = new MemoryStream(body);
        await CompatSsePump.PumpLinesAsync(stream, capture.Handle, CancellationToken.None);
        return capture.Captured;
    }
}
