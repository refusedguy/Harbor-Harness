using System.Diagnostics;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.E2E.Framework;
using Harbor.Registries.Events;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.LoadTests;

/// <summary>
///     Concurrent multi-session load suite (#420): a backend × shape matrix
///     driven through the REAL agent stack (shared <see cref="AgentLoop" />
///     singleton + shared <see cref="InMemoryEventBus" /> + real
///     OpenAI-compatible HTTP client over SSE) against one
///     <see cref="MockLlmServer" /> in echo mode.
/// </summary>
/// <remarks>
///     <para>
///         <b>Matrix.</b> Session shapes (<c>1×1</c>, <c>10×3</c> fast;
///         <c>50×1</c>, <c>100×1</c> behind <c>HARBOR_LOAD=1</c>) crossed with
///         the durable-store axis (<c>memory</c>, <c>jsonl</c>, <c>sqlite</c>).
///         Every leg asserts the same invariants: all runs complete, no
///         EventBus deadlock (every agent start has a matching end), no
///         cross-session message corruption, and every persisted message
///         carries its run's <c>SessionId</c>.
///     </para>
///     <para>
///         <b>Determinism contract:</b> the only pacing mechanism is the
///         refund-on-completion <c>TokenBucketRateLimiter</c> and
///         <see cref="MockLlmServer.SetChunkDelay" /> time dilation. The
///         harness never sleeps on real time.
///     </para>
///     <para>
///         <b>Budgets are relative, never absolute (#998):</b> an absolute
///         wall-clock threshold on a shared CI runner measures the runner, not
///         the code (1.43× spread on an untouched test). The scaling leg below
///         therefore gates only machine-independent COUNTS (admissions double
///         exactly when sessions double, every start has a matching end) and
///         prints both stopwatches report-only: at a ~10 ms leg scale the ratio
///         itself measures the runner (CI run 37956923400 read 9.6 ms vs
///         49.3 ms, ratio 5.16 — filed as #1059 instead of tuned here).
///         The <c>[Timeout]</c> values are liveness tripwires (a deadlock must
///         fail, not hang), not budgets.
///     </para>
///     <para>
///         <b>Opt-in gate:</b> heavy shapes return immediately unless
///         <c>HARBOR_LOAD=1</c>, so the default PR-CI run stays fast; the full
///         matrix runs via the command in <c>tests/Harbor.LoadTests/README.md</c>.
///     </para>
/// </remarks>
[ParallelLimiter<MockServerLimit>]
public sealed class MultiSessionLoadTests
{
    /// <summary>Rounds per scaling leg; the minimum is reported (noise only inflates).</summary>
    private const int ScalingRounds = 3;

    private static bool FullMatrixEnabled =>
        Environment.GetEnvironmentVariable("HARBOR_LOAD") == "1";

