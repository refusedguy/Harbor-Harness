using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CSharpFunctionalExtensions;
using Harbor.Application.Sessions;

namespace Harbor.Application.Tests;

public class FileClaimRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"harbor-claims-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Test cleanup is best-effort; the dir may already be gone from a previous run.
        }
    }

    private FileClaimRegistry New(TimeSpan? grace = null) => new(_dir, grace);

    [Test]
    public async Task Acquire_Dispose_CreatesThenRemovesClaimFile()
    {
        using var registry = New();
        var acquired = await registry.AcquireAsync("session:abc", CancellationToken.None);

        await Assert.That(acquired.IsSuccess).IsTrue();
        var claim = acquired.Value;
        await Assert.That(File.Exists(claim.ClaimPath)).IsTrue();

        string content = await File.ReadAllTextAsync(claim.ClaimPath, CancellationToken.None);
        await Assert.That(content.Contains($"pid={Environment.ProcessId}", StringComparison.Ordinal)).IsTrue();

        claim.Dispose();
        await Assert.That(File.Exists(claim.ClaimPath)).IsFalse();
        await Assert.That(registry.IsHeld("session:abc")).IsFalse();
    }

    [Test]
    public async Task DoubleAcquire_SameInstance_FailsCleanly()
    {
        using var registry = New();
        var firstResult = await registry.AcquireAsync("scope", CancellationToken.None);
        var first = firstResult.Value;

        var second = await registry.AcquireAsync("scope", CancellationToken.None);

        await Assert.That(second.IsFailure).IsTrue();
        await Assert.That(second.Error).Contains("already claimed by this process");
        // First claim file survived the failed attempt untouched.
        await Assert.That(File.Exists(first.ClaimPath)).IsTrue();

        first.Dispose();
    }

    [Test]
    public async Task ScopeToFileName_ReplacesUnsafeCharacters()
    {
        string name = FileClaimRegistry.ScopeToFileName("a/b\\c:d*e?.txt");

        await Assert.That(name).IsEqualTo("a_x002fb_x005cc_x003ad_x002ae_x003f_x002etxt");
        await Assert.That(Path.GetFileName(name)).IsEqualTo(name);
    }

    [Test]
    public async Task ScopeToFileName_IsLossless_DistinctScopesDiverge()
    {
        // #93: the old lossy fold mapped a/b, a:b and a_b onto one stem.
        string slash = FileClaimRegistry.ScopeToFileName("a/b");
        string colon = FileClaimRegistry.ScopeToFileName("a:b");
        string underscore = FileClaimRegistry.ScopeToFileName("a_b");

        await Assert.That(slash).IsEqualTo("a_x002fb");
        await Assert.That(colon).IsEqualTo("a_x003ab");
        await Assert.That(underscore).IsEqualTo("a__b");
        await Assert.That(slash == colon).IsFalse();
        await Assert.That(slash == underscore).IsFalse();
        await Assert.That(colon == underscore).IsFalse();

        foreach (char c in slash + colon + underscore)
        {
            await Assert.That(char.IsLetterOrDigit(c) || c == '_').IsTrue();
        }
    }

    [Test]
    public async Task Acquire_DirectoryBlockedByFile_ReturnsFailureInsteadOfThrowing()
    {
        // #93: a file occupying the claims-directory path makes
        // Directory.CreateDirectory raise IOException — must surface as
        // Result.Failure, never escape as an exception.
        string blocker = Path.Combine(Path.GetTempPath(), $"harbor-claims-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "blocker", CancellationToken.None);
        try
        {
            using var registry = new FileClaimRegistry(blocker);
            var result = await registry.AcquireAsync($"blockedN{Guid.NewGuid():N}", CancellationToken.None);

            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Test]
    public async Task Acquire_ClaimPathIsDirectory_ReturnsFailureInsteadOfThrowing()
    {
        // #93: a directory at the claim path makes CreateNew/ReadAllText raise
        // UnauthorizedAccessException (Linux) or IOException (Windows) — both
        // must map to Result.Failure via the broadened catch breadth.
        using var registry = New();
        string scopeName = $"dirclaimN{Guid.NewGuid():N}";
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, $"{FileClaimRegistry.ScopeToFileName(scopeName)}.claim"));

        var result = await registry.AcquireAsync(scopeName, CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(registry.IsHeld(scopeName)).IsFalse();
    }

    [Test]
    public async Task Acquire_HeldScope_ConcurrentSameInstance_AllRefused_NoOrphans()
    {
        // #93 atomic fast path: while one holder keeps the scope, parallel
        // same-instance contenders are refused without a double grant.
        using var registry = New();
        string scopeName = $"heldraceN{Guid.NewGuid():N}";
        var holder = await registry.AcquireAsync(scopeName, CancellationToken.None);
        await Assert.That(holder.IsSuccess).IsTrue();
        try
        {
            const int contenders = 8;
            int refused = 0;
            await Parallel.ForAsync(
                0,
                contenders,
                new ParallelOptions { MaxDegreeOfParallelism = contenders },
                async (_, _) =>
                {
                    var attempt = await registry.AcquireAsync(scopeName, CancellationToken.None);
                    if (attempt.IsFailure)
                    {
                        Interlocked.Increment(ref refused);
                    }
                    else
                    {
                        attempt.Value.Dispose();
                    }
                });

            await Assert.That(refused).IsEqualTo(contenders);
        }
        finally
        {
            holder.Value.Dispose();
        }

        await Assert.That(Directory.GetFiles(_dir, "*.claim").Length).IsEqualTo(0);
    }

    [Test]
    public async Task StealLocks_BoundedAfterManySteals()
    {
        // #93: the static steal-lock table is FIFO-capped at MaxStealLocks
        // (mirrors the ApprovalCoordinator _retired cap); 1280 distinct steal
        // scopes must not grow it past the cap and must leave no orphans.
        int deadPid = StartChildAndReap();
        Directory.CreateDirectory(_dir);
        using var registry = New(grace: TimeSpan.FromMilliseconds(50));
        const int scopes = FileClaimRegistry.MaxStealLocks + 256;
        for (int i = 0; i < scopes; i++)
        {
            string scopeName = $"evictN{i:x4}{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(
                Path.Combine(_dir, $"{scopeName}.claim"),
                string.Create(CultureInfo.InvariantCulture,
                    $"pid={deadPid};token=frozen;ts={DateTime.UtcNow.AddSeconds(-2):o}"),
                CancellationToken.None);

            var result = await registry.AcquireAsync(scopeName, CancellationToken.None);
            await Assert.That(result.IsSuccess).IsTrue();
            result.Value.Dispose();
        }

        // Transient overshoot from parallel steal tests resolves in
        // milliseconds (trim runs synchronously inside GetStealLock).
        for (int attempt = 0;
            attempt < 50 && FileClaimRegistry.StealLockCount > FileClaimRegistry.MaxStealLocks;
            attempt++)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        await Assert.That(FileClaimRegistry.StealLockCount <= FileClaimRegistry.MaxStealLocks).IsTrue();
        await Assert.That(Directory.GetFiles(_dir, "*.claim").Length).IsEqualTo(0);
    }

    [Test]
    public async Task DeadOwnerPastGrace_IsStolen()
    {
        string scopeName = $"stolenN{Guid.NewGuid():N}";
        int deadPid = StartChildAndReap();

        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, $"{scopeName}.claim"),
            string.Create(CultureInfo.InvariantCulture,
                $"pid={deadPid};token=foreign;ts={DateTime.UtcNow.AddSeconds(-2):o}"),
            CancellationToken.None);

        using var registry = New(grace: TimeSpan.FromMilliseconds(50));
        var result = await registry.AcquireAsync(scopeName, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        string content = await File.ReadAllTextAsync(result.Value.ClaimPath, CancellationToken.None);
        await Assert.That(content.Contains($"pid={Environment.ProcessId}", StringComparison.Ordinal)).IsTrue();

        result.Value.Dispose();
        await Assert.That(File.Exists(result.Value.ClaimPath)).IsFalse();
    }

    [Test]
    public async Task CorruptStamp_IsStalestealable()
    {
        string scopeName = $"corruptN{Guid.NewGuid():N}";
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, $"{scopeName}.claim"), "not a stamp at all", CancellationToken.None);

        using var registry = New(grace: TimeSpan.FromMilliseconds(50));
        var result = await registry.AcquireAsync(scopeName, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        result.Value.Dispose();
        await Assert.That(File.Exists(result.Value.ClaimPath)).IsFalse();
    }

    [Test]
    public async Task LiveForeignPid_IsNeverStolen_EvenWhenAncient()
    {
        // Own pid behind an ancient foreign stamp simulates another LIVE process.
        using var registry = New(grace: TimeSpan.Zero);
        string scopeName = $"heldN{Guid.NewGuid():N}";
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, $"{scopeName}.claim"),
            string.Create(CultureInfo.InvariantCulture,
                $"pid={Environment.ProcessId};token=live;ts={DateTime.UtcNow.AddHours(-1):o}"),
            CancellationToken.None);

        var result = await registry.AcquireAsync(scopeName, CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("still running");
        await Assert.That(Directory.GetFiles(_dir, "*.claim").Length).IsEqualTo(1);
    }

    [Test]
    public async Task KeepAlive_RefreshesStamp_ReleaseStillDeletes()
    {
        using var registry = New();
        var claim = (await registry.AcquireAsync("beat", CancellationToken.None)).Value;

        Thread.Sleep(30); // ensure the RFC3339 timestamp visibly moves
        claim.KeepAlive();

        string refreshed = await File.ReadAllTextAsync(claim.ClaimPath, CancellationToken.None);
        await Assert.That(refreshed.StartsWith("pid=", StringComparison.Ordinal)).IsTrue();

        claim.Dispose();
        // Token identity survived the rewrite — release deleted OUR file.
        await Assert.That(File.Exists(claim.ClaimPath)).IsFalse();
    }

    /// <summary>
    /// E2E concurrent stress — phase A: 12 workers race one scope in parallel;
    /// atomic CreateNew guarantees a winner exists and releases cleanly.
    /// Phase B: while one holder keeps the slot every other worker is rejected
    /// (the true double-grant guard). Phase C: deterministic handoff — each
    /// worker claims exactly once in sequence; zero orphaned files remain.
    /// </summary>
    [Test]
    public async Task ConcurrentStress_OneWinner_NoDoubleGrant_CleanHandoff()
    {
        const int workers = 12;
        string scopeName = $"stressN{Guid.NewGuid():N}";
        using var registry = New();

        int granted = 0;
        var phaseA = new ParallelOptions { MaxDegreeOfParallelism = workers };
        await Parallel.ForAsync(0, workers, phaseA, async (_, _) =>
        {
            var attempt = await registry.AcquireAsync(scopeName, CancellationToken.None);
            if (attempt.IsSuccess)
            {
                Interlocked.Increment(ref granted);
                attempt.Value.Dispose();
            }
        });

        await Assert.That(granted).IsGreaterThanOrEqualTo(1);

        // Phase B — the held scope rejects ALL parallel contenders.
        var holder = await registry.AcquireAsync(scopeName, CancellationToken.None);
        await Assert.That(holder.IsSuccess).IsTrue();

        int rejectedWhileHeld = 0;
        var phaseB = new ParallelOptions { MaxDegreeOfParallelism = workers };
        await Parallel.ForAsync(0, workers - 1, phaseB, async (_, _) =>
        {
            var attempt = await registry.AcquireAsync(scopeName, CancellationToken.None);
            if (attempt.IsFailure)
            {
                Interlocked.Increment(ref rejectedWhileHeld);
            }
            else
            {
                attempt.Value.Dispose(); // defensive teardown on invariant breach
            }
        });
        holder.Value.Dispose();

        // Phase C — after release the scope hands off deterministically.
        int handoffs = 0;
        for (int i = 0; i < workers; i++)
        {
            var attempt = await registry.AcquireAsync(scopeName, CancellationToken.None);
            await Assert.That(attempt.IsSuccess).IsTrue();
            handoffs++;
            attempt.Value.Dispose();
        }

        await Assert.That(rejectedWhileHeld).IsEqualTo(workers - 1);
        await Assert.That(handoffs).IsEqualTo(workers);
        await Assert.That(Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.claim").Length : 0).IsEqualTo(0);
    }

    /// <summary>
    /// Spawns a short-lived child process and returns its (reaped) pid.</summary>
    private static int StartChildAndReap()
    {
        bool isWindows = OperatingSystem.IsWindows();
        var psi = isWindows
            ? new ProcessStartInfo("cmd.exe", "/C exit") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false };

        using var child = Process.Start(psi)!;
        child.WaitForExit(5000);
        return child.Id;
    }

    /// <summary>
    /// E2E multi-"process" simulation: independent registry instances share
    /// NOTHING but the claims directory, so every grant/fail decision rides
    /// on CreateNew atomicity alone. Each parallel round admits exactly one
    /// winner; serialized hand-off rounds terminate with zero orphan files.
    /// </summary>
    [Test]
    public async Task IndependentRegistries_Race_OneWinner_PerRound_ZeroOrphans()
    {
        const int contendersPerRound = 8;
        const int rounds = 6;
        string scopeName = $"multiN{Guid.NewGuid():N}";
        var registries = Enumerable.Range(0, contendersPerRound).Select(_ => New()).ToArray();
        try
        {
            int grants = 0;
            for (int round = 0; round < rounds; round++)
            {
                ConcurrentBag<Result<FileClaim>> attempts = [];
                await Parallel.ForAsync(
                    0,
                    contendersPerRound,
                    new ParallelOptions { MaxDegreeOfParallelism = contendersPerRound },
                    async (_, _) => attempts.Add(await registries[Random.Shared.Next(contendersPerRound)]
                        .AcquireAsync(scopeName, CancellationToken.None)));

                var winners = attempts.Where(a => a.IsSuccess).ToArray();
                await Assert.That(winners.Length).IsEqualTo(1);
                grants++;

                // Live self-pid stamp is refused by every OTHER instance too.
                var losser = await registries[round % contendersPerRound].AcquireAsync(scopeName, CancellationToken.None);
                await Assert.That(losser.IsFailure).IsTrue();

                foreach (var w in winners)
                {
                    w.Value.Dispose();
                }
            }

            await Assert.That(grants).IsEqualTo(rounds);
            await Assert.That(Directory.GetFiles(_dir, "*.claim").Length).IsEqualTo(0);
        }
        finally
        {
            foreach (var r in registries)
            {
                r.Dispose();
            }
        }
    }

    /// <summary>
    /// E2E steal storm: K independent instances hammer one dead-owner claim
    /// past grace. Deterministic outcome for same-process contenders — the
    /// steal sequence is serialized per scope (#57: check → delete → recreate
    /// → verify under a lock), so the first completer wins and its live-pid
    /// file makes every later check refuse. Exactly one grant, no leftovers.
    /// </summary>
    [Test]
    public async Task StealStorm_DeadOwner_AtMostOneGrant_NoOrphans()
    {
        const int stormSize = 10;
        string scopeName = $"stormN{Guid.NewGuid():N}";
        int deadPid = StartChildAndReap();

        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, $"{scopeName}.claim"),
            string.Create(CultureInfo.InvariantCulture,
                $"pid={deadPid};token=frozen;ts={DateTime.UtcNow.AddSeconds(-2):o}"),
            CancellationToken.None);

        var registries = Enumerable.Range(0, stormSize).Select(_ => New(grace: TimeSpan.FromMilliseconds(50))).ToArray();
        List<FileClaim> winners = [];
        try
        {
            await Parallel.ForAsync(
                0,
                stormSize,
                new ParallelOptions { MaxDegreeOfParallelism = stormSize },
                async (_, _) =>
                {
                    var attempt = await registries[Random.Shared.Next(stormSize)].AcquireAsync(scopeName, CancellationToken.None);
                    if (attempt.IsSuccess)
                    {
                        lock (winners)
                        {
                            winners.Add(attempt.Value);
                        }
                    }
                });

            await Assert.That(winners.Count).IsEqualTo(1);

            foreach (var w in winners)
            {
                w.Dispose();
            }

            // The lone winner stole (deleted) the seed and released its own
            // file on dispose — nothing may remain.
            string[] leftovers = Directory.GetFiles(_dir, "*.claim");
            await Assert.That(leftovers.Length).IsEqualTo(0);
        }
        finally
        {
            foreach (var r in registries)
            {
                r.Dispose();
            }
        }
    }
}
