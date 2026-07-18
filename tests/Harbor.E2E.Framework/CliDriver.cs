namespace Harbor.E2E.Framework;

/// <summary>
///     <see cref="IE2eDriver" /> implementation for one-shot CLI commands.
///     Wraps <see cref="System.Diagnostics.Process" /> with redirected stdin /
///     stdout / stderr and exposes the captured stdout as the "screen".
/// </summary>
/// <remarks>
///     <para>
///         <b>Use cases:</b>
///         <list type="bullet">
///             <item><c>harbor --version</c></item>
///             <item><c>harbor providers</c></item>
///             <item><c>harbor ask "..."</c> (returns one completion then exits)</item>
///         </list>
///     </para>
///     <para>
///         <b>Not for:</b> the interactive REPL (use <see cref="TuiDriver"/> with
///         <c>HARBOR_TUI=plain</c>) or any TUI renderer (use <see cref="TuiDriver"/>).
///     </para>
///     <para>
///         <b>Build assumption:</b> the target project is built before the test
///         runs (<c>dotnet build</c>). The driver invokes
///         <c>dotnet exec &lt;app.dll&gt; &lt;args&gt;</c> against the assembly
///         produced by the most recent build, NOT <c>dotnet run</c> (which would
///         re-evaluate restore/build and is needlessly slow under test).
///     </para>
/// </remarks>
public sealed class CliDriver : IE2eDriver
{
    private readonly string _projectPath;
    private Process? _process;
    private StringBuilder _stdout = new();
    private StringBuilder _stderr = new();
    private StreamReader? _stdoutReader;
    private StreamReader? _stderrReader;
    private StreamWriter? _stdinWriter;

    /// <summary>
    ///     Create a driver targeting the Harbor app at <paramref name="projectRelativePath" />
    ///     (relative to the repo root, e.g. <c>apps/Harbor.App.Cli/Harbor.App.Cli.csproj</c>).
    /// </summary>
    public CliDriver(string projectRelativePath)
    {
        _projectPath = HarborAppLocator.ResolveProjectPath(projectRelativePath);
    }

    /// <inheritdoc />
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    ///     Captured stderr from the wrapped process. Available after
    ///     <see cref="StartAsync"/>; safe to read after <see cref="WaitForExitAsync"/>.
    ///     Test code uses this for diagnostics when a CLI command returns a
    ///     non-zero exit code (the console logger is silenced at Warning level
    ///     so Info/Debug messages don't pollute stdout assertions).
    /// </summary>
    public Task<string> ReadStderrAsync(CancellationToken ct = default)
    {
        lock (_stderr)
            return Task.FromResult(_stderr.ToString());
    }

    /// <inheritdoc />
    public Task StartAsync(string[] args, IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        if (_process is { HasExited: false })
            throw new InvalidOperationException("CliDriver already running. Call WaitForExitAsync or StopAsync first.");

        // Resolve the built DLL — assumes `dotnet build` was run beforehand.
        string projectDir = Path.GetDirectoryName(_projectPath) ?? ".";
        string projectName = Path.GetFileNameWithoutExtension(_projectPath);
        string assemblyPath = Path.Combine(projectDir, "bin", "Debug", "net10.0", projectName + ".dll");
        if (!File.Exists(assemblyPath))
        {
            // Fall back to Release config in case the test was run after a Release build.
            string releasePath = Path.Combine(projectDir, "bin", "Release", "net10.0", projectName + ".dll");
            if (File.Exists(releasePath))
                assemblyPath = releasePath;
        }

        string host = HarborAppLocator.ResolveDotnetHost();
        var psi = new ProcessStartInfo
        {
            FileName = host,
            UseShellExecute = false,
            RedirectStandardInput = true,
            // Set the working directory to the project root so ASP.NET Core
            // apps (Blazor, MAUI) find their wwwroot / ContentRoot relative to
            // the project dir, not the test runner's CWD. CLI tests don't care
            // about CWD so this is safe across all drivers.
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Tell the child .NET process to use UTF-8 on stdout/stderr so
            // non-ASCII characters (emoji in the welcome banner, etc.) survive.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(assemblyPath);
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        if (env is not null)
        {
            foreach ((string k, string v) in env)
                psi.Environment[k] = v;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _stdout = new StringBuilder();
        _stderr = new StringBuilder();

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start CLI process.");

        _stdoutReader = _process.StandardOutput;
        _stderrReader = _process.StandardError;
        _stdinWriter = _process.StandardInput;
        _stdinWriter.AutoFlush = true;

        // Drain stdout/stderr asynchronously so the buffer never deadlocks.
        _ = Task.Run(async () =>
        {
            char[] buf = new char[4096];
            int n;
            while ((n = await _stdoutReader.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                lock (_stdout)
                    _stdout.Append(buf, 0, n);
            }
        }, ct);
        _ = Task.Run(async () =>
        {
            char[] buf = new char[4096];
            int n;
            while ((n = await _stderrReader.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                lock (_stderr)
                    _stderr.Append(buf, 0, n);
            }
        }, ct);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendInputAsync(string input, CancellationToken ct = default)
    {
        if (_stdinWriter is null)
            throw new InvalidOperationException("CliDriver not started.");
        await _stdinWriter.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
        await _stdinWriter.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendKeyAsync(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None, CancellationToken ct = default)
    {
        // For a one-shot CLI, "keys" don't really exist — just synthesise the
        // ANSI representation and feed it as raw input. Most CLI tests won't
        // use this; it's here to satisfy the contract.
        char c = (char)key;
        return SendInputAsync(c.ToString(), ct);
    }

    /// <inheritdoc />
    public Task<string> ReadScreenAsync(CancellationToken ct = default)
    {
        lock (_stdout)
            return Task.FromResult(_stdout.ToString());
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
            await Task.Delay(75, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_process is null)
            throw new InvalidOperationException("CliDriver not started.");
        TimeSpan deadline = TimeSpan.FromSeconds(30);
        if (timeout is { } t) deadline = t;

        // Close stdin so the REPL (if any) sees EOF and exits.
        try { _stdinWriter?.Close(); } catch { /* ignore */ }

        if (!_process.WaitForExit((int)deadline.TotalMilliseconds))
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return -1;
        }
        // Give the async stdout/stderr drainers a chance to flush.
        await Task.Delay(50, ct).ConfigureAwait(false);
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
        _stdinWriter?.Dispose();
        _stdoutReader?.Dispose();
        _stderrReader?.Dispose();
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }
}
