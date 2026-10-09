using System.Diagnostics;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.E2E.Framework;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using Harbor.Registries.Events;

namespace Harbor.LoadTests;

/// <summary>
///     Concurrent multi-session load suite (sprint Testing Strategy Z.3, #420):
///     sessions × agents driven through the REAL agent stack (shared
///     AgentLoop + shared InMemoryEventBus + real OpenAI-compatible HTTP
///     client over SSE) against one <see cref="MockLlmServer" /> in echo mode,
///     on every <see cref="StoreBackend" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Determinism contract:</b> the only pacing is the refund-on-completion
///         <c>TokenBucketRateLimiter</c> and <see cref="MockLlmServer.SetChunkDelay" />
///         time dilation — the harness never sleeps on real time.
///     </para>
///     <para>
///         <b>Budget policy (#996, #998):</b> no test asserts an absolute
///         wall-clock threshold — on shared CI hardware the same commit
///         measured a 1.43× spread, so a millisecond budget tests the runner,
///         not the code. Scaling is gated by a same-run paired ratio
///         (<see cref="Scaling_DoublingSessions_StaysSubQuadratic" />) and by
///         machine-independent counts everywhere else. The
///         <c>CancellationTokenSource</c> bounds below are liveness tripwires
///         (a deadlocked run must fail, not hang the lane), deliberately
///         generous, never perf gates.
///     </para>
///     <para>
///         <b>Lanes:</b> the default run executes only the fast matrix. Shapes
///         of 50+ sessions need <c>HARBOR_LOAD=1</c> (nightly/manual lane,
///         see <c>tests/Harbor.LoadTests/README.md</c>).
///     </para>
/// </remarks>
[ParallelLimiter<MockServerLimit>]
public sealed class MultiSessionLoadTests
{
    private const int Sessions = 10;
    private const int AgentsPerSession = 3;
    private const int BucketCapacity = 6;
    private const int TotalRuns = Sessions * AgentsPerSession;
    private const int MemoryBudgetBytes = 200 * 1024 * 1024;

    /// <summary>
    ///     Liveness tripwire for fast shapes, not a perf gate: generous on
    ///     purpose, exists so a deadlock fails instead of hanging the lane.
    /// </summary>
    private static readonly TimeSpan FastLivenessBound = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Liveness tripwire for heavy (HARBOR_LOAD=1) shapes, not a perf
    ///     gate: 100 sessions on a file-backed store must finish long before
    ///     this; only a deadlock or a stall should ever observe it.
    /// </summary>
    private static readonly TimeSpan HeavyLivenessBound = TimeSpan.FromMinutes(10);

    [Test]
    public async Task TenSessions_ThreeAgents_EchoRunsComplete_NoCorruptionNoDeadlock()
    {
        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            Sessions, AgentsPerSession, BucketCapacity, TimeSpan.FromMilliseconds(2));
        await harness.CreateSessionsAsync(Sessions);

        SessionRunResult[] results = await harness.RunAllAsync();

