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
    private readonly string? _screenshotDir;
    private Process? _process;
    private Process? _xvfbProcess;
    private Process? _terminalProcess;
    private string? _display;
    private CancellationTokenSource? _readerCts;
    private StringBuilder _screen = new();
    private StringBuilder _rawAnsi = new(); // Keep raw ANSI for screenshot rendering
    private readonly AnsiTerminalBuffer _terminalBuffer = new(120, 50);
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
    /// <param name="screenshotDir">
    ///     Optional directory for screenshot capture. If provided, screenshots will
    ///     be taken using Xvfb + terminal emulator. If null, no screenshots.
    /// </param>
    public TuiDriver(string projectRelativePath, string tuiName, string? screenshotDir = null)
    {
        _projectRelativePath = projectRelativePath;
        _tuiName = tuiName;
        _screenshotDir = screenshotDir;
        if (screenshotDir is not null)
        {
            Directory.CreateDirectory(screenshotDir);
        }
    }

    /// <inheritdoc />
    public bool IsRunning =>
        (_process is { HasExited: false }) ||
        (_terminalProcess is { HasExited: false });

    /// <inheritdoc />
    public async Task StartAsync(string[] args, IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "TuiDriver on Windows requires a ConPTY-backed implementation. " +
                "See docs/E2E_TESTING.md → 'Platform notes' for the Windows path.");
        }

        if (_process is { HasExited: false })
            throw new InvalidOperationException("TuiDriver already running. Call WaitForExitAsync or StopAsync first.");

        // If screenshot directory is provided, start Xvfb and use terminal emulator
        if (_screenshotDir is not null)
        {
            await StartXvfbAsync(ct).ConfigureAwait(false);
            await StartTerminalEmulatorAsync(args, env, ct).ConfigureAwait(false);
        }
        else
        {
            // Use original PTY approach for non-screenshot mode
            await StartPtyModeAsync(args, env, ct).ConfigureAwait(false);
        }
    }

    private async Task StartXvfbAsync(CancellationToken ct)
    {
        // Try to find an available display starting from :98 (avoid :99 used by Avalonia)
        for (int displayNum = 98; displayNum >= 90; displayNum--)
        {
            string display = $":{displayNum}";
            string lockFile = $"/tmp/.X{displayNum}-lock";

            if (!File.Exists(lockFile))
            {
                _display = display;
                break;
            }
        }

        if (_display is null)
            throw new InvalidOperationException("Could not find available display for Xvfb");

        var psi = new ProcessStartInfo
        {
            FileName = "Xvfb",
            Arguments = $"{_display} -screen 0 1280x720x24 -ac +extension GLX +render -noreset",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        _xvfbProcess = Process.Start(psi);
        if (_xvfbProcess is null)
            throw new InvalidOperationException("Failed to start Xvfb");

        await Task.Delay(500, ct).ConfigureAwait(false);

        if (_xvfbProcess.HasExited)
            throw new InvalidOperationException($"Xvfb exited immediately with code {_xvfbProcess.ExitCode}");
    }

    private async Task StartTerminalEmulatorAsync(string[] args, IDictionary<string, string>? env, CancellationToken ct)
    {
        // Resolve the built DLL
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
        string dotnetArgs = $"exec {assemblyPath} " + string.Join(" ", args.Select(a => $"\"{a}\""));

        // Use xterm with explicit geometry and font
        var terminalPsi = new ProcessStartInfo
        {
            FileName = "xterm",
            Arguments = $"-geometry 120x50 -fa \"Monospace\" -fs 12 -bg black -fg white -e {host} {dotnetArgs}",
            Environment =
            {
                ["DISPLAY"] = _display!,
                ["HARBOR_TUI"] = _tuiName,
                ["TERM"] = "xterm-256color"
            },
            UseShellExecute = false
        };

        if (env is not null)
        {
            foreach ((string k, string v) in env)
                terminalPsi.Environment[k] = v;
        }

        _terminalProcess = Process.Start(terminalPsi);
        if (_terminalProcess is null)
            throw new InvalidOperationException("Failed to start terminal emulator");

        // Wait a bit for terminal to start
        await Task.Delay(1000, ct).ConfigureAwait(false);

        // For terminal emulator mode, we can't easily capture PTY output
        // So we'll rely on window screenshots instead
        _screen = new StringBuilder();
        _rawAnsi = new StringBuilder();
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    private Task StartPtyModeAsync(string[] args, IDictionary<string, string>? env, CancellationToken ct)
    {
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
        _rawAnsi = new StringBuilder();
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
        // We also keep raw ANSI for screenshot rendering.
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
                lock (_rawAnsi)
                {
                    _rawAnsi.Append(chunk);
                }
                lock (_terminalBuffer)
                {
                    _terminalBuffer.Write(chunk);
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
        if (_terminalProcess is not null)
        {
            await SendInputToTerminalWindowAsync(input, ct).ConfigureAwait(false);
            return;
        }

        if (_stdinWriter is null)
            throw new InvalidOperationException("TuiDriver not started.");
        await _stdinWriter.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
        await _stdinWriter.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendKeyAsync(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None, CancellationToken ct = default)
    {
        if (_terminalProcess is not null)
        {
            string xKey = ToXdotoolKey(key, modifiers);
            await ExecuteXdotoolAsync(xKey, ct).ConfigureAwait(false);
            return;
        }

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

    /// <summary>
    ///     Read the current visible terminal grid (2D screen buffer). Unlike
    ///     <see cref="ReadScreenAsync" /> which returns the append-only log of
    ///     all output, this returns only what is currently visible on screen —
    ///     reflecting cursor positioning, scrolling, and overwrites.
    /// </summary>
    public Task<string> ReadGridAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_terminalBuffer.GetVisibleText());
    }

    /// <summary>
    ///     Get the raw ANSI output from the PTY (without stripping escape sequences).
    ///     This can be used for screenshot rendering with ANSI-capable tools.
    /// </summary>
    public string ReadRawAnsi()
    {
        lock (_rawAnsi)
        {
            return _rawAnsi.ToString();
        }
    }

    /// <summary>
    ///     Capture the current TUI screen to a text file (ANSI-preserved).
    ///     The output can be rendered with tools like `ansi2html` or `TerminalImageViewer`.
    ///     Only works in PTY mode, not terminal emulator mode.
    /// </summary>
    /// <param name="path">Output file path</param>
    public async Task CaptureScreenAsync(string path, CancellationToken ct = default)
    {
        if (_terminalProcess is not null)
            throw new InvalidOperationException("ANSI capture not available in terminal emulator mode. Use ScreenshotAsync instead.");

        string rawAnsi = ReadRawAnsi();
        await File.WriteAllTextAsync(path, rawAnsi, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Capture the current visible TUI screen as a PNG image.
    ///     Works in PTY mode: waits for the screen buffer, then renders it via
    ///     headless Chromium (or ImageMagick fallback).
    /// </summary>
    public async Task CapturePngAsync(string path, CancellationToken ct = default)
    {
        if (_terminalProcess is not null)
            throw new InvalidOperationException("CapturePngAsync requires PTY mode. Pass no screenshotDir to TuiDriver.");

        string html;
        lock (_terminalBuffer)
        {
            html = _terminalBuffer.ToHtml();
        }
        await TerminalScreenshotRenderer.RenderHtmlToPngAsync(html, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Take a screenshot of the TUI window.
    ///     Only works in terminal emulator mode with Xvfb.
    /// </summary>
    /// <param name="name">Screenshot filename (without extension)</param>
    /// <returns>Path to the saved PNG, or null if screenshot failed</returns>
    public async Task<string?> ScreenshotAsync(string name, CancellationToken ct = default)
    {
        if (_screenshotDir is null || _display is null)
            return null; // Screenshots only supported when screenshotDir is provided

        string outputPath = Path.Combine(_screenshotDir, $"{name}.png");

        // Find the xterm window
        var findWindowPsi = new ProcessStartInfo
        {
            FileName = "xwininfo",
            Arguments = "-root -tree",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { ["DISPLAY"] = _display }
        };

        string? windowId = null;
        using (var findWindow = Process.Start(findWindowPsi))
        {
            if (findWindow is not null)
            {
                string output = await findWindow.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await findWindow.WaitForExitAsync(ct).ConfigureAwait(false);

                // Look for xterm window
                foreach (string line in output.Split('\n'))
                {
                    if ((line.Contains("xterm") || line.Contains("XTerm")) && line.Contains("0x"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"(0x[0-9a-f]+)");
                        if (match.Success)
                        {
                            windowId = match.Groups[1].Value;
                            break;
                        }
                    }
                }
            }
        }

        if (windowId is not null)
        {
            // Capture specific window
            var capturePsi = new ProcessStartInfo
            {
                FileName = "import",
                Arguments = $"-window {windowId} \"{outputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                Environment = { ["DISPLAY"] = _display }
            };

            using var capture = Process.Start(capturePsi);
            if (capture is not null)
            {
                await capture.WaitForExitAsync(ct).ConfigureAwait(false);
                if (capture.ExitCode == 0 && File.Exists(outputPath))
                    return outputPath;
            }
        }

        return null; // Screenshot failed
    }

    /// <inheritdoc />
    public async Task<bool> WaitForTextAsync(string pattern, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_terminalProcess is not null)
        {
            // Terminal emulator mode: poll for text via xdotool window title,
            // then fallback to ReadScreenAsync (PTY buffer) with a delay.
            Console.WriteLine($"DEBUG [TuiDriver] WaitForTextAsync: polling for '{pattern}' in terminal-emulator mode");
            var deadline = timeout ?? TimeSpan.FromSeconds(10);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // Try xdotool getwindowfocus getwindowname first
                string? windowTitle = await TryGetWindowTitleAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(windowTitle) && windowTitle.Contains(pattern, StringComparison.Ordinal))
                    return true;

                // Fallback: try ReadScreenAsync (works in PTY mode, may be empty in terminal emulator mode)
                string screen = await ReadScreenAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(screen) && screen.Contains(pattern, StringComparison.Ordinal))
                    return true;

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            // Pattern was not found within the deadline. In terminal emulator mode
            // we can't reliably read the screen, so report failure honestly.
            Console.WriteLine($"WARN [TuiDriver] terminal-emulator text polling unavailable, pattern '{pattern}' not found within timeout");
            return false;
        }

        var ptyDeadline = TimeSpan.FromSeconds(10);
        if (timeout is { } t) ptyDeadline = t;
        var ptySw = Stopwatch.StartNew();
        while (ptySw.Elapsed < ptyDeadline)
        {
            ct.ThrowIfCancellationRequested();
            string screen = await ReadScreenAsync(ct).ConfigureAwait(false);
            if (screen.Contains(pattern, StringComparison.Ordinal))
                return true;
            // Also check the AnsiTerminalBuffer 2D grid — renderers like
            // Spectre.Console use cursor positioning to overwrite text, so the
            // flat ANSI-stripped buffer may not contain text that IS visible
            // on the actual screen.
            lock (_terminalBuffer)
            {
                if (_terminalBuffer.ContainsText(pattern))
                    return true;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = TimeSpan.FromSeconds(30);
        if (timeout is { } t) deadline = t;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        // Use terminal process if in screenshot mode, otherwise use PTY process
        Process? targetProcess = _terminalProcess ?? _process;
        if (targetProcess is null)
            throw new InvalidOperationException("TuiDriver not started.");

        try
        {
            await targetProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return targetProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { targetProcess.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            try { targetProcess.WaitForExit(2000); }
            catch
            { /* ignore */
            }
            return -1;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        if (_process is { HasExited: false } proc)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            try { proc.WaitForExit(2000); }
            catch
            { /* ignore */
            }
        }

        if (_terminalProcess is { HasExited: false } termProc)
        {
            try { termProc.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            try { termProc.WaitForExit(2000); }
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

        // Close stdin/stdout/stderr readers + writers defensively
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
        _terminalProcess?.Dispose();

        // Cleanup Xvfb
        if (_xvfbProcess is not null)
        {
            try
            {
                if (!_xvfbProcess.HasExited)
                {
                    _xvfbProcess.Kill(entireProcessTree: true);
                    _xvfbProcess.WaitForExit(2000);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
            _xvfbProcess.Dispose();
            _xvfbProcess = null;
        }

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
        bool alt = (modifiers & ConsoleModifiers.Alt) != 0;

        // Ctrl+letter → 0x01..0x1A
        if (ctrl && key is >= ConsoleKey.A and <= ConsoleKey.Z)
        {
            return char.ToString((char)(key - ConsoleKey.A + 1));
        }

        // Alt+digit → ESC + digit (e.g. Alt+1 → ESC 1)
        if (alt && key is >= ConsoleKey.D0 and <= ConsoleKey.D9)
        {
            char digit = (char)('0' + (key - ConsoleKey.D0));
            return "\u001b" + digit;
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

    private async Task SendInputToTerminalWindowAsync(string input, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input))
            return;

        var textBuffer = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            switch (ch)
            {
                case '\r':
                case '\n':
                    await FlushTypedTextAsync(textBuffer, ct).ConfigureAwait(false);
                    await ExecuteXdotoolAsync("Return", ct).ConfigureAwait(false);
                    break;
                case '\t':
                    await FlushTypedTextAsync(textBuffer, ct).ConfigureAwait(false);
                    await ExecuteXdotoolAsync("Tab", ct).ConfigureAwait(false);
                    break;
                case '\u001b':
                    await FlushTypedTextAsync(textBuffer, ct).ConfigureAwait(false);
                    await ExecuteXdotoolAsync("Escape", ct).ConfigureAwait(false);
                    break;
                default:
                    textBuffer.Append(ch);
                    break;
            }
        }

        await FlushTypedTextAsync(textBuffer, ct).ConfigureAwait(false);
    }

    private async Task FlushTypedTextAsync(StringBuilder textBuffer, CancellationToken ct)
    {
        if (textBuffer.Length == 0)
            return;

        string text = textBuffer.ToString();
        textBuffer.Clear();

        string? windowId = await TryGetTerminalWindowIdAsync(ct).ConfigureAwait(false);
        if (windowId is null)
            throw new InvalidOperationException("Could not resolve xterm window id for terminal input.");

        var psi = new ProcessStartInfo
        {
            FileName = "xdotool",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["DISPLAY"] = _display! }
        };
        psi.ArgumentList.Add("type");
        psi.ArgumentList.Add("--window");
        psi.ArgumentList.Add(windowId);
        psi.ArgumentList.Add("--delay");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(text);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start xdotool type process.");

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("xdotool type failed to deliver text input.");
    }

    private async Task ExecuteXdotoolAsync(string keyChord, CancellationToken ct)
    {
        string? windowId = await TryGetTerminalWindowIdAsync(ct).ConfigureAwait(false);
        if (windowId is null)
            throw new InvalidOperationException("Could not resolve xterm window id for terminal key input.");

        var psi = new ProcessStartInfo
        {
            FileName = "xdotool",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["DISPLAY"] = _display! }
        };
        psi.ArgumentList.Add("key");
        psi.ArgumentList.Add("--window");
        psi.ArgumentList.Add(windowId);
        psi.ArgumentList.Add(keyChord);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start xdotool key process.");

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"xdotool key failed for '{keyChord}'.");
    }

    private async Task<string?> TryGetTerminalWindowIdAsync(CancellationToken ct)
    {
        if (_display is null)
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = "xwininfo",
            Arguments = "-root -tree",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { ["DISPLAY"] = _display }
        };

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        string output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
            return null;

        foreach (string line in output.Split('\n'))
        {
            if ((line.Contains("xterm", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("XTerm", StringComparison.OrdinalIgnoreCase)) &&
                line.Contains("0x", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(0x[0-9a-f]+)");
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }

        return null;
    }

    private async Task<string?> TryGetWindowTitleAsync(CancellationToken ct)
    {
        if (_display is null)
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = "xdotool",
            Arguments = "getwindowfocus getwindowname",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { ["DISPLAY"] = _display }
        };

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        string output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
            return null;

        return output.Trim();
    }

    private static string ToXdotoolKey(ConsoleKey key, ConsoleModifiers modifiers)
    {
        string baseKey = key switch
        {
            ConsoleKey.Enter => "Return",
            ConsoleKey.Escape => "Escape",
            ConsoleKey.Tab => "Tab",
            ConsoleKey.Backspace => "BackSpace",
            ConsoleKey.UpArrow => "Up",
            ConsoleKey.DownArrow => "Down",
            ConsoleKey.RightArrow => "Right",
            ConsoleKey.LeftArrow => "Left",
            ConsoleKey.Home => "Home",
            ConsoleKey.End => "End",
            ConsoleKey.PageUp => "Page_Up",
            ConsoleKey.PageDown => "Page_Down",
            ConsoleKey.F12 => "F12",
            ConsoleKey.F11 => "F11",
            ConsoleKey.F10 => "F10",
            ConsoleKey.F9 => "F9",
            ConsoleKey.F8 => "F8",
            ConsoleKey.F7 => "F7",
            ConsoleKey.F6 => "F6",
            ConsoleKey.F5 => "F5",
            ConsoleKey.F4 => "F4",
            ConsoleKey.F3 => "F3",
            ConsoleKey.F2 => "F2",
            ConsoleKey.F1 => "F1",
            ConsoleKey.Spacebar => "space",
            _ when key is >= ConsoleKey.A and <= ConsoleKey.Z => key.ToString().ToLowerInvariant(),
            _ => key.ToString()
        };

        if ((modifiers & ConsoleModifiers.Control) != 0)
            return $"ctrl+{baseKey}";
        if ((modifiers & ConsoleModifiers.Shift) != 0)
            return $"shift+{baseKey}";
        if ((modifiers & ConsoleModifiers.Alt) != 0)
            return $"alt+{baseKey}";

        return baseKey;
    }
}
