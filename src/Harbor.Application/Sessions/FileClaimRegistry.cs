using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;

namespace Harbor.Application.Sessions;

/// <summary>
/// Cross-process advisory lock backed by an exclusive-create claim file
/// (<c>FileMode.CreateNew</c> ⇒ atomic O_EXCL semantics on POSIX and Windows).
/// Claims belong to a scope name (e.g. <c>session:{id}</c>) and carry the
/// owning pid plus a monotonic timestamp; claims whose owner died and aged
/// past the grace window are stealable, so a crashed CLI cannot wedge a
/// session forever. In-process double acquisition of one scope is refused
/// via a registry-local index before any filesystem roundtrip.
/// </summary>
public sealed class FileClaimRegistry : IDisposable
{
    private readonly string _directory;
    private readonly TimeSpan _staleGrace;
    private readonly ConcurrentDictionary<string, FileClaim> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// In-flight acquire reservations for this instance (#93). A scope is
    /// added before any filesystem roundtrip and removed on every exit path,
    /// so same-process contenders fail fast without wasted CreateNew I/O.
    /// The filesystem backstop still arbitrates across instances.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);

    /// <summary>
    ///     Serializes the steal sequence (check → delete → recreate → verify)
    ///     between same-process contenders (#57). Without it two contenders
    ///     can both decide "stealable" off the same seed file, then the loser
    ///     deletes the winner's fresh file with an unconditional
    ///     <c>File.Delete</c> and recreates its own — two simultaneous grants.
    ///     Cross-process interleavings keep advisory semantics (the atomic
    ///     <c>CreateNew</c> plus post-create verification still apply); the
    ///     lock only removes the in-process check-then-act hole. Entries are
    ///     bounded by <see cref="MaxStealLocks"/> FIFO eviction (#93; idle
    ///     instances are disposed, contended ones survive through their
    ///     holder's local reference) and the lock covers the steal path
    ///     only — the fresh-create fast path stays lock-free.
    ///     Async-compatible (<c>SemaphoreSlim</c>, never a monitor).
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _stealLocks = new(StringComparer.Ordinal);

    /// <summary>Maximum steal-lock entries retained (#93; mirrors the ApprovalCoordinator _retired cap).</summary>
    public const int MaxStealLocks = 1024;

    /// <summary>Current steal-lock entry count (observability for tests and monitoring).</summary>
    public static int StealLockCount => _stealLocks.Count;

    /// <summary>
    /// FIFO insertion order for steal-lock eviction. Guarded by
    /// <see cref="_stealLockGate"/>; membership mirrored in
    /// <see cref="_stealLockQueued"/> so re-acquires of a live scope do not
    /// enqueue duplicates.
    /// </summary>
    private static readonly object _stealLockGate = new();
    private static readonly Queue<string> _stealLockOrder = new();
    private static readonly HashSet<string> _stealLockQueued = new(StringComparer.Ordinal);

    /// <summary>
    /// Get (or create) the per-scope steal serializer, evicting the oldest
    /// idle entries past <see cref="MaxStealLocks"/>. Evicted semaphores are
    /// disposed only when idle (<c>CurrentCount == 1</c>); a contended lock
    /// keeps working through its holder's local reference while the next
    /// contender mints a fresh instance (documented residual: a steal that
    /// races its own eviction briefly splits serialization, but the atomic
    /// <c>CreateNew</c> plus post-create verification still arbitrate).
    /// </summary>
    private static SemaphoreSlim GetStealLock(string scope)
    {
        var sem = _stealLocks.GetOrAdd(scope, static _ => new SemaphoreSlim(1, 1));
        lock (_stealLockGate)
        {
            if (_stealLockQueued.Add(scope))
            {
                _stealLockOrder.Enqueue(scope);
                while (_stealLockOrder.Count > MaxStealLocks
                    && _stealLockOrder.TryDequeue(out string? oldest))
                {
                    _stealLockQueued.Remove(oldest);
                    if (string.Equals(oldest, scope, StringComparison.Ordinal))
                    {
                        // Never evict the entry just added; re-queue and stop.
                        _stealLockQueued.Add(oldest);
                        _stealLockOrder.Enqueue(oldest);
                        break;
                    }

                    if (_stealLocks.TryRemove(oldest, out var evicted) && evicted.CurrentCount == 1)
                    {
                        evicted.Dispose();
                    }
                }
            }
        }

        return sem;
    }