        // Every run of every session succeeded.
        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(AgentsPerSession);
            await Assert.That(result.Errors).IsEmpty();
        }

        // No EventBus deadlock: every agent start has a matching end.
        await Assert.That(harness.Signals.AgentStarts).IsEqualTo(TotalRuns);
        await Assert.That(harness.Signals.AgentEnds).IsEqualTo(TotalRuns);

        // No UiStore reducer threw under concurrent event streams.
        await Assert.That(harness.Signals.DispatchErrors).IsEmpty();

        // The token bucket shaped the load exactly as configured.
        await Assert.That(harness.Limiter.PeakInFlight).IsLessThanOrEqualTo(BucketCapacity);
        await Assert.That(harness.Limiter.TotalAdmissions).IsEqualTo(TotalRuns);

        await AssertSessionsNotCorruptedAsync(harness, AgentsPerSession);
        await AssertUiStoresConvergedAsync(harness);
        await AssertMemoryBudgetAsync();
    }

    [Test]
    public async Task CapacityOne_StrictlySerializesStreams()
    {
        const int sessions = 2;
        const int agents = 2;

        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agents, bucketCapacity: 1, TimeSpan.FromMilliseconds(1));
        await harness.CreateSessionsAsync(sessions);

        SessionRunResult[] results = await harness.RunAllAsync();

        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agents);
            await Assert.That(result.Errors).IsEmpty();
        }

        // A single-token bucket can never admit a second concurrent stream.
        await Assert.That(harness.Limiter.PeakInFlight).IsEqualTo(1);
        await Assert.That(harness.Limiter.TotalAdmissions).IsEqualTo(sessions * agents);

        // Serialized runs still produce intact transcripts.
        await AssertSessionsNotCorruptedAsync(harness, agents);
    }

    /// <summary>
    ///     Fast scenario matrix: every shape completes with intact
    ///     transcripts, no deadlock, no cross-session bleed, and ordered
    ///     per-session turn streams. Default lane (no HARBOR_LOAD needed).
    /// </summary>
    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 1)]
    [Arguments(4, 2)]
    [Arguments(10, 3)]
    public async Task MatrixShape_CompletesWithoutCorruptionOrDeadlock(int sessions, int agentsPerSession)
    {
        int bucket = Math.Min(6, sessions * agentsPerSession);
        await using MultiSessionLoadHarness harness =
            await StartShapeAsync(sessions, agentsPerSession, bucket, StoreBackend.Memory);
        using var cts = new CancellationTokenSource(FastLivenessBound);

        await AssertShapeCompletedAsync(harness, agentsPerSession, cts.Token);
        await AssertTurnOrderingAsync(harness, agentsPerSession);
    }

    /// <summary>
    ///     Heavy shapes (nightly/manual lane, <c>HARBOR_LOAD=1</c>): 50 and
    ///     100 concurrent sessions complete with intact transcripts. The
    ///     <c>CancellationTokenSource</c> bound is a liveness tripwire, not a
    ///     perf budget — there is deliberately no wall-clock assertion here
    ///     (#996: absolute thresholds on shared runners test the runner).
    /// </summary>
    [Test]
    [SkipUnlessHarborLoad]
    [Arguments(50, 1)]
    [Arguments(100, 1)]
    public async Task HeavyShape_CompletesWithoutCorruptionOrDeadlock(int sessions, int agentsPerSession)
    {
        await using MultiSessionLoadHarness harness =
            await StartShapeAsync(sessions, agentsPerSession, bucketCapacity: 16, StoreBackend.Memory);
        using var cts = new CancellationTokenSource(HeavyLivenessBound);

        await AssertShapeCompletedAsync(harness, agentsPerSession, cts.Token);
        await AssertTurnOrderingAsync(harness, agentsPerSession);
    }

    /// <summary>
    ///     Store-backend matrix, fast shapes: the same concurrent pipelines
    ///     run against memory, JSONL and SQLite so write contention and
    ///     file-handle pressure are covered, not just the memory path.
    ///     Each row owns an isolated temp path (see the harness), so rows
    ///     never share files.
    /// </summary>
    [Test]
    [Arguments("memory", 2)]
    [Arguments("memory", 4)]
    [Arguments("jsonl", 2)]
    [Arguments("jsonl", 4)]
    [Arguments("sqlite", 2)]
    [Arguments("sqlite", 4)]
    public async Task StoreBackend_CompletesWithoutCorruption(string backendName, int sessions)
    {
        var backend = Enum.Parse<StoreBackend>(backendName, ignoreCase: true);
        const int agentsPerSession = 1;
        await using MultiSessionLoadHarness harness =
            await StartShapeAsync(sessions, agentsPerSession, Math.Min(6, sessions), backend);
        using var cts = new CancellationTokenSource(FastLivenessBound);

        await AssertShapeCompletedAsync(harness, agentsPerSession, cts.Token);
    }

    /// <summary>
    ///     Store-backend matrix, heavy shape (nightly/manual lane,
    ///     <c>HARBOR_LOAD=1</c>): 50 concurrent sessions per backend.
    ///     Same liveness-not-perf contract as
    ///     <see cref="HeavyShape_CompletesWithoutCorruptionOrDeadlock" />.
    /// </summary>
    [Test]
    [SkipUnlessHarborLoad]
    [Arguments("memory")]
    [Arguments("jsonl")]
    [Arguments("sqlite")]
    public async Task StoreBackend_HeavyShape_CompletesWithoutCorruption(string backendName)
    {
        var backend = Enum.Parse<StoreBackend>(backendName, ignoreCase: true);
        const int sessions = 50;
        const int agentsPerSession = 1;
        await using MultiSessionLoadHarness harness =
            await StartShapeAsync(sessions, agentsPerSession, bucketCapacity: 16, backend);
        using var cts = new CancellationTokenSource(HeavyLivenessBound);

        await AssertShapeCompletedAsync(harness, agentsPerSession, cts.Token);
    }

    /// <summary>
    ///     Rate-limiter capacity ladder: capacity 1 strictly serializes
    ///     (peak stays 1); capacity equal to the run count admits everything
    ///     with no drops (<c>TotalAdmissions</c> equals the run count) and no
    ///     starvation (every run completes inside the liveness bound).
    ///     No lower bound on the unshaped peak: it is scheduling-dependent
    ///     and asserting one would gate on runner timing.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(8)]
    public async Task RateLimiter_CapacityLadder_AdmitsAllRunsWithoutDrops(int capacity)
    {
        const int sessions = 4;
        const int agentsPerSession = 2;
        await using MultiSessionLoadHarness harness =
            await StartShapeAsync(sessions, agentsPerSession, capacity, StoreBackend.Memory);
        using var cts = new CancellationTokenSource(FastLivenessBound);

        await AssertShapeCompletedAsync(harness, agentsPerSession, cts.Token);
        await Assert.That(harness.Limiter.PeakInFlight).IsLessThanOrEqualTo(capacity);
        if (capacity == 1)
        {
            await Assert.That(harness.Limiter.PeakInFlight).IsEqualTo(1);
        }
    }

    /// <summary>
    ///     Event-bus ordering under interleaving: per-session turn streams
    ///     strictly alternate start/end (runs within a session are
    ///     sequential), no <c>TurnEndEvent</c> precedes its
    ///     <c>TurnStartEvent</c>, every finalized message was announced by
    ///     id, and no event carries a foreign <c>SessionId</c>. This is the
    ///     regression surface for per-session routing (R25–R26) and for the
    ///     §OOP-001 class — singleton mutable state shared across sessions.
    /// </summary>
    [Test]
    [Arguments(2, 1)]
    [Arguments(6, 2)]
    public async Task EventBus_PerSessionOrderingHoldsUnderInterleaving(int sessions, int agentsPerSession)
    {
        int bucket = Math.Min(4, sessions * agentsPerSession);
        await using MultiSessionLoadHarness harness =
            await StartShapeAsync(sessions, agentsPerSession, bucket, StoreBackend.Memory);
        using var cts = new CancellationTokenSource(FastLivenessBound);

        await AssertShapeCompletedAsync(harness, agentsPerSession, cts.Token);
        await AssertTurnOrderingAsync(harness, agentsPerSession);
        await AssertMessagesPairedAsync(harness);
    }

    /// <summary>
    ///     Paired same-run scaling gate (#998 pattern for #996 site 2, which
    ///     was the absolute 60s suite budget this replaces): run N then 2N
    ///     sessions back to back in one test and bound the ratio.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>t(2N) &lt;= 3 · t(N)</c>: linear scaling is 2.00, quadratic
    ///         is 4.00, so 3 discriminates without reddening on a correct
    ///         tree when one leg takes a GC the other did not (the #939
    ///         shape) and without admitting the O(N²) the gate exists to
    ///         catch. Fixed per-leg costs (mock-server bind, store setup) sit
    ///         in both operands and bias the ratio toward 1 — the
    ///         conservative direction. The runner's speed is in both operands
    ///         and cancels, which is the whole point versus an absolute
    ///         budget.
    ///     </para>
    ///     <para>
    ///         The load-bearing half is machine-independent counts: admissions
    ///         double exactly with the input. A broken limiter or a dropped
    ///         run moves the counts on any hardware.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Scaling_DoublingSessions_StaysSubQuadratic()
    {
        const int smallSessions = 8;
        const int agentsPerSession = 1;

        TimeSpan legN = await MeasureShapeAsync(smallSessions, agentsPerSession);
        TimeSpan leg2N = await MeasureShapeAsync(smallSessions * 2, agentsPerSession);

        double ratio = leg2N.TotalMilliseconds / Math.Max(legN.TotalMilliseconds, 1.0);
        Console.WriteLine(
            $"[load-scale] N={smallSessions} took {legN.TotalSeconds:F2}s, "
            + $"2N took {leg2N.TotalSeconds:F2}s, ratio {ratio:F2} (bound 3.0)");

        await Assert.That(ratio).IsLessThanOrEqualTo(3.0);
    }

    /// <summary>
    ///     Start a harness and create its sessions (one statement per test).
    /// </summary>
    private static async Task<MultiSessionLoadHarness> StartShapeAsync(
        int sessions,
        int agentsPerSession,
        int bucketCapacity,
        StoreBackend backend)
    {
        MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agentsPerSession, bucketCapacity, TimeSpan.FromMilliseconds(1), backend);
        await harness.CreateSessionsAsync(sessions);
        return harness;
    }

    /// <summary>
    ///     The shared completion contract every matrix row asserts: all runs
    ///     succeeded, starts match ends, no dispatch threw, the limiter
    ///     admitted exactly the run count (no drops), transcripts are
    ///     intact, and no event bled across sessions.
    /// </summary>
    private static async Task AssertShapeCompletedAsync(
        MultiSessionLoadHarness harness,
        int agentsPerSession,
        CancellationToken ct)
    {
        SessionRunResult[] results = await harness.RunAllAsync(ct);

        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agentsPerSession);
            await Assert.That(result.Errors).IsEmpty();
        }

        int totalRuns = results.Length * agentsPerSession;
        await Assert.That(harness.Signals.AgentStarts).IsEqualTo(totalRuns);
        await Assert.That(harness.Signals.AgentEnds).IsEqualTo(totalRuns);
        await Assert.That(harness.Signals.DispatchErrors).IsEmpty();
        await Assert.That(harness.Limiter.TotalAdmissions).IsEqualTo(totalRuns);

        await AssertSessionsNotCorruptedAsync(harness, agentsPerSession);
        await AssertUiStoresDrainedAsync(harness);
        await AssertNoCrossSessionBleedAsync(harness);
    }

    /// <summary>
    ///     Run one shape and return its wall time. Both scaling legs use a
    ///     wide-open bucket (capacity = sessions) so the limiter never
    ///     shapes either leg — the ratio measures the system, not the gate.
    /// </summary>
    private static async Task<TimeSpan> MeasureShapeAsync(int sessions, int agentsPerSession)
    {
        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agentsPerSession, bucketCapacity: sessions, TimeSpan.FromMilliseconds(1));
        await harness.CreateSessionsAsync(sessions);

        var stopwatch = Stopwatch.StartNew();
        SessionRunResult[] results = await harness.RunAllAsync();
        stopwatch.Stop();

        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agentsPerSession);
            await Assert.That(result.Errors).IsEmpty();
        }

        await Assert.That(harness.Limiter.TotalAdmissions).IsEqualTo(sessions * agentsPerSession);
        return stopwatch.Elapsed;
    }

    /// <summary>
    ///     Cross-session isolation: every session-attributed event observed
    ///     on the shared bus carries a <c>SessionId</c> created by this run.
    ///     A foreign or missing id (turns are always attributed by
    ///     TurnRunner — a null here is a regression, not noise) fails.
    /// </summary>
    private static async Task AssertNoCrossSessionBleedAsync(MultiSessionLoadHarness harness)
    {
        var ids = new HashSet<string>(harness.Contexts.Select(ctx => ctx.Session.Id));

        foreach (AgentEvent evt in harness.Signals.RecordedEvents)
        {
            string? sessionId = evt switch
            {
                AgentStartEvent start => start.SessionId,
                TurnStartEvent start => start.SessionId,
                TurnEndEvent end => end.SessionId,
                MessageStartEvent start => start.Message.SessionId,
                MessageEndEvent end => end.Message.SessionId,
                _ => null,
            };

            // AgentEndEvent carries no SessionId by contract (matched to
            // starts by count); MessageUpdateEvent deltas are excluded —
            // their Partial is mid-stream and not reliably attributed.
            if (evt is AgentEndEvent or MessageUpdateEvent)
            {
                continue;
            }

            await Assert.That(sessionId is not null && ids.Contains(sessionId)).IsTrue();
        }
    }

    /// <summary>
    ///     Per-session turn ordering: runs within a session are sequential,
    ///     so each session's turn stream must strictly alternate start/end —
    ///     at most one open turn, never negative, fully drained, and every
    ///     run turned at least once. Interleaving ACROSS sessions is expected
    ///     and is what this holds under.
    /// </summary>
    private static async Task AssertTurnOrderingAsync(MultiSessionLoadHarness harness, int agentsPerSession)
    {
        var open = new Dictionary<string, int>();
        var starts = new Dictionary<string, int>();
        var ends = new Dictionary<string, int>();

        foreach (AgentEvent evt in harness.Signals.RecordedEvents)
        {
            if (evt is TurnStartEvent start && start.SessionId is not null)
            {
                open[start.SessionId] = open.GetValueOrDefault(start.SessionId) + 1;
                starts[start.SessionId] = starts.GetValueOrDefault(start.SessionId) + 1;
                await Assert.That(open[start.SessionId]).IsLessThanOrEqualTo(1);
            }
            else if (evt is TurnEndEvent end && end.SessionId is not null)
            {
                open[end.SessionId] = open.GetValueOrDefault(end.SessionId) - 1;
                ends[end.SessionId] = ends.GetValueOrDefault(end.SessionId) + 1;
                await Assert.That(open[end.SessionId]).IsGreaterThanOrEqualTo(0);
            }
        }

        foreach (LoadSessionContext ctx in harness.Contexts)
        {
            string id = ctx.Session.Id;
            await Assert.That(starts.GetValueOrDefault(id)).IsGreaterThanOrEqualTo(agentsPerSession);
            await Assert.That(ends.GetValueOrDefault(id)).IsEqualTo(starts.GetValueOrDefault(id));
            await Assert.That(open.GetValueOrDefault(id)).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     Every finalized assistant message was announced by message id,
    ///     and announcements match finalizations one to one — no lost or
    ///     duplicated message on the shared bus.
    /// </summary>
    private static async Task AssertMessagesPairedAsync(MultiSessionLoadHarness harness)
    {
        var announced = new HashSet<string>();
        var finalized = new HashSet<string>();

        foreach (AgentEvent evt in harness.Signals.RecordedEvents)
        {
            if (evt is MessageStartEvent start)
            {
                announced.Add(start.Message.Id);
            }
            else if (evt is MessageEndEvent end)
            {
                finalized.Add(end.Message.Id);
            }
        }

        await Assert.That(announced.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(finalized.Count).IsEqualTo(announced.Count);
        foreach (string id in finalized)
        {
            await Assert.That(announced.Contains(id)).IsTrue();
        }
    }

    /// <summary>
    ///     Corruption check: the PERSISTED transcript (read straight from the
    ///     store, independently of the in-memory contexts) contains exactly one
    ///     alternating user/assistant pair per run, and every assistant text
    ///     byte-matches the echo hash of the preceding prompt.
    /// </summary>
    private static async Task AssertSessionsNotCorruptedAsync(MultiSessionLoadHarness harness, int runsPerSession)
    {
        foreach (LoadSessionContext ctx in harness.Contexts)
        {
            Result<IReadOnlyList<AgentMessage>> stored =
                await harness.ReadStoredAsync(ctx.Session.Id);
            await Assert.That(stored.IsSuccess).IsTrue();

            IReadOnlyList<AgentMessage> messages = stored.Value;
            await Assert.That(messages.Count).IsEqualTo(runsPerSession * 2);

            for (int i = 0; i < runsPerSession; i++)
            {
                AgentMessage user = messages[i * 2];
                AgentMessage assistant = messages[(i * 2) + 1];

                await Assert.That(user.Role).IsEqualTo("user");
                await Assert.That(assistant.Role).IsEqualTo("assistant");

                string prompt = ((UserMessage)user).Content;
                string text = AssistantText(assistant);
                await Assert.That(text).IsEqualTo(LoadTestFakes.ExpectedEcho(prompt));
            }
        }
    }

    /// <summary>
    ///     All 10 UiStores absorb the same shared event stream through
    ///     concurrent reducer dispatches. The bus delivers every event to
    ///     every subscriber (each fan-out awaits its handler; the 250ms
    ///     per-handler budget dwarfs our microsecond dispatches), so after the
    ///     run EVERY store must be fully drained: idle, not streaming, and
    ///     holding a bounded transcript. A lost or raced dispatch would leave
    ///     a store stuck in "running" or make the reducer throw.
    ///
    ///     Line counts and cross-store equality are deliberately not asserted:
    ///     the 10 concurrent session pipelines publish interleaved, so stores
    ///     legitimately observe different event ORDERS, and the Active-message
    ///     buffer folds differently per order. Transcript integrity is asserted
    ///     separately from the store (corruption check above).
    /// </summary>
    private static async Task AssertUiStoresConvergedAsync(MultiSessionLoadHarness harness)
    {
        await Assert.That(harness.Stores.Count).IsEqualTo(Sessions);

        foreach (UiStore store in harness.Stores)
        {
            UiState state = store.State;
            await Assert.That(state.Chat.IsStreaming).IsFalse();
            await Assert.That(state.Chat.IsAgentRunning).IsFalse();
            await Assert.That(state.Chat.Status).IsEqualTo("idle");
            await Assert.That(state.Chat.Lines.Length).IsGreaterThanOrEqualTo(1);
            await Assert.That(state.Chat.Lines.Length).IsLessThanOrEqualTo(TotalRuns * 2);
        }
    }

    /// <summary>
    ///     Shape-agnostic drain check for matrix rows: every UiStore is idle
    ///     and holds at least one line. (The strict line-count bound lives in
    ///     <see cref="AssertUiStoresConvergedAsync" />, which is calibrated to
    ///     the canonical 10×3 shape.)
    /// </summary>
    private static async Task AssertUiStoresDrainedAsync(MultiSessionLoadHarness harness)
    {
        await Assert.That(harness.Stores.Count).IsGreaterThanOrEqualTo(1);

        foreach (UiStore store in harness.Stores)
        {
            UiState state = store.State;
            await Assert.That(state.Chat.IsStreaming).IsFalse();
            await Assert.That(state.Chat.IsAgentRunning).IsFalse();
            await Assert.That(state.Chat.Status).IsEqualTo("idle");
            await Assert.That(state.Chat.Lines.Length).IsGreaterThanOrEqualTo(1);
        }
    }

    private static async Task AssertMemoryBudgetAsync()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
        if (privateBytes <= MemoryBudgetBytes) return;

        // Fail with the actual number instead of a bare comparison —
        // makes regressions diagnosable from CI logs alone. The hard gate
        // is opt-in (HARBOR_MEM_BUDGET_STRICT=1, dedicated perf hardware):
        // shared CI runners carry a different runtime baseline, so there
        // the breach is report-only noise, mirroring the perf-gate
        // HARBOR_PERF_BASELINE_STRICT convention.
        if (Environment.GetEnvironmentVariable("HARBOR_MEM_BUDGET_STRICT") == "1")
        {
            await Assert.That(FormatMb(privateBytes)).IsEqualTo(FormatMb(MemoryBudgetBytes));
        }

        Console.WriteLine(
            $"[mem] {FormatMb(privateBytes)} > budget {FormatMb(MemoryBudgetBytes)} " +
            "(report-only on shared runners; set HARBOR_MEM_BUDGET_STRICT=1 to enforce)");
    }

    private static string AssistantText(AgentMessage message)
    {
        var sb = new StringBuilder();
        foreach (ContentPart part in ((AssistantMessage)message).Parts)
        {
            if (part is TextPart text)
            {
                sb.Append(text.Text);
            }
        }

        return sb.ToString();
    }

    private static string FormatMb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
