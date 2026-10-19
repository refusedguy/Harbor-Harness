using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

// SqliteAppendScalingGate — #1534 (hot-path slice of #97, per append):
// every appended row used to pay 2× `ToString("O")` (the message stamp plus the
// session.updated_at touch) and one AddWithValue per parameter, with each
// value-type arg (`int` status/kind, `long` created_at_ms) boxing and having
// its SqliteType inferred from the runtime type on every call.
//
// GATE: relative, same shape as #410 / #1373 / #1528. Cost(2N)/cost(N) <= 3 for
// BOTH time and allocations over N appended messages (linear is 2.00, O(N^2)
// is 4.00). No absolute millisecond or byte budget: those measure the runner,
// not the code — the absolute columns print for information only.
//
// The windows are 1024 / 2048 messages, not small — run 39124054890 red-lined
// a 96/192 cut at 4.41x purely because SQLite's default 1000-page WAL
// autocheckpoint threshold fired inside the 2N leg and never inside the N leg,
// parking a whole checkpoint fsync burst on one side of the ratio. At 1024 the
// threshold is crossed in BOTH windows (proportionally: ~2-4 vs ~4-8 events),
// so the burst is shared and the ratio reads the code again.
//
// The alloc leg is thread-scoped (GC.GetAllocatedBytesForCurrentThread), the
// same idiom ToolDispatcherResolveOnceTests uses over an awaited async body:
// allocations that land on a hopped continuation are undercounted, but the
// undercount hits both windows alike, so the RATIO stays meaningful. The test
// is Linux-only for the same reason — GC accounting differs across runtimes.
[ParallelLimiter<SqliteStoreLimit>]
public sealed class SqliteAppendScalingGate
{
    private const int MessagesSmall = 1024;
    private const int WarmupMessages = 32;
    private const int Rounds = 3;
    private const double GrowthLimit = 3.0;

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
            "scaling-gate message body",
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

    private static async Task<(double Ms, long Allocated)> DriveAppendsAsync(
        SqliteSessionStore store, string sessionId, int count)
    {
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        await AppendAsync(store, sessionId, count);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Append_ScalesLinearly_InMessageCount()
    {
        if (!OperatingSystem.IsLinux())
            return; // GC accounting varies across OS runtimes.

        var store = Create(out string dbPath);
        try
        {
            var scratch = (await store.CreateAsync("/gate", "code", "kilocode", "hy3")).Value;
            var small = (await store.CreateAsync("/gate", "code", "kilocode", "hy3")).Value;
            var large = (await store.CreateAsync("/gate", "code", "kilocode", "hy3")).Value;

            // Warm-up (tiered JIT over the open/insert/update path), discarded —
            // runs on its own scratch session so it does not touch the windows.
            await AppendAsync(store, scratch.Id, WarmupMessages);

            long allocSmall = long.MaxValue;
            long allocLarge = long.MaxValue;
            double msSmall = double.MaxValue;
            double msLarge = double.MaxValue;
            for (int round = 0; round < Rounds; round++)
            {
                var s = await DriveAppendsAsync(store, small.Id, MessagesSmall);
                var l = await DriveAppendsAsync(store, large.Id, MessagesSmall * 2);
                msSmall = Math.Min(msSmall, s.Ms);
                msLarge = Math.Min(msLarge, l.Ms);
                allocSmall = Math.Min(allocSmall, s.Allocated);
                allocLarge = Math.Min(allocLarge, l.Allocated);
            }

            double timeRatio = msLarge / msSmall;
            double allocRatio = (double)allocLarge / allocSmall;

            Console.WriteLine(
                $"sqlite-append-scaling: {MessagesSmall} msgs = {msSmall:F3} ms / {allocSmall} B, "
                + $"{MessagesSmall * 2} msgs = {msLarge:F3} ms / {allocLarge} B "
                + $"(time x{timeRatio:F2}, alloc x{allocRatio:F2}, limit x{GrowthLimit:F2}, best of {Rounds}; "
                + "the absolute columns are informational and runner-dependent)");

            // Non-vacuity (#901 shape): both windows really appended every
            // message — scratch + Rounds×N in the small session, Rounds×2N in
            // the large one — so the ratios divide by real work, not nothing.
            var smallRows = await store.GetMessagesAsync(small.Id);
            var largeRows = await store.GetMessagesAsync(large.Id);
            await Assert.That(smallRows.Value.Count).IsEqualTo(Rounds * MessagesSmall);
            await Assert.That(largeRows.Value.Count).IsEqualTo(Rounds * MessagesSmall * 2);

            await Assert.That(msSmall)
                .IsGreaterThan(0.0)
                .Because("the time baseline must be a real measurement: msSmall = " + msSmall.ToString("F3") + " ms.");
            await Assert.That(allocSmall)
                .IsGreaterThan(0L)
                .Because("the alloc baseline must be a real measurement, got " + allocSmall + " B.");

            await Assert.That(timeRatio)
                .IsLessThanOrEqualTo(GrowthLimit)
                .Because(
                    "time(2N)/time(N) = " + timeRatio.ToString("F2") + " against a limit of "
                    + GrowthLimit.ToString("F2") + ". Linear is 2.00 and O(N^2) is 4.00.");
            await Assert.That(allocRatio)
                .IsLessThanOrEqualTo(GrowthLimit)
                .Because(
                    "alloc(2N)/alloc(N) = " + allocRatio.ToString("F2") + " against a limit of "
                    + GrowthLimit.ToString("F2") + ": " + allocLarge + " B versus " + allocSmall
                    + " B. Allocations do not move with the runner's speed, so this ratio holds on every machine.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
