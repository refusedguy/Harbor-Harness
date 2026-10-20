namespace Harbor.TestKit;

/// <summary>
///     Owns one temporary directory and deletes it when the scope is disposed.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a scope object and not a string.</b> The hand-written
///         boilerplate this replaces was the same five lines in every file:
///         build a unique path, create it, <c>try { … }</c>,
///         <c>finally { Directory.Delete(dir, recursive: true); }</c>. Two of
///         those shapes were wrong in ways a reader cannot see: a
///         <c>Directory.Delete</c> that ran only on the success path (the
///         directory outlived a failing test), and a delete that threw when the
///         directory was already gone (the failure masked the real one). A
///         <c>using</c>-bound scope removes both by construction — the
///         compiler emits the <c>finally</c>.
///     </para>
///     <para>
///         <b>Parallel safety.</b> Each instance owns a path nobody else can
///         guess: the prefix is a human label, and the suffix is a fresh
///         <see cref="Guid" />. Two tests running in parallel therefore get
///         two different directories and neither can delete the other's
///         files. Nothing here is process-global, so no serialisation is
///         needed.
///     </para>
///     <para>
///         <b>Where the directory lives.</b> Under <c>~/.cache/harbor-tests</c>,
///         not the world-writable shared <c>/tmp</c> — the same decision
///         <c>TestTempDirs</c> already made, for the same reason (a
///         predictable path under a world-writable directory is a symlink
///         attack surface).
///     </para>
/// </remarks>
public sealed class TempDirScope : IDisposable
{
    private static int _sequence;

    private bool _disposed;

    /// <summary>
    ///     Creates the directory and returns a scope that owns it.
    /// </summary>
    /// <param name="prefix">
    ///     Human-readable label for the directory name. Purely diagnostic — the
    ///     uniqueness comes from the suffix, not from the prefix.
    /// </param>
    public TempDirScope(string prefix = "harbor")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        // Three sources of uniqueness, so that two processes (or two runs of
        // the same test) cannot collide even if the GUID were to repeat.
        int sequence = Interlocked.Increment(ref _sequence);
        string name = $"{prefix}-{Environment.ProcessId}-{sequence}-{Guid.NewGuid():N}";

        Path = System.IO.Path.Combine(BaseDirectory, name);
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    ///     Root for all test temp paths; created on first use.
    /// </summary>
    /// <remarks>
    ///     The same <c>~/.cache/harbor-tests</c> root <c>TestTempDirs</c> uses —
    ///     a user-private directory instead of the world-writable shared
    ///     <c>/tmp</c>.
    /// </remarks>
    public static string BaseDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "harbor-tests");

    /// <summary>
    ///     Absolute path of the created directory. The directory exists for as
    ///     long as this scope is alive.
    /// </summary>
    public string Path { get; }

    /// <summary>
    ///     A path INSIDE this scope's directory. The file is not created — the
    ///     caller writes it.
    /// </summary>
    /// <param name="parts">Path segments, combined under <see cref="Path" />.</param>
    public string Combine(params string[] parts) => System.IO.Path.Combine(Path, System.IO.Path.Combine(parts));

    /// <summary>
    ///     Deletes the directory and everything under it.
    /// </summary>
    /// <remarks>
    ///     Best-effort by design: cleanup must never turn a passing test into a
    ///     failing one, and must never hide the failure that made the cleanup
    ///     necessary. A directory that is already gone is a success, not an
    ///     error. Calling this twice is a no-op.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup: another handle is still open, or the file
            // system said no. The test's own result is what matters.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
