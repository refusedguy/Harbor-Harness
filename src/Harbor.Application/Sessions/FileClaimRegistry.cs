using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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

        var claimPath = Path.Combine(_directory, $"{ScopeToFileName(scope)}.claim");
        if (_active.ContainsKey(scope))
        {
            return Result.Failure<FileClaim>($"Scope '{scope}' is already claimed by this process.");
        }

        Directory.CreateDirectory(_directory);

        // Fresh create wins atomically.
        FileClaim? created = await CreateClaimAsync(scope, claimPath, ct).ConfigureAwait(false);
        if (created is not null)
        {
            _active[scope] = created;
            return created;
        }

        // Existing file: readable-but-dead owner past grace ⇒ steal.
        if (!ShouldSteal(claimPath, out string? failure))
        {
            return Result.Failure<FileClaim>(failure ?? $"Scope '{scope}' is held by another live process.");
        }

        try
        {
            File.Delete(claimPath);
        }
        catch (IOException)
        {
            // Lost the steal race — the stealer that won owns it now.
        }

        created = await CreateClaimAsync(scope, claimPath, ct).ConfigureAwait(false);
        if (created is null)
        {
            return Result.Failure<FileClaim>($"Lost steal race for scope '{scope}'.");
        }

        _active[scope] = created;
        return created;
    }

    /// <summary>True while this registry instance holds <paramref name="scope"/>.</summary>
    public bool IsHeld(string scope) => _active.ContainsKey(scope);

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

    private bool ShouldSteal(string claimPath, out string? failure)
    {
        failure = null;
        DateTime nowUtc = DateTime.UtcNow;

        string content;
        try
        {
            content = File.ReadAllText(claimPath);
        }
        catch (IOException)
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
        catch (IOException)
        {
            // Gone already — releasing a missing claim is success.
        }
    }
    /// <summary>Scope string → safe on-disk stem (unsafe chars → '_').</summary>
    public static string ScopeToFileName(string scope)
    {
        Span<char> buf = stackalloc char[scope.Length];
        for (int i = 0; i < scope.Length; i++)
        {
            char c = scope[i];
            buf[i] = char.IsLetterOrDigit(c) ? c : '_';
        }

        return new string(buf);
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
        catch (IOException)
        {
            // Best-effort heartbeat: races resolve into either role harmlessly.
        }
    }
}
