using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Harbor.TestKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

// SqliteAppendCostGate — #1534 (hot-path slice of #97, per append), REWRITTEN
// by #1747.
//
// WHAT THE SLICE WAS
// ------------------
// Every appended row used to pay 2x `ToString("O")` (the message stamp plus
// the session.updated_at touch) and one AddWithValue per parameter, with each
// value-type arg (`int` status/kind, `long` created_at_ms) boxing and having
// its SqliteType inferred from the runtime type on every call.
//
// WHAT THE GATE IS NOW (and why it stopped being a ratio)
// -------------------------------------------------------
// The gate used to be a RELATIVE one: `cost(2N)/cost(N) <= 3` for time and for
// allocations, over N=1024 and 2N=2048 appended messages, decided from
// best-of-3 legs with no GC fence and no leg scaling. It is the gate the
// #1747 inventory names as the receipt for "a ratio depends on the base and
// on the noise": it reddened on a loaded runner at **ratio 3.102 against a
// limit of 3.00** — a 3% overshoot on a byte-identical tree.
//
// A ratio has two defects this rewrite removes:
//
//   * the DENOMINATOR is the same order of magnitude as the numerator, so a
//     scheduling wobble or a WAL auto-checkpoint that lands in one leg and not
//     the other moves the ratio by whole multiples;
//   * the BASE is not a fixed amount of work, so the number the gate reads
//     depends on the size the gate happened to pick.
//
// The gate is now a BUDGET PER UNIT OF WORK, measured through the shared
// `PerfGate` helper (#1747): a FIXED amount of work (512 appended messages
// into a session that already holds 512), against a constant budget per
// appended message:
//
//     bytes per appended message  <= 32 KiB   (machine-independent, HARD)
//     probe units per message     <= 8000     (machine-scaled time, SOFT)
//
// WHY THE BUDGETS ARE WHAT THEY ARE
// ---------------------------------
// The byte budget is the load-bearing branch, and it is machine-independent:
// a byte count does not move with the runner's speed, so it is the only
// branch that can be a hard gate on a shared runner. Its number comes from
// `docs/BENCHMARKS.md` — "Sqlite WAL Append x10 | 2.2-2.6 ms | 155 KB", i.e.
// ~15.5 KB per append — with 2x headroom for the runtime's own bookkeeping
// inside the measured window. The #1534 shape this gate was written for cost
// two extra `ToString("O")` strings and two boxes per row; it cannot hide
// under a 32 KiB ceiling, and the REPORT line prints the measured number on
// every run so a maintainer can tighten the constant against what the runner
// actually does.
//
// The time budget is expressed in PROBE UNITS — the cost of one appended
// message divided by the cost of one unit of the fixed calibration probe
// measured on the SAME runner in the SAME run. That makes it
// machine-independent in the same way the byte budget is: the runner's speed
// appears in both operands and cancels. It is deliberately generous
// (8000 probe units is several times the measured cost), because the append
// path is dominated by the storage stack — a connection open, a WAL append,
// and an occasional auto-checkpoint fsync — and no CPU yardstick scales an
// fsync wait. It is also SOFT: `PerfGate` stands the time branch down when the
// measurement is not trusted (a spread across rounds the runner cannot
// reproduce), and reports it either way.
//
// THE MEASURED WINDOW
// -------------------
// The session under measurement already holds `SeedMessages` rows before the
// measured window opens, so a cost that grows with the SESSION's size lands
// in the measurement instead of hiding behind a fresh session. A path that is
// quadratic in the number of appends shows up too: its per-message AVERAGE
// over the window is several times the linear one, and the budget is written
// per message.
//
// THE ALLOCATION PROBE
// --------------------
// The append path awaits, so the per-thread counter is the wrong instrument
// (#741): sampled on both sides of an `await` it is the difference between two
// unrelated thread counters. The window therefore uses the PROCESS-WIDE
// counter, and the class is keyless `[NotInParallel]` — the only form that
// actually delivers "nothing else in this assembly allocates while the window
// is open" (a named key would only de-conflict itself).
//
// Linux-only for the same reason the older gates were: GC accounting differs
// across OS runtimes, and this project is in the ci.yml `test-os` matrix.
//
// KEYLESS [NotInParallel], deliberately — the process-wide allocation counter
// bills every allocation in the process for the duration of the window, and
// the keyless form is the only one that actually delivers "nothing else in
// this assembly allocates while the window is open". A named key such as
// [NotInParallel("alloc-tripwire")] would serialise only against classes
// carrying that same key, and this assembly has a dozen that carry no
// attribute at all. The old `[ParallelLimiter<SqliteStoreLimit>]` is gone with
// it: the limiter bounded how many SQLite suites ran at once, which the
// exclusivity already does — and bounding the OTHER suites against each
// other is not this gate's business.
[NotInParallel]
public sealed class SqliteAppendCostGate
{
    /// <summary>Rows the session already holds before the measured window opens.</summary>
    private const int SeedMessages = 512;

