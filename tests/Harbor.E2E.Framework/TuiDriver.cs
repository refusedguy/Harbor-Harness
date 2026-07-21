namespace Harbor.E2E.Framework;
/// <summary>
///     <see cref="IE2eDriver" /> implementation for interactive TUI renderers.
///     Allocates a pseudo-terminal (PTY) via Python's <c>pty.openpty()</c> and
///     runs the Harbor CLI inside it with <c>HARBOR_TUI=&lt;rendererName&gt;</c>,
///     so renderers that use raw-mode reads (<c>Console.ReadKey</c>) and ANSI
///     cursor control work correctly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why Python PTY?</b> .NET has no native PTY API on Linux. The
///         <c>script(1)</c> util-linux wrapper is the conventional choice but
///         is blocked by the dev/CI sandbox's seccomp profile (which kills
///         <c>script</c> with SIGKILL before it can exec the child). Python's
///         <c>pty</c> module calls <c>openpty(3)</c> directly from the parent
///         (no intermediate exec), and on this sandbox it succeeds. Python3 is
///         assumed present on every modern Linux distro and on macOS.
///     </para>
///     <para>
///         <b>Architecture:</b>
///         <code>
///   Test (C#)              Python wrapper               TUI child (.NET)
///   ─────────              ───────────────               ───────────────
///   ProcessStartInfo
///     python3 -u -c
///       &lt;script&gt;
///       dotnet
///       &lt;assembly.dll&gt;
///       &lt;args...&gt;
///                          ┌─ pty.openpty() → (master, slave)
///                          │  fcntl TIOCSWINSZ 50x120
///                          │
///                          ├─ os.fork()
///                          │   ├─ child:
///                          │   │   os.setsid()
///                          │   │   TIOCSCTTY(slave)
///                          │   │   dup2(slave, 0/1/2)
///                          │   │   execvp(dotnet, …)
///                          │   │                      →  Console.ReadKey(true)
///                          │   │                         Console.Out.Write(ANSI…)
///                          │   │
///                          │   └─ parent (Python):
///                          │       thread: stdin→master   ◀── C# writes stdin
///                          │       main:   master→stdout  ──▶ C# reads stdout
///                          │       signal handler:
///                          │         SIGTERM/SIGINT → kill(child)
///                          │
///                          └─ exit with child's exit code
///         </code>
///     </para>
///     <para>
///         <b>Windows:</b> the proper approach is ConPTY (<c>CreatePseudoConsole</c>
///         + thread-pool pumping). That's a meaningful chunk of P/Invoke code;
///         the Windows-specific implementation throws
///         <see cref="PlatformNotSupportedException" />. When the project is
///         built on Windows, callers should swap in a ConPTY-backed driver.
///     </para>
/// </remarks>
public sealed class TuiDriver : IE2eDriver
{
    /// <summary>
    ///     The embedded Python PTY-wrapper script. Runs the child (.NET host)
    ///     in a freshly-allocated PTY and bridges stdin/stdout between the
    ///     C# test process and the child. Installs SIGTERM/SIGINT handlers so
    ///     that <see cref="Process.Kill(bool)" /> cascades to the child even
    ///     though the child is in its own session (post-<c>setsid</c>).
    /// </summary>
    /// <remarks>
    ///     Passed to <c>python3 -u -c &lt;script&gt; &lt;dotnet&gt; &lt;dll&gt; &lt;args…&gt;</c>.
    ///     The <c>-u</c> flag forces unbuffered stdout/stderr so the test sees
    ///     TUI output immediately. The script reads <c>sys.argv[1:]</c> as the
    ///     child argv (no shell involved — <c>execvp</c> is called directly).
    /// </remarks>
    private const string PythonPtyScript = """
                                           import atexit, fcntl, os, pty, select, signal, struct, sys, termios, threading, time

                                           def set_size(fd, rows=50, cols=120):
                                               winsize = struct.pack('HHHH', rows, cols, 0, 0)
                                               fcntl.ioctl(fd, termios.TIOCSWINSZ, winsize)

                                           master, slave = pty.openpty()
                                           set_size(master)
                                           set_size(slave)

                                           child_args = sys.argv[1:]
                                           if not child_args:
                                               sys.stderr.write('tui-driver: missing child argv\n')
                                               sys.exit(2)

                                           pid = os.fork()
                                           if pid == 0:
                                               # Child: take over the slave end as its controlling tty + stdio.
                                               os.close(master)
                                               os.setsid()
                                               try:
                                                   fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
                                               except OSError:
                                                   pass
                                               os.dup2(slave, 0)
                                               os.dup2(slave, 1)
                                               os.dup2(slave, 2)
                                               if slave > 2:
                                                   os.close(slave)
                                               try:
                                                   os.execvp(child_args[0], child_args)
                                               except OSError as e:
                                                   sys.stderr.write('tui-driver: execvp failed: ' + str(e) + '\n')
                                                   os._exit(127)
                                           else:
                                               # Parent (Python): bridge C# stdin → master, and master → C# stdout.
                                               os.close(slave)

                                               def kill_child(signum=None, frame=None):
                                                   try:
                                                       os.kill(pid, signal.SIGTERM)
                                                       for _ in range(20):
                                                           try:
                                                               wpid, _ = os.waitpid(pid, os.WNOHANG)
                                                               if wpid != 0:
                                                                   break
                                                           except ChildProcessError:
                                                               break
                                                           time.sleep(0.025)
                                                       else:
                                                           try:
                                                               os.kill(pid, signal.SIGKILL)
                                                               os.waitpid(pid, 0)
                                                           except OSError:
                                                               pass
                                                   except OSError:
                                                       pass
                                                   if signum is not None:
                                                       sys.exit(0)

                                               signal.signal(signal.SIGTERM, kill_child)
                                               signal.signal(signal.SIGINT, kill_child)
                                               atexit.register(kill_child)

                                               def forward_stdin():
                                                   while True:
                                                       try:
                                                           data = os.read(sys.stdin.fileno(), 4096)
                                                       except OSError:
                                                           break
                                                       if not data:
                                                           break
                                                       try:
                                                           os.write(master, data)
                                                       except OSError:
                                                           break

                                               threading.Thread(target=forward_stdin, daemon=True).start()

                                               while True:
                                                   try:
                                                       data = os.read(master, 65536)
                                                   except OSError:
                                                       break
                                                   if not data:
                                                       break
                                                   sys.stdout.buffer.write(data)
                                                   sys.stdout.buffer.flush()

                                               try:
                                                   _, status = os.waitpid(pid, 0)
                                                   if os.WIFEXITED(status):
                                                       sys.exit(os.WEXITSTATUS(status))
                                                   if os.WIFSIGNALED(status):
                                                       sys.exit(128 + os.WTERMSIG(status))
                                               except ChildProcessError:
                                                   pass
                                               sys.exit(0)
                                           """;