    /// <summary>
    /// Create a registry bound to a claims directory.
    /// </summary>
    /// <param name="directory">Directory holding <c>*.claim</c> files (created on demand).</param>
    /// <param name="staleGrace">Minimum age of a dead-owner claim before another process may steal it.</param>
    public FileClaimRegistry(string directory, TimeSpan? staleGrace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _staleGrace = staleGrace ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Try once to acquire <paramref name="scope"/>. Failure carries a caller-
    /// presentable reason (another live holder / stolen-after-steal race);
    /// contention is handled by callers polling at their own cadence.
    /// </summary>
    public async Task<Result<FileClaim>> AcquireAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        if (_active.ContainsKey(scope))
        {
            return Result.Failure<FileClaim>($"Scope '{scope}' is already claimed by this process.");
        }

        // Atomic fast path (#93): reserve before any I/O so same-process
        // contenders fail without a wasted CreateNew roundtrip.
        if (!_inflight.TryAdd(scope, 0))
        {
            return Result.Failure<FileClaim>($"Scope '{scope}' is already claimed by this process.");
        }

        try
        {
            var claimPath = Path.Combine(_directory, $"{ScopeToFileName(scope)}.claim");

            try
            {
                Directory.CreateDirectory(_directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<FileClaim>($"Cannot access claims directory '{_directory}': {ex.Message}");
            }

            // Fresh create wins atomically.
            FileClaim? created = await CreateClaimAsync(scope, claimPath, ct).ConfigureAwait(false);
            if (created is not null)
            {
                if (!_active.TryAdd(scope, created))
                {
                    // Defensive: same-instance reservation makes this
                    // unreachable; never leave our file orphaned.
                    created.Dispose();
                    DeleteOwnFile(claimPath, created.Token);
                    return Result.Failure<FileClaim>($"Scope '{scope}' is already claimed by this process.");
                }

                return created;
            }

            // Existing file: readable-but-dead owner past grace ⇒ steal.
            // The whole steal sequence rides the per-scope lock so same-process
            // contenders serialize: the first completer's live-pid file makes
            // every later check refuse (see _stealLocks).
            if (!ShouldSteal(claimPath, out string? failure))
            {
                return Result.Failure<FileClaim>(failure ?? $"Scope '{scope}' is held by another live process.");
            }

            var stealLock = GetStealLock(scope);
            await stealLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-check under the lock: a previous holder may have completed
                // while we queued, and its live-pid file must refuse us now.
                if (!ShouldSteal(claimPath, out failure))
                {
                    return Result.Failure<FileClaim>(failure ?? $"Scope '{scope}' is held by another live process.");
                }

                try
                {
                    File.Delete(claimPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Lost the steal race — the stealer that won owns it now.
                }

                created = await CreateClaimAsync(scope, claimPath, ct).ConfigureAwait(false);
                if (created is null)
                {
                    return Result.Failure<FileClaim>($"Lost steal race for scope '{scope}'.");
                }

                // Post-create verification: confirm the on-disk token is still
                // ours (a cross-process deleter could have slipped between our
                // create and now — in-process contenders cannot, they wait on
                // the lock). A mismatch means we lost: concede without
                // registering, and never touch the foreign file.
                if (!OwnsFile(claimPath, created.Token))
                {
                    return Result.Failure<FileClaim>($"Lost steal race for scope '{scope}'.");
                }
            }
            finally
            {
                stealLock.Release();
            }

            if (!_active.TryAdd(scope, created))
            {
                created.Dispose();
                DeleteOwnFile(claimPath, created.Token);
                return Result.Failure<FileClaim>($"Scope '{scope}' is already claimed by this process.");
            }

            return created;
        }
        finally
        {
            _inflight.TryRemove(scope, out _);
        }
    }

    /// <summary>True while this registry instance holds <paramref name="scope"/>.</summary>
    public bool IsHeld(string scope) => _active.ContainsKey(scope);

    /// <summary>
    /// Best-effort delete of a just-created claim file carrying our
    /// <paramref name="token"/>; a foreign token is never touched.
    /// </summary>
    private static void DeleteOwnFile(string claimPath, string token)
    {
        try
        {
            if (OwnsFile(claimPath, token))
            {
                File.Delete(claimPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the caller carries the outcome.
        }
    }

    private async Task<FileClaim?> CreateClaimAsync(string scope, string claimPath, CancellationToken ct)
    {
        var claim = new FileClaim(this, scope, claimPath, Environment.ProcessId);
        try
        {
            // FileMode.CreateNew fails when the file exists — the whole design.
            await using var stream = new FileStream(
                claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 256);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(claim.Serialize().AsMemory(), ct).ConfigureAwait(false);
            return claim;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     True when the file at <paramref name="claimPath" /> still carries
    ///     our <paramref name="token" />. Any I/O failure reads as "not ours"
    ///     (fail closed — the file vanished or belongs to the winner).
    /// </summary>
    private static bool OwnsFile(string claimPath, string token)
    {
        try
        {
            return File.ReadAllText(claimPath).Contains($"token={token}", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool ShouldSteal(string claimPath, out string? failure)
    {
        failure = null;
        DateTime nowUtc = DateTime.UtcNow;

        string content;
        try
        {
            content = File.ReadAllText(claimPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disappeared mid-check or unreadable: treat as still-held.
            return false;
        }

        if (!FileClaim.TryParse(content, out int pid, out _, out DateTime stampedUtc))
        {
            failure = $"Claim '{claimPath}' is corrupt.";
            return nowUtc - stampedUtc > _staleGrace * 2;
        }

        bool ownerDead = !PidAlive(pid);
        if (ownerDead && nowUtc - stampedUtc > _staleGrace)
        {
            return true;
        }

        failure = ownerDead
            ? $"Claim '{claimPath}' owner (pid {pid}) is gone but the grace window has not elapsed."
            : $"Claim '{claimPath}' owner (pid {pid}) is still running.";
        return false;
    }

    private static bool PidAlive(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var probe = Process.GetProcessById(pid);
            return !probe.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws for already-exited processes.
            return false;
        }
    }

    internal void Release(FileClaim claim)
    {
        if (!_active.TryRemove(new KeyValuePair<string, FileClaim>(claim.Scope, claim)))
        {
            return; // Already released or force-replaced; never touch foreign files twice.
        }

        try
        {
            // Delete only when the on-disk token is still ours — another
            // process may have stolen and re-created the file meanwhile.
            string content = File.ReadAllText(claim.ClaimPath);
            if (content.Contains($"token={claim.Token}", StringComparison.Ordinal))
            {
                File.Delete(claim.ClaimPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone already (or unreadable) — releasing a missing claim is success.
        }
    }
    /// <summary>
    /// Scope string → safe on-disk stem. Lossless (#93): <c>_</c> escapes to
    /// <c>__</c>, every other non-letter-or-digit char to <c>_xXXXX</c>
    /// (lowercase hex UTF-16 code unit), so distinct scopes never collide
    /// (<c>a/b</c> vs <c>a:b</c> vs <c>a_b</c> all diverge). Output is
    /// <c>[A-Za-z0-9_]</c> only — safe on POSIX and Windows.
    /// </summary>
    public static string ScopeToFileName(string scope)
    {
        var sb = new StringBuilder(scope.Length);
        foreach (char c in scope)
        {
            if (c == '_')
            {
                sb.Append("__");
            }
            else if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append("_x");
                sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var claim in _active.Values)
        {
            claim.Dispose();
        }

        _active.Clear();
    }
}

/// <summary>An owned cross-process claim; dispose releases (token-guarded delete).</summary>
public sealed class FileClaim : IDisposable
{
    private readonly FileClaimRegistry _owner;
    private int _released;

    internal FileClaim(FileClaimRegistry owner, string scope, string claimPath, int pid)
    {
        _owner = owner;
        Scope = scope;
        ClaimPath = claimPath;
        OwnerPid = pid;
        Token = Guid.NewGuid().ToString("N");
        StampedUtc = DateTime.UtcNow;
    }

    public string Scope { get; }
    public string ClaimPath { get; }
    public int OwnerPid { get; }
    internal string Token { get; }
    internal DateTime StampedUtc { get; }

    internal string Serialize() =>
        string.Create(CultureInfo.InvariantCulture, $"pid={OwnerPid};token={Token};ts={StampedUtc.ToString("o", CultureInfo.InvariantCulture)}");

    internal static bool TryParse(string content, out int pid, out string token, out DateTime stampedUtc)
    {
        pid = -1;
        token = string.Empty;
        stampedUtc = DateTime.MinValue;

        foreach (var part in content.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = part[..eq];
            string value = part[(eq + 1)..];
            switch (key)
            {
                case "pid":
                    _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
                    break;
                case "token":
                    token = value;
                    break;
                case "ts":
                    _ = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out stampedUtc);
                    break;
            }
        }

        return pid >= 0 && token.Length > 0 && stampedUtc != DateTime.MinValue;
    }

    /// <summary>Refresh the timestamp so dead-pid theft does not fire early.</summary>
    public void KeepAlive()
    {
        ClaimStamp.Rewrite(ClaimPath, Token, OwnerPid, DateTime.UtcNow);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _owner.Release(this);
        GC.SuppressFinalize(this);
    }
}

internal static class ClaimStamp
{
    public static void Rewrite(string path, string token, int pid, DateTime stampedUtc)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            using var reader = new StreamReader(stream);
            string existing = reader.ReadToEnd();
            // Only refresh a stamp we own; never resurrect foreign metadata.
            if (!existing.Contains($"token={token}", StringComparison.Ordinal))
            {
                return;
            }

            stream.Seek(0, SeekOrigin.Begin);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream);
            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"pid={pid};token={token};ts={stampedUtc.ToString("o", CultureInfo.InvariantCulture)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort heartbeat: races resolve into either role harmlessly.
        }
    }
}
