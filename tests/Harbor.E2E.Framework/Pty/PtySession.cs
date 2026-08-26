using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Harbor.E2E.Framework.Pty;

namespace Harbor.E2E.Framework;

/// <summary>Launch spec for <see cref="PtySession.Start" />.</summary>
public sealed record PtyStartSpec(
    string FileName,
    IReadOnlyList<string> Args,
    int Cols,
    int Rows,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool SearchPath = true);

/// <summary>
///     A real process inside a real pseudo-terminal (CE-5 Зона 1). The parent
///     owns the PTY master: scripted stdin bytes go in, raw stdout/stderr
///     bytes come out, resizes are applied via TIOCSWINSZ, and the slave-side
///     termios is snapshot-able from the master fd — exactly the layer where
///     CE-4's 49-byte Termios bug was invisible to every existing test.
/// </summary>
/// <remarks>
///     <para>
///         <b>Mechanics:</b> posix_openpt/grantpt/unlockpt → posix_spawnp with
///         POSIX_SPAWN_SETSID + file-actions open(slavePath) onto fd 0 and
///         dup2 onto 1/2 (fresh open after setsid ⇒ controlling-tty
///         acquisition, like a login shell). No fork-in-managed-code hazards;
///         everything is async-signal-safe libc surface.
///     </para>
///     <para>
///         <b>Platform:</b> Linux only (CI platform). Windows ConPTY is a
///         follow-up — callers guard via <see cref="RequireLinux" />.
///     </para>
/// </remarks>
public sealed class PtySession : IAsyncDisposable
{
    private readonly int _masterFd;
    private readonly int _slaveFd;
    private readonly int _pid;
    private readonly Task<int> _exitTask;
    private readonly Thread _readerThread;
    private readonly List<byte> _raw = [];
    private readonly object _rawLock = new();
    private readonly object _writeLock = new();
    private readonly byte[] _initialTermios;

