namespace Harbor.E2E.Framework;

/// <summary>
///     <see cref="IE2eDriver" /> implementation for interactive TUI renderers.
///     Allocates a pseudo-terminal (PTY) and runs the Harbor CLI inside it with
///     <c>HARBOR_TUI=&lt;rendererName&gt;</c>, so renderers that use raw-mode
///     reads (<c>Console.ReadKey</c>) and ANSI cursor control work correctly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Linux/macOS:</b> uses the <c>script -qfc &lt;command&gt; /dev/null</c>
///         wrapper which opens a PTY pair, attaches the child's stdin/stdout/stderr
///         to the slave end, and connects the master end to our pipes. <c>script</c>
///         is shipped with util-linux and is present on every modern distro.
///     </para>
///     <para>
///         <b>Windows:</b> the proper approach is ConPTY
///         (<c>CreatePseudoConsole</c> + thread-pool pumping). That's a meaningful
///         chunk of P/Invoke code; the Windows-specific implementation is
///         intentionally left as <see cref="PlatformNotSupportedException"/> for
///         now. When the project is built on Windows, callers should swap in a
///         ConPTY-backed driver (see docs/E2E_TESTING.md → "Platform notes").
///     </para>
///     <para>
///         <b>Why PTY instead of redirected pipes?</b> The Spectre.Tui,
///         Terminal.Gui, and Termina renderers all call <c>Console.ReadKey(true)</c>
///         in raw mode and ANSI-write directly to <c>Console.Out</c>. When
///         stdin/stdout are pipes (not a TTY), <c>Console.ReadKey</c> throws or
///         returns immediately, and ANSI escape sequences can be dropped. A PTY
///         is the only way to faithfully exercise the rendering code path.
///     </para>
/// </remarks>
public sealed class TuiDriver : IE2eDriver
{
    private readonly string _projectRelativePath;
    private readonly string _tuiName;
    private Process? _process;
    private StringBuilder _screen = new();
    private StreamReader? _stdoutReader;
    private StreamReader? _stderrReader;
    private StreamWriter? _stdinWriter;

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

    /// <summary>
    ///     Whether the current OS + sandbox supports PTY allocation. TUI tests
    ///     that need a real PTY (SpectreTui, Termina, Terminal.Gui, RazorConsole)
    ///     should call this in a <c>[Skip]</c> guard so they no-op gracefully on
    ///     sandboxes that block <c>forkpty</c>/<c>openpty</c> (CI containers,
    ///     some Docker seccomp profiles, this dev sandbox).
    /// </summary>
    /// <remarks>
    ///     The check spawns <c>script -qfc "echo pty-ok" /dev/null</c> (the same
    ///     wrapper <see cref="StartAsync"/> uses) and verifies it prints the
    ///     expected output within 3 seconds. <c>script</c> uses
    ///     <c>openpty(3)</c> internally; if the sandbox blocks that call, the
    ///     child is SIGKILL'd and the check returns <see langword="false"/>.
    /// </remarks>
    public static bool IsPtyAvailable()
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "script",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-qfc");
            psi.ArgumentList.Add("echo pty-ok");
            psi.ArgumentList.Add("/dev/null");
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return false;
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { /* ignore */ } return false; }
            return stdout.Contains("pty-ok", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Skip reason string for tests that need a PTY but the current sandbox
    ///     doesn't allow one. Tests can pass this verbatim to <c>[Skip(...)]</c>.
    /// </summary>
    public const string NoPtySkipReason =
        "PTY allocation is blocked on this OS/sandbox (script(1) is killed). " +
        "TUI E2E tests require a real PTY — run on a Linux/macOS host with " +
        "unrestricted forkpty/openpty. See docs/E2E_TESTING.md.";

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
        // `script` flags:
        //   -q  quiet (no header/footer)
        //   -f  flush after each write (so test sees output promptly)
        //   -c <cmd>  run <cmd> in the PTY
        //   /dev/null  don't save typescript to a file
        //   -E always  exit when the child exits (default on most distros; explicit on busybox)
        string childCmd = FormattableString.Invariant($"{host} exec \"{assemblyPath}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "script",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-qfc");
        psi.ArgumentList.Add(childCmd);
        psi.ArgumentList.Add("/dev/null");
        // Pass through the HARBOR_TUI override (it can still be overridden by the
        // caller's env dict — we apply theirs last).
        psi.Environment["HARBOR_TUI"] = _tuiName;
        if (env is not null)
        {
            foreach ((string k, string v) in env)
                psi.Environment[k] = v;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _screen = new StringBuilder();

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start PTY-wrapped TUI process.");

        _stdoutReader = _process.StandardOutput;
        _stderrReader = _process.StandardError;
        _stdinWriter = _process.StandardInput;
        _stdinWriter.AutoFlush = true;

        // Drain stdout into the rolling screen buffer. Strip ANSI escape
        // sequences as we go so WaitForTextAsync can do a plain substring
        // search instead of having to deal with cursor-move sequences.
        _ = Task.Run(async () =>
        {
            char[] buf = new char[4096];
            int n;
            while ((n = await _stdoutReader.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                string chunk = new string(buf, 0, n);
                string clean = AnsiStripper.Strip(chunk);
                lock (_screen)
                    _screen.Append(clean);
            }
        }, ct);
        _ = Task.Run(async () =>
        {
            char[] buf = new char[4096];
            int n;
            while ((n = await _stderrReader.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                // Stderr is informational only — append to screen so error
                // messages from the TUI are visible to WaitForTextAsync.
                string chunk = new string(buf, 0, n);
                string clean = AnsiStripper.Strip(chunk);
                lock (_screen)
                    _screen.Append(clean);
            }
        }, ct);

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
            return Task.FromResult(_screen.ToString());
    }

    /// <inheritdoc />
    public async Task<bool> WaitForTextAsync(string pattern, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan deadline = TimeSpan.FromSeconds(10);
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
        TimeSpan deadline = TimeSpan.FromSeconds(30);
        if (timeout is { } t) deadline = t;

        if (!_process.WaitForExit((int)deadline.TotalMilliseconds))
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return -1;
        }
        await Task.Delay(100, ct).ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        if (_process is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.WaitForExit(2000); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Close stdin/stdout/stderr readers + writers defensively. If the PTY
        // was killed mid-write (sandbox SIGKILL of `script`), the pipe may be
        // broken; swallowing the IOException keeps teardown from masking the
        // real test failure.
        try { _stdinWriter?.Dispose(); } catch { /* pipe broken — ignore */ }
        try { _stdoutReader?.Dispose(); } catch { /* pipe broken — ignore */ }
        try { _stderrReader?.Dispose(); } catch { /* pipe broken — ignore */ }
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Map a <see cref="ConsoleKey"/> + modifiers to the ANSI byte sequence
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
            _ => char.ToString((char)key),
        };
    }
}
