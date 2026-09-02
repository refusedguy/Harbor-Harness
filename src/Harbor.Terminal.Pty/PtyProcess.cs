using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Harbor.Terminal.Pty;

/// <summary>Launch spec for <see cref="PtyProcess.Start" />.</summary>
/// <param name="FileName">Executable (PATH-resolved when <paramref name="SearchPath" />).</param>
/// <param name="Args">Arguments passed verbatim.</param>
/// <param name="WorkingDirectory">Child cwd via a spawn file action; null = inherit.</param>
/// <param name="ExtraEnvironment">Overlay on top of the inherited process environment.</param>
/// <param name="Cols">Initial terminal width.</param>
/// <param name="Rows">Initial terminal height.</param>
/// <param name="SearchPath">PATH-resolve <paramref name="FileName" /> (posix_spawnp).</param>
public sealed record PtyStartSpec(
    string FileName,
    IReadOnlyList<string>? Args = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
    int Cols = 120,
    int Rows = 32,
    bool SearchPath = true);

/// <summary>
///     A real process inside a real pseudo-terminal, owned by the desktop app:
///     the parent holds the PTY master, child output arrives as raw byte
///     chunks on <see cref="OutputReceived" /> (reader thread), input goes in
///     through <see cref="Write" />, and <see cref="Resize" /> propagates
///     TIOCSWINSZ to the controlling terminal.
/// </summary>
/// <remarks>Unix (Linux + macOS). Windows ConPTY is a documented follow-up.</remarks>
public sealed class PtyProcess : IAsyncDisposable
{
    /// <summary>Size of the master-side read chunks.</summary>
    private const int ReadBufferSize = 8192;

    private readonly int _masterFd;
    private readonly int _pid;
    private readonly Task<int> _exitTask;
    private readonly Lock _writeLock = new();
    private int _disposed;

    private PtyProcess(int masterFd, int pid)
    {
        _masterFd = masterFd;
        _pid = pid;

        _exitTask = Task.Run(() =>
        {
            _ = NativeMethods.waitpid(_pid, out int status, 0);
            return DecodeStatus(status);
        });

        // LongRunning: the reader parks in a blocking libc read for the whole
        // process lifetime — it must not occupy a thread-pool worker.
        _ = Task.Factory.StartNew(
            ReaderLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>Raised on the reader thread for every raw output chunk from the child.</summary>
    public event EventHandler<PtyOutputEventArgs>? OutputReceived;

    /// <summary>Raised on the reader thread once the child's output side reached EOF.</summary>
    public event EventHandler? OutputClosed;

    /// <summary>false off-Unix.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Process id of the child.</summary>
    public int Pid => _pid;

    /// <summary>True once the child has been reaped.</summary>
    public bool HasExited => _exitTask.IsCompleted;

    /// <summary>Exit code (throws before exit; negative when SIGKILLed).</summary>
    public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exitTask.WaitAsync(ct);

    /// <summary>Spawn <paramref name="spec" /> inside a fresh PTY.</summary>
    public static PtyProcess Start(PtyStartSpec spec)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("PtyProcess requires a POSIX PTY platform (Windows ConPTY follow-up).");
        }

        int master = NativeMethods.posix_openpt(NativeMethods.O_RDWR | NativeMethods.O_NOCTTY | NativeMethods.O_CLOEXEC);
        if (master < 0)
        {
            throw new IOException($"posix_openpt failed: errno={Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (NativeMethods.grantpt(master) != 0 || NativeMethods.unlockpt(master) != 0)
            {
                throw new IOException($"grantpt/unlockpt failed: errno={Marshal.GetLastWin32Error()}.");
            }

            string slavePath = NativeMethods.GetSlaveName(master);
            Resize(master, spec.Cols, spec.Rows);

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                env[(string)entry.Key] = (string)entry.Value!;
            }

            if (spec.ExtraEnvironment is not null)
            {
                foreach ((string key, string value) in spec.ExtraEnvironment)
                {
                    env[key] = value;
                }
            }

            int pid = NativeMethods.SpawnInPty(
                spec.FileName, spec.Args ?? [], env, slavePath, spec.WorkingDirectory, spec.SearchPath);
            return new PtyProcess(master, pid);
        }
        catch
        {
            _ = NativeMethods.close(master);
            throw;
        }
    }

    // ── Input side ─────────────────────────────────────────────────────────

    /// <summary>Write raw bytes to the master (→ child stdin).</summary>
    public void Write(byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_writeLock)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int n = NativeMethods.write(_masterFd, ref bytes[offset], bytes.Length - offset);
                if (n < 0)
                {
                    if (Marshal.GetLastWin32Error() == NativeMethods.EINTR)
                    {
                        continue; // EINTR — retry the same range
                    }

                    throw new IOException($"write(master) failed: errno={Marshal.GetLastWin32Error()}.");
                }

                offset += n;
            }
        }
    }

    /// <summary>Write UTF-8 text verbatim (no newline appended).</summary>
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    /// <summary>Write text + '\n' (the byte canonical shells map to Enter).</summary>
    public void WriteLine(string text) => Write(text + "\n");

    /// <summary>Resize the controlling terminal via TIOCSWINSZ.</summary>
    public void Resize(int cols, int rows) => Resize(_masterFd, cols, rows);

    // ── Reader ─────────────────────────────────────────────────────────────

    private void ReaderLoop()
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                int n = NativeMethods.read(_masterFd, buffer, buffer.Length);
                if (n < 0)
                {
                    if (Marshal.GetLastWin32Error() == NativeMethods.EINTR)
                    {
                        continue;
                    }

                    break; // EBADF/EIO — master closed or slave gone
                }

                if (n == 0) break; // EOF

                var chunk = new byte[n];
                Array.Copy(buffer, chunk, n);
                OutputReceived?.Invoke(this, new PtyOutputEventArgs(chunk));
            }
        }
        catch (Exception ex)
        {
            // Reader must not crash the process on shutdown races.
            Debug.WriteLineIf(!IsSupported, $"PtyProcess reader ended: {ex.Message}");
        }

        OutputClosed?.Invoke(this, EventArgs.Empty);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void Resize(int fd, int cols, int rows)
    {
        var size = new NativeMethods.WinSize
        {
            Cols = (ushort)Math.Clamp(cols, 2, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 2, ushort.MaxValue),
        };
        if (NativeMethods.ioctl(fd, NativeMethods.TIOCSWINSZ, ref size) != 0)
        {
            throw new IOException($"TIOCSWINSZ failed: errno={Marshal.GetLastWin32Error()}.");
        }
    }

    private static int DecodeStatus(int status)
    {
        // waitpid status: low byte = signal (if non-zero), else high byte = exit code.
        return (status & 0x7f) != 0 ? -(status & 0x7f) : status >> 8;
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;

        OutputReceived = null;
        OutputClosed = null;
        try
        {
            _ = NativeMethods.kill(_pid, NativeMethods.SIGKILL);
        }
        catch (Exception ex)
        {
            // Already reaped — nothing to do.
            Debug.WriteLine($"PtyProcess kill skipped: {ex.Message}");
        }

        _ = NativeMethods.close(_masterFd);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Payload of <see cref="PtyProcess.OutputReceived" />.</summary>
public sealed class PtyOutputEventArgs(byte[] data) : EventArgs
{
    /// <summary>Raw bytes produced by the child (owned copy, safe to retain).</summary>
    public byte[] Data { get; } = data;
}