    /// <summary>Appended messages per measured leg — the fixed amount of work.</summary>
    private const int MeasuredMessages = 512;

    /// <summary>
    ///     Allocation budget for ONE appended message, in bytes. Machine-
    ///     independent, so it is compared as measured, with no band.
    /// </summary>
    private const long MaxBytesPerMessage = 16 * 1024;

    /// <summary>
    ///     Time budget for ONE appended message, in probe units (see
    ///     `PerfGate`). Machine-scaled and soft.
    /// </summary>
    private const double MaxProbeUnitsPerMessage = 1_500.0;

    private static string NewTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-sqlite-{Guid.NewGuid():N}.db");

    private static SqliteSessionStore Create(out string dbPath)
    {
        dbPath = NewTempDbPath();
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static UserMessage NewMessage(string sessionId, int i) =>
        new(
            $"umsg-{Guid.NewGuid():N}",
            sessionId,
            DateTimeOffset.UnixEpoch.AddSeconds(i),
            "cost-gate message body",
            "code",
            "claude-opus-4");

    private static async Task AppendAsync(SqliteSessionStore store, string sessionId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var result = await store.AppendMessageAsync(sessionId, NewMessage(sessionId, i));
            if (result.IsFailure)
                throw new InvalidOperationException($"append failed: {result.Error}");
        }
    }

    /// <summary>
    ///     One measured leg: append <paramref name="count" /> messages to a
    ///     session that already holds <see cref="SeedMessages" /> rows, and
    ///     report the window's wall time and its allocation.
    /// </summary>
    private static async Task<PerfGateLeg> DriveAppendsAsync(
        SqliteSessionStore store, string sessionId, int count)
    {
        // Stopwatch.GetTimestamp does not allocate, so the window's own
        // bookkeeping stays out of the allocation sample.
        long started = Stopwatch.GetTimestamp();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await AppendAsync(store, sessionId, count);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long elapsed = Stopwatch.GetTimestamp() - started;

        return new PerfGateLeg(
            (double)elapsed * 1000.0 / Stopwatch.Frequency,
            allocated);
    }

    [Test]
    [Timeout(300_000)]
    public async Task Append_CostPerMessage_IsWithinBudget()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var store = Create(out string dbPath);
        try
        {
            var sessions = new List<string>();

            PerfGateResult result = await PerfGate.MeasureAsync(
                work: async units =>
                {
                    var session = (await store.CreateAsync("/gate", "code", "kilocode", "hy3")).Value;
                    sessions.Add(session.Id);

                    // Seed, UNMEASURED — see the file header.
                    await AppendAsync(store, session.Id, SeedMessages);

                    return await DriveAppendsAsync(store, session.Id, units);
                },
                units: MeasuredMessages,
                options: new PerfGateOptions
                {
                    Label = "sqlite-append-cost",
                    MaxBytesPerUnit = MaxBytesPerMessage,
                    MaxProbeUnitsPerUnit = MaxProbeUnitsPerMessage,
                });

            // Non-vacuity (#901 shape): every measured window really appended
            // every message, into a session that already held the seed. A
            // budget divided by zero work passes everything.
            await Assert.That(sessions).IsNotEmpty()
                .Because("the gate measured at least one window; none means nothing was measured.");
            foreach (string sessionId in sessions)
            {
                var rows = await store.GetMessagesAsync(sessionId);
                await Assert.That(rows).IsSuccessful();
                await Assert.That(rows.Value.Count).IsEqualTo(SeedMessages + MeasuredMessages);
            }

            await result.AssertWithinBudgetsAsync("sqlite append per message");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