    [Arguments(1, 1)]
    [Arguments(10, 3)]
    [Test]
    [Timeout(300_000)]
    public async Task ScenarioMatrix_Fast_CompletesWithoutCorruptionOrDeadlock(int sessions, int agents)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await DriveAndVerifyAsync(sessions, agents, LoadStoreBackend.Memory, bucketCapacity: 6, cts.Token);
    }

    [Arguments(50, 1)]
    [Arguments(100, 1)]
    [Test]
    [Timeout(600_000)]
    public async Task ScenarioMatrix_Heavy_CompletesWithoutCorruptionOrDeadlock(int sessions, int agents)
    {
        if (!FullMatrixEnabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await DriveAndVerifyAsync(sessions, agents, LoadStoreBackend.Memory, bucketCapacity: 6, cts.Token);
    }

    [Arguments("memory")]
    [Arguments("jsonl")]
    [Arguments("sqlite")]
    [Test]
    [Timeout(300_000)]
    public async Task BackendMatrix_Fast_ParityAcrossStores(string backendName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        LoadStoreBackend backend = MultiSessionLoadHarness.ParseBackend(backendName);
        await DriveAndVerifyAsync(4, 1, backend, bucketCapacity: 4, cts.Token);
    }

    [Arguments("jsonl")]
    [Arguments("sqlite")]
    [Test]
    [Timeout(600_000)]
    public async Task BackendMatrix_Heavy_FiftySessionsPerFileBackend(string backendName)
    {
        if (!FullMatrixEnabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        LoadStoreBackend backend = MultiSessionLoadHarness.ParseBackend(backendName);
        await DriveAndVerifyAsync(50, 1, backend, bucketCapacity: 6, cts.Token);
    }

    [Arguments(1)]
    [Arguments(4)]
    [Test]
    [Timeout(300_000)]
    public async Task RateLimiter_CapacityLadder_AdmitsAllDropsNone(int capacity)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await DriveAndVerifyAsync(4, 1, LoadStoreBackend.Memory, capacity, cts.Token);
    }

    [Test]
    [Timeout(300_000)]
    public async Task CapacityOne_StrictlySerializesStreams()
    {
        const int sessions = 2;
        const int agents = 2;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agents, bucketCapacity: 1, TimeSpan.FromMilliseconds(1));
        await harness.CreateSessionsAsync(sessions, cts.Token);

        SessionRunResult[] results = await harness.RunAllAsync(cts.Token);

        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agents);
            await Assert.That(result.Errors).IsEmpty();
        }

        // A single-token bucket can never admit a second concurrent stream:
        // at most one in flight, and every run admitted exactly once.
        await Assert.That(harness.Limiter.PeakInFlight).IsEqualTo(1);
        await Assert.That(harness.Limiter.TotalAdmissions).IsEqualTo(sessions * agents);

        // Serialized runs still produce intact, correctly-owned transcripts.
        await AssertSessionsNotCorruptedAsync(harness, agents);
    }

    [Test]
    [Timeout(300_000)]
    public async Task EventBus_TurnOrdering_NoBleedNoReorder()
    {
        const int sessions = 6;
        const int agents = 2;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agents, bucketCapacity: 6, TimeSpan.FromMilliseconds(1));
        await harness.CreateSessionsAsync(sessions, cts.Token);

        var recorder = new TurnOrderRecorder(harness.Contexts.Select(c => c.Session.Id));
        using IDisposable _ = harness.Bus.Subscribe((AgentEvent evt, CancellationToken _) =>
        {
            recorder.OnEvent(evt);
            return ValueTask.CompletedTask;
        });

        SessionRunResult[] results = await harness.RunAllAsync(cts.Token);

        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agents);
            await Assert.That(result.Errors).IsEmpty();
        }

        await recorder.AssertBalancedAndOrderedAsync();
        await AssertSessionsNotCorruptedAsync(harness, agents);
    }

    /// <summary>
    ///     Relative scaling leg (#420, #998 pattern): 8 sessions against 4 in
    ///     the same run. The GATE is machine-independent counts — doubling the
    ///     sessions exactly doubles admissions/starts/ends with transcripts
    ///     intact (a dropped run or a starved admission fails here on any
    ///     runner). Stopwatches are printed report-only with every round, not
    ///     just the best: at ~10 ms leg scale a best-of-3 ratio hides variance,
    ///     and variance is the noise diagnostic (CI run 37956923400 read
    ///     ratio 5.16 — see #1059). Per-round peaks are printed alongside so
    ///     limiter queueing (8 runs vs capacity 6) reads separately from
    ///     runner jitter. Allocation is deliberately NOT gated either — the
    ///     drive crosses async/HTTP boundaries, so per-thread allocated
    ///     bytes are unsound.
    /// </summary>
    [Test]
    [Timeout(600_000)]
    public async Task SessionCountScaling_PairedSameRun_CountsScaleLinearly()
    {
        const int smallSessions = 4;
        const int largeSessions = 8;
        const int agents = 1;
        const int capacity = 6;

        // Warm-up drive, discarded: pays tiered-JIT compilation so it lands
        // in neither leg.
        await MeasureDriveAsync(smallSessions, agents, capacity);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        (int smallAdmissions, double smallMs) = (0, double.MaxValue);
        var smallRounds = new List<double>(ScalingRounds);
        int smallPeak = 0;
        for (int r = 0; r < ScalingRounds; r++)
        {
            (int admissions, double ms, int peak) = await MeasureDriveAsync(smallSessions, agents, capacity);
            await Assert.That(admissions).IsEqualTo(smallSessions * agents);
            smallAdmissions = admissions;
            smallMs = Math.Min(smallMs, ms);
            smallRounds.Add(ms);
            smallPeak = Math.Max(smallPeak, peak);
        }

        (int largeAdmissions, double largeMs) = (0, double.MaxValue);
        var largeRounds = new List<double>(ScalingRounds);
        int largePeak = 0;
        for (int r = 0; r < ScalingRounds; r++)
        {
            (int admissions, double ms, int peak) = await MeasureDriveAsync(largeSessions, agents, capacity);
            await Assert.That(admissions).IsEqualTo(largeSessions * agents);
            largeAdmissions = admissions;
            largeMs = Math.Min(largeMs, ms);
            largeRounds.Add(ms);
            largePeak = Math.Max(largePeak, peak);
        }

        Console.WriteLine(
            $"[scaling] {smallSessions}x{agents}: {smallMs:F1} ms best of {ScalingRounds} " +
            $"(rounds {string.Join(", ", smallRounds.Select(v => v.ToString("F1")))}; peak {smallPeak}); " +
            $"{largeSessions}x{agents}: {largeMs:F1} ms best of {ScalingRounds} " +
            $"(rounds {string.Join(", ", largeRounds.Select(v => v.ToString("F1")))}; peak {largePeak}) " +
            $"(report-only; counts gated, see #1059)");

        // Non-vacuity first: zero admissions satisfy every equality below.
        await Assert.That(smallAdmissions).IsEqualTo(smallSessions * agents);
        await Assert.That(largeAdmissions).IsEqualTo(largeSessions * agents);

        // Linear count scaling: twice the sessions, exactly twice the admitted work.
        await Assert.That(largeAdmissions).IsEqualTo(2 * smallAdmissions);
    }

    /// <summary>
    ///     One full drive plus every machine-independent invariant: counts
    ///     (admissions, starts/ends, transcript shape) hold on any runner.
    /// </summary>
    private static async Task DriveAndVerifyAsync(
        int sessions,
        int agents,
        LoadStoreBackend backend,
        int bucketCapacity,
        CancellationToken ct)
    {
        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agents, bucketCapacity, TimeSpan.FromMilliseconds(1), backend);

        // The leg actually ran on the backend the matrix asked for.
        await Assert.That(harness.Backend).IsEqualTo(backend);

        await harness.CreateSessionsAsync(sessions, ct);
        SessionRunResult[] results = await harness.RunAllAsync(ct);

        int total = sessions * agents;
        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agents);
            await Assert.That(result.Errors).IsEmpty();
        }

        // No EventBus deadlock: every agent start has a matching end.
        await Assert.That(harness.Signals.AgentStarts).IsEqualTo(total);
        await Assert.That(harness.Signals.AgentEnds).IsEqualTo(total);

        // No UiStore reducer threw under concurrent event streams.
        await Assert.That(harness.Signals.DispatchErrors).IsEmpty();

        // The token bucket shaped the load exactly as configured: every run
        // admitted exactly once (no drop, no starvation), never over capacity.
        // For capacity 1 this pins PeakInFlight to exactly 1 (at least one
        // admission happened, at most one was ever in flight).
        await Assert.That(harness.Limiter.TotalAdmissions).IsEqualTo(total);
        await Assert.That(harness.Limiter.PeakInFlight).IsLessThanOrEqualTo(bucketCapacity);

        await AssertSessionsNotCorruptedAsync(harness, agents);
        await AssertUiStoresConvergedAsync(harness, sessions, total);
    }

    /// <summary>
    ///     One timed drive; also verifies completion so a slow leg cannot be
    ///     a leg that silently dropped runs. Returns admitted-run count plus
    ///     wall-clock milliseconds plus limiter peak (timing and peak are
    ///     report-only, never gated).
    /// </summary>
    private static async Task<(int Admissions, double ElapsedMs, int PeakInFlight)> MeasureDriveAsync(int sessions, int agents, int capacity)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using MultiSessionLoadHarness harness = await MultiSessionLoadHarness.StartAsync(
            sessions, agents, capacity, TimeSpan.FromMilliseconds(1), LoadStoreBackend.Memory);
        await harness.CreateSessionsAsync(sessions, cts.Token);

        var stopwatch = Stopwatch.StartNew();
        SessionRunResult[] results = await harness.RunAllAsync(cts.Token);
        stopwatch.Stop();

        int total = sessions * agents;
        foreach (SessionRunResult result in results)
        {
            await Assert.That(result.SucceededRuns).IsEqualTo(agents);
            await Assert.That(result.Errors).IsEmpty();
        }

        await Assert.That(harness.Signals.AgentStarts).IsEqualTo(total);
        await Assert.That(harness.Signals.AgentEnds).IsEqualTo(total);

        return (harness.Limiter.TotalAdmissions, stopwatch.Elapsed.TotalMilliseconds, harness.Limiter.PeakInFlight);
    }

    /// <summary>
    ///     Corruption check: the PERSISTED transcript (read straight from the
    ///     store, independently of the in-memory contexts) contains exactly one
    ///     alternating user/assistant pair per run, every assistant text
    ///     byte-matches the echo hash of the preceding prompt, and every
    ///     message carries its run's <c>SessionId</c> (no cross-session bleed).
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

                // Isolation: a message persisted under session S belongs to S.
                await Assert.That(user.SessionId).IsEqualTo(ctx.Session.Id);
                await Assert.That(assistant.SessionId).IsEqualTo(ctx.Session.Id);

                string prompt = ((UserMessage)user).Content;
                string text = AssistantText(assistant);
                await Assert.That(text).IsEqualTo(LoadTestFakes.ExpectedEcho(prompt));
            }
        }
    }

    /// <summary>
    ///     Every UiStore absorbed the shared event stream through concurrent
    ///     reducer dispatches and drained fully: idle, not streaming, holding
    ///     a bounded transcript. Line counts and cross-store equality are
    ///     deliberately not asserted: concurrent pipelines publish interleaved,
    ///     so stores legitimately observe different event orders. Transcript
    ///     integrity is asserted separately from the store (above).
    /// </summary>
    private static async Task AssertUiStoresConvergedAsync(
        MultiSessionLoadHarness harness,
        int sessions,
        int totalRuns)
    {
        await Assert.That(harness.Stores.Count).IsEqualTo(sessions);

        foreach (UiStore store in harness.Stores)
        {
            UiState state = store.State;
            await Assert.That(state.Chat.IsStreaming).IsFalse();
            await Assert.That(state.Chat.IsAgentRunning).IsFalse();
            await Assert.That(state.Chat.Status).IsEqualTo("idle");
            await Assert.That(state.Chat.Lines.Length).IsGreaterThanOrEqualTo(1);
            await Assert.That(state.Chat.Lines.Length).IsLessThanOrEqualTo(totalRuns * 2);
        }
    }

    /// <summary>
    ///     Thread-safe recorder of turn/message ordering on the shared bus.
    ///     The bus fans every session's events to every subscriber, so this is
    ///     the regression surface for cross-session bleed: an event carrying
    ///     another session's (or no) <c>SessionId</c>, or a <c>TurnEndEvent</c>
    ///     with no open <c>TurnStartEvent</c>, fails the leg.
    /// </summary>
    private sealed class TurnOrderRecorder
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _known;
        private readonly Dictionary<string, int> _openTurns = new();
        private readonly List<string> _violations = new();
        private int _turnStarts;
        private int _turnEnds;

        public TurnOrderRecorder(IEnumerable<string> knownSessionIds)
        {
            _known = new HashSet<string>(knownSessionIds);
        }

        public void OnEvent(AgentEvent evt)
        {
            lock (_gate)
            {
                switch (evt)
                {
                    case TurnStartEvent start:
                        _turnStarts++;
                        CheckMember(start.SessionId, nameof(TurnStartEvent));
                        if (start.SessionId is not null)
                        {
                            _openTurns[start.SessionId] = _openTurns.GetValueOrDefault(start.SessionId) + 1;
                        }

                        break;
                    case TurnEndEvent end:
                        _turnEnds++;
                        CheckMember(end.SessionId, nameof(TurnEndEvent));
                        if (end.SessionId is not null)
                        {
                            int open = _openTurns.GetValueOrDefault(end.SessionId);
                            if (open == 0)
                            {
                                _violations.Add($"TurnEnd with no open TurnStart for session {end.SessionId}");
                            }
                            else
                            {
                                _openTurns[end.SessionId] = open - 1;
                            }
                        }

                        break;
                    case MessageStartEvent messageStart:
                        CheckMember(messageStart.Message.SessionId, nameof(MessageStartEvent));
                        break;
                    case MessageEndEvent messageEnd:
                        CheckMember(messageEnd.Message.SessionId, nameof(MessageEndEvent));
                        break;
                }
            }
        }

        public async Task AssertBalancedAndOrderedAsync()
        {
            List<string> violations;
            List<string> leaking;
            int starts;
            int ends;
            lock (_gate)
            {
                violations = new List<string>(_violations);
                leaking = _openTurns
                    .Where(kv => kv.Value != 0)
                    .Select(kv => kv.Key + ":" + kv.Value)
                    .ToList();
                starts = _turnStarts;
                ends = _turnEnds;
            }

            await Assert.That(violations).IsEmpty();
            await Assert.That(leaking).IsEmpty();

            // Non-vacuity: a recorder that saw nothing balances trivially.
            await Assert.That(starts > 0).IsTrue();
            await Assert.That(starts).IsEqualTo(ends);
        }

        private void CheckMember(string? sessionId, string kind)
        {
            if (sessionId is null)
            {
                _violations.Add($"{kind} with null SessionId");
            }
            else if (!_known.Contains(sessionId))
            {
                _violations.Add($"{kind} for unknown session {sessionId}");
            }
        }
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
}
