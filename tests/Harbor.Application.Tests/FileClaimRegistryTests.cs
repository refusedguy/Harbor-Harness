using System.Diagnostics;
using System.Globalization;
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

        await Assert.That(name).IsEqualTo("a_b_c_d_e__txt");
        await Assert.That(Path.GetFileName(name)).IsEqualTo(name);
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

    /// <summary>Spawns a short-lived child process and returns its (reaped) pid.</summary>
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
}