    private PtySession(int masterFd, int slaveFd, int pid, byte[] initialTermios)
    {
        _masterFd = masterFd;
        _slaveFd = slaveFd;
        _pid = pid;
        _initialTermios = initialTermios;

        _exitTask = Task.Run(
            () =>
            {
                _ = LibcNative.waitpid(_pid, out int status, 0);
                return DecodeStatus(status);
            });

        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "PtySession.Reader",
        };
        _readerThread.Start();
    }

    /// <summary>false off-Linux.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>All bytes ever produced by the child, in arrival order. Snapshot copy.</summary>
    public byte[] RawOutput
    {
        get
        {
            lock (_rawLock)
            {
                return [.. _raw];
            }
        }
    }

    /// <summary><see cref="RawOutput" /> decoded as UTF-8 (lossy on split sequences at snapshot edges).</summary>
    public string RawText => Encoding.UTF8.GetString(RawOutput);

    public bool HasExited => _exitTask.IsCompleted;

    /// <summary>Exit code once exited; throws before that. -1 when SIGKILLed on timeout.</summary>
    public int ExitCode => _exitTask.IsCompleted ? _exitTask.Result : throw new InvalidOperationException("Process has not exited.");

    /// <summary>TUnit skip off-Linux — Windows ConPTY is an explicit follow-up.</summary>
    public static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("PTY scenarios are Linux-only in CE-5 (Windows ConPTY is a follow-up).");
        }
    }

    /// <summary>Spawn <paramref name="spec" /> inside a fresh PTY of the given geometry.</summary>
    public static PtySession Start(PtyStartSpec spec)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("PtySession requires Linux (ConPTY follow-up).");
        }

        int master = LibcNative.posix_openpt(LibcNative.O_RDWR | LibcNative.O_NOCTTY | LibcNative.O_CLOEXEC);
        if (master < 0)
        {
            throw new IOException($"posix_openpt failed: errno={Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (LibcNative.grantpt(master) != 0 || LibcNative.unlockpt(master) != 0)
            {
                throw new IOException($"grantpt/unlockpt failed: errno={Marshal.GetLastWin32Error()}.");
            }

            string slavePath = LibcNative.GetSlaveName(master);
            ApplySize(master, spec.Cols, spec.Rows);

            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                env[(string)entry.Key] = (string)entry.Value!;
            }

            if (spec.Environment is not null)
            {
                foreach (var kv in spec.Environment)
                {
                    env[kv.Key] = kv.Value;
                }
            }

            // The child opens the slave itself via spawn file actions; the
            // test process never needs a slave fd of its own.
            // Termios baseline BEFORE the child can touch raw mode — the
            // reference point for CE-5 З.8 (restore-after-exit assertions).
            byte[] initialTermios = CaptureTermiosOn(master);
            int pid = LibcNative.SpawnInPty(spec.FileName, spec.Args, env, slavePath, spec.SearchPath);
            return new PtySession(master, -1, pid, initialTermios);
        }
        catch
        {
            _ = LibcNative.close(master);
            throw;
        }
    }

    // ── Input side ─────────────────────────────────────────────────────────

    /// <summary>Write raw bytes to the master (→ child stdin).</summary>
    public void Write(byte[] bytes)
    {
        lock (_writeLock)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int n = LibcNative.write(_masterFd, ref bytes[offset], bytes.Length - offset);
                if (n < 0)
                {
                    if (Marshal.GetLastWin32Error() == 4)
                    {
                        continue; // EINTR — retry the same range
                    }

                    throw new IOException($"write(master) failed: errno={Marshal.GetLastWin32Error()}.");
                }

                offset += n;
            }
        }
    }

    /// <summary>Write UTF-8 bytes verbatim (no newline appended).</summary>
    public void SendKey(string ansiSequence) => Write(Encoding.UTF8.GetBytes(ansiSequence));

    /// <summary>Write text + '\n' (the byte the raw parser maps to Enter).</summary>
    public void WriteLine(string text) => Write(Encoding.UTF8.GetBytes(text + "\n"));

    /// <summary>TIOCSWINSZ on the master; the kernel shares winsize with the slave side.</summary>
    public ValueTask ResizeAsync(int cols, int rows)
    {
        ApplySize(_masterFd, cols, rows);
        return ValueTask.CompletedTask;
    }

    // ── Output side ────────────────────────────────────────────────────────

    /// <summary>Poll until <paramref name="predicate" /> matches cumulative output or timeout elapses.</summary>
    public async Task<bool> WaitForOutputAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate(RawText))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    ///     Poll until <paramref name="needle" /> appears in decoded output.
    ///     ⚠ Matches the RAW master-byte stream, where the app emits
    ///     cursor-positioned runs — streamed timeline text never forms a
    ///     contiguous phrase byte-wise. Assert screen CONTENT on an ANSI
    ///     screen emulation (ConsoleExPtyScenarioBase.WaitForScreenAsync)
    ///     instead; raw needles are valid only for atomic control sequences
    ///     (alt-screen enter/leave) and short single-run fragments.
    /// </summary>
    public Task<bool> WaitForTextAsync(string needle, TimeSpan? timeout = null) =>
        WaitForOutputAsync(text => text.Contains(needle, StringComparison.Ordinal), timeout ?? TimeSpan.FromSeconds(10));

    /// <summary>Length of <see cref="RawOutput" /> right now — phase marker for scoped assertions.</summary>
    public int OutputLength
    {
        get
        {
            lock (_rawLock)
            {
                return _raw.Count;
            }
        }
    }

    /// <summary>Cumulative output truncated to start at raw offset <paramref name="from" />.</summary>
    public byte[] RawOutputFrom(int from)
    {
        lock (_rawLock)
        {
            return _raw.Count <= from ? [] : [.. _raw.Skip(from)];
        }
    }

    // ── Termios ────────────────────────────────────────────────────────────

    /// <summary>60-byte kernel view of the slave termios via tcgetattr(master).</summary>
    public byte[] CaptureTermios() => CaptureTermiosOn(_masterFd);

    /// <summary>Termios snapshot taken BEFORE the child was spawned (pre-raw baseline).</summary>
    public byte[] InitialTermios => [.. _initialTermios];

    private static byte[] CaptureTermiosOn(int fd)
    {
        var t = new LibcNative.TermiosKernel { Cc = new byte[32] };
        if (LibcNative.tcgetattr(fd, ref t) != 0)
        {
            throw new IOException($"tcgetattr(master) failed: errno={Marshal.GetLastWin32Error()}.");
        }

        int size = Marshal.SizeOf<LibcNative.TermiosKernel>();
        nint ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(t, ptr, false);
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ── Exit ───────────────────────────────────────────────────────────────

    /// <summary>Wait for exit; SIGKILL and return -1 past <paramref name="timeout" />.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        Task completed = await Task.WhenAny(_exitTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != _exitTask)
        {
            Kill();
            await _exitTask.ConfigureAwait(false);
            return -1;
        }

        return await _exitTask.ConfigureAwait(false);
    }

    /// <summary>SIGKILL the child (best-effort, safe on reaped processes).</summary>
    public void Kill()
    {
        try
        {
            _ = LibcNative.kill(_pid, LibcNative.SIGKILL);
        }
        catch
        {
            /* already gone */
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Kill();

        // Closing the master makes the reader's next read fail (EIO/EOF).
        _ = LibcNative.close(_masterFd);
        await _exitTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static void ApplySize(int fd, int cols, int rows)
    {
        var ws = new LibcNative.WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        if (LibcNative.ioctl(fd, LibcNative.TIOCSWINSZ, ref ws) != 0)
        {
            throw new IOException($"ioctl(TIOCSWINSZ) failed: errno={Marshal.GetLastWin32Error()}.");
        }
    }

    private static int DecodeStatus(int status) => (status & 0x7f) == 0
        ? (status >> 8) & 0xff
        : 128 + (status & 0x7f); // signaled → 128+sig convention

    private void ReaderLoop()
    {
        var buf = new byte[16384];
        while (true)
        {
            int n = LibcNative.read(_masterFd, buf, buf.Length);
            if (n < 0 && Marshal.GetLastWin32Error() == 4)
            {
                continue; // EINTR — retry, the fd is still alive
            }

            if (n <= 0)
            {
                return; // EOF/EIO after hangup or dispose — normal teardown
            }

            lock (_rawLock)
            {
                for (int i = 0; i < n; i++)
                {
                    _raw.Add(buf[i]);
                }
            }
        }
    }
}