    /// <summary>
    ///     Skip reason string for tests that need a PTY but the current sandbox
    ///     doesn't allow one. Tests can pass this verbatim to <c>[Skip(...)]</c>.
    /// </summary>
    public const string NoPtySkipReason =
        "PTY allocation is blocked on this OS/sandbox (python3 pty.openpty fails). " +
        "TUI E2E tests require a real PTY — run on a Linux/macOS host with " +
        "python3 and unrestricted openpty. See docs/E2E_TESTING.md.";

    private readonly string _projectRelativePath;
    private readonly string _tuiName;
    private Process? _process;
    private CancellationTokenSource? _readerCts;
    private StringBuilder _screen = new();
    private StreamReader? _stderrReader;
    private StreamWriter? _stdinWriter;
    private StreamReader? _stdoutReader;

    /// <summary>
    ///     Create a TUI driver.
    /// </summary>
    /// <param name="projectRelativePath">
    ///     The Harbor app project (typically <c>apps/Harbor.App.Cli/Harbor.App.Cli.csproj</c>).
    /// </param>
    /// <param name="tuiName">
    ///     Value of the <c>HARBOR_TUI</c> env var. One of:
    ///     <c>spectre-tui</c>, <c>termina</c>, <c>terminal-gui</c>, <c>razor</c>,
    ///     <c>plain</c>, <c>ansi</c>, <c>spectre</c>, <c>fullscreen</c>.
    /// </param>
    public TuiDriver(string projectRelativePath, string tuiName)
    {
        _projectRelativePath = projectRelativePath;
        _tuiName = tuiName;
    }

    /// <inheritdoc />
    public bool IsRunning => _process is { HasExited: false };

    /// <inheritdoc />
    public Task StartAsync(string[] args, IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "TuiDriver on Windows requires a ConPTY-backed implementation. " +
                "See docs/E2E_TESTING.md → 'Platform notes' for the Windows path.");
        }

        if (_process is { HasExited: false })
            throw new InvalidOperationException("TuiDriver already running. Call WaitForExitAsync or StopAsync first.");

        // Resolve the built DLL — same convention as CliDriver.
        string projectPath = HarborAppLocator.ResolveProjectPath(_projectRelativePath);
        string projectDir = Path.GetDirectoryName(projectPath) ?? ".";
        string projectName = Path.GetFileNameWithoutExtension(projectPath);
        string assemblyPath = Path.Combine(projectDir, "bin", "Debug", "net10.0", projectName + ".dll");
        if (!File.Exists(assemblyPath))
        {
            string releasePath = Path.Combine(projectDir, "bin", "Release", "net10.0", projectName + ".dll");
            if (File.Exists(releasePath))
                assemblyPath = releasePath;
        }

        string host = HarborAppLocator.ResolveDotnetHost();

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // -u   unbuffered stdout/stderr so the test sees TUI output immediately.
        // -c <script>  read the program from this argument string (no temp file).
        // <dotnet> <dll> <args...>  passed as sys.argv[1:] to the Python script,
        // which forwards them to os.execvp. No shell involved — safe escaping.
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(PythonPtyScript);
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(assemblyPath);
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        // Pass through the HARBOR_TUI override (it can still be overridden by
        // the caller's env dict — we apply theirs last so it wins).
        psi.Environment["HARBOR_TUI"] = _tuiName;
        // Force the child's TERM to a 256-colour xterm so renderers that probe
        // terminfo get a deterministic answer (the dev sandbox has no terminfo
        // db for "screen" or "tmux" in some minimal containers).
        psi.Environment["TERM"] = "xterm-256color";
        if (env is not null)
        {
            foreach ((string k, string v) in env)
                psi.Environment[k] = v;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _screen = new StringBuilder();
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start Python PTY-wrapped TUI process.");

        _stdoutReader = _process.StandardOutput;
        _stderrReader = _process.StandardError;
        _stdinWriter = _process.StandardInput;
        _stdinWriter.AutoFlush = true;

        // Drain stdout (the PTY master's output) into the rolling screen buffer.
        // ANSI escape sequences are stripped as we go so WaitForTextAsync can do
        // a plain substring search instead of dealing with cursor-move sequences.
        var readerToken = _readerCts.Token;
        _ = Task.Run(async () =>
        {
            char[] buf = new char[4096];
            int n;
            while ((n = await _stdoutReader.ReadAsync(buf, readerToken).ConfigureAwait(false)) > 0)
            {
                string chunk = new(buf, 0, n);
                string clean = AnsiStripper.Strip(chunk);
                lock (_screen)
                {
                    _screen.Append(clean);
                }
            }
        }, readerToken);
        _ = Task.Run(async () =>
        {
            char[] buf = new char[4096];
            int n;
            while ((n = await _stderrReader.ReadAsync(buf, readerToken).ConfigureAwait(false)) > 0)
            {
                // Stderr is informational only — append to screen so error
                // messages from the TUI are visible to WaitForTextAsync.
                string chunk = new(buf, 0, n);
                string clean = AnsiStripper.Strip(chunk);
                lock (_screen)
                {
                    _screen.Append(clean);
                }
            }
        }, readerToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendInputAsync(string input, CancellationToken ct = default)
    {
        if (_stdinWriter is null)
            throw new InvalidOperationException("TuiDriver not started.");
        await _stdinWriter.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
        await _stdinWriter.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendKeyAsync(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None, CancellationToken ct = default)
    {
        string seq = KeyToAnsi(key, modifiers);
        await SendInputAsync(seq, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string> ReadScreenAsync(CancellationToken ct = default)
    {
        lock (_screen)
        {
            return Task.FromResult(_screen.ToString());
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForTextAsync(string pattern, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = TimeSpan.FromSeconds(10);
        if (timeout is { } t) deadline = t;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string screen = await ReadScreenAsync(ct).ConfigureAwait(false);
            if (screen.Contains(pattern, StringComparison.Ordinal))
                return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_process is null)
            throw new InvalidOperationException("TuiDriver not started.");
        var deadline = TimeSpan.FromSeconds(30);
        if (timeout is { } t) deadline = t;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            try { _process.WaitForExit(2000); }
            catch
            { /* ignore */
            }
            return -1;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        if (_process is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            try { p.WaitForExit(2000); }
            catch
            { /* ignore */
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        // Close stdin/stdout/stderr readers + writers defensively. If the PTY
        // was killed mid-write (sandbox SIGKILL of the wrapper), the pipe may
        // be broken; swallowing the IOException keeps teardown from masking
        // the real test failure.
        try { _stdinWriter?.Dispose(); }
        catch
        { /* pipe broken — ignore */
        }
        try { _stdoutReader?.Dispose(); }
        catch
        { /* pipe broken — ignore */
        }
        try { _stderrReader?.Dispose(); }
        catch
        { /* pipe broken — ignore */
        }
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Whether the current OS + sandbox supports PTY allocation. TUI tests
    ///     that need a real PTY (SpectreTui, Termina, Terminal.Gui, RazorConsole)
    ///     should call this in a <c>[Skip]</c> guard so they no-op gracefully on
    ///     sandboxes that block <c>openpty</c>/<c>forkpty</c>.
    /// </summary>
    /// <remarks>
    ///     The check spawns
    ///     <c>
    ///         python3 -c "import pty, fcntl, termios, struct;
    ///         pty.openpty(); print('pty-ok')"
    ///     </c>
    ///     and verifies it prints the
    ///     expected output within 3 seconds. <c>pty.openpty</c> uses
    ///     <c>openpty(3)</c> internally; if the sandbox blocks that call (or
    ///     <c>python3</c> is missing), the check returns <see langword="false" />.
    /// </remarks>
    public static bool IsPtyAvailable()
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import fcntl, pty, struct, sys, termios; m, s = pty.openpty(); fcntl.ioctl(m, termios.TIOCSWINSZ, struct.pack('HHHH', 50, 120, 0, 0)); sys.stdout.write('pty-ok\\n')");
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return false;
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); }
                catch
                { /* ignore */
                }
                return false;
            }
            return stdout.Contains("pty-ok", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Map a <see cref="ConsoleKey" /> + modifiers to the ANSI byte sequence
    ///     a typical TUI expects. Covers the keys used by Harbor TUI tests:
    ///     Enter, Escape, Ctrl-C, Ctrl-P, F12, arrow keys. Unknown keys fall
    ///     back to the Unicode character of the key.
    /// </summary>
    private static string KeyToAnsi(ConsoleKey key, ConsoleModifiers modifiers)
    {
        bool ctrl = (modifiers & ConsoleModifiers.Control) != 0;
        bool shift = (modifiers & ConsoleModifiers.Shift) != 0;

        // Ctrl+letter → 0x01..0x1A
        if (ctrl && key is >= ConsoleKey.A and <= ConsoleKey.Z)
        {
            return char.ToString((char)(key - ConsoleKey.A + 1));
        }

        return key switch
        {
            ConsoleKey.Enter => "\r",
            ConsoleKey.Escape => "\u001b",
            ConsoleKey.Tab => shift ? "\u001b[Z" : "\t",
            ConsoleKey.Backspace => "\u007f",
            ConsoleKey.UpArrow => "\u001b[A",
            ConsoleKey.DownArrow => "\u001b[B",
            ConsoleKey.RightArrow => "\u001b[C",
            ConsoleKey.LeftArrow => "\u001b[D",
            ConsoleKey.Home => "\u001b[H",
            ConsoleKey.End => "\u001b[F",
            ConsoleKey.PageUp => "\u001b[5~",
            ConsoleKey.PageDown => "\u001b[6~",
            ConsoleKey.F12 => "\u001b[24~",
            ConsoleKey.F11 => "\u001b[23~",
            ConsoleKey.F10 => "\u001b[21~",
            ConsoleKey.F9 => "\u001b[20~",
            ConsoleKey.F8 => "\u001b[19~",
            ConsoleKey.F7 => "\u001b[18~",
            ConsoleKey.F6 => "\u001b[17~",
            ConsoleKey.F5 => "\u001b[15~",
            ConsoleKey.F4 => "\u001b[14~",
            ConsoleKey.F3 => "\u001b[13~",
            ConsoleKey.F2 => "\u001b[12~",
            ConsoleKey.F1 => "\u001b[11~",
            ConsoleKey.Spacebar => " ",
            _ => char.ToString((char)key)
        };
    }
}
