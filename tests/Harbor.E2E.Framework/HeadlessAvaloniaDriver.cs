namespace Harbor.E2E.Framework;

/// <summary>
///     Rendering mode for Avalonia E2E tests.
/// </summary>
public enum AvaloniaE2eMode
{
    /// <summary>
    ///     Use Avalonia.Headless with off-screen software rendering.
    ///     Fast and portable, but may differ from real UI rendering.
    /// </summary>
    Headless,

    /// <summary>
    ///     Use Xvfb (X Virtual Framebuffer) for accurate pixel-perfect rendering.
    ///     Requires Xvfb to be installed on Linux.
    /// </summary>
    Xvfb
}

/// <summary>
///     <see cref="IE2eDriver" /> implementation that drives the Harbor Avalonia
///     desktop app with support for both headless and Xvfb rendering modes.
/// </summary>
/// <remarks>
///     <para>
///         <b>Rendering modes:</b>
///         <list type="bullet">
///             <item>
///                 <c>HARBOR_AVALONIA_E2E_MODE=headless</c> (default): Uses
///                 <c>Avalonia.Headless</c> with off-screen software rendering.
///                 Fast, no external dependencies, but may differ from real UI.
///             </item>
///             <item>
///                 <c>HARBOR_AVALONIA_E2E_MODE=xvfb</c>: Uses Xvfb (X Virtual
///                 Framebuffer) for accurate pixel-perfect rendering matching
///                 real desktop. Requires Xvfb to be installed and available.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Why hybrid:</b> Headless mode is portable but may have rendering
///         differences from real UI. Xvfb mode provides accurate screenshots but
///         requires X server. This driver lets developers choose based on their
///         environment - CI can use headless for speed, local development can use
///         Xvfb for visual accuracy.
///     </para>
///     <para>
///         <b>Xvfb setup:</b> When Xvfb mode is requested, the driver:
///         <list type="number">
///             <item>Finds an available display (:99, :98, ...)</item>
///             <item>Starts Xvfb with 24-bit color at 1280x720</item>
///             <item>Sets DISPLAY environment variable</item>
///             <item>Runs the Avalonia app normally (not headless)</item>
///             <item>Takes screenshots via external tools (import/imagemagick)</item>
///         </list>
///     </para>
/// </remarks>
public sealed class HeadlessAvaloniaDriver : IE2eDriver
{
    private readonly string _projectRelativePath;
    private readonly AvaloniaE2eMode _mode;
    private CliDriver? _proxy;
    private Process? _xvfbProcess;
    private string? _display;
    private readonly string _screenshotDir;

    /// <summary>
    ///     Create a driver for the Avalonia app at <paramref name="projectRelativePath" />.
    /// </summary>
    /// <param name="projectRelativePath">Path to the Avalonia project .csproj</param>
    /// <param name="screenshotDir">Directory where screenshots will be saved</param>
    /// <param name="mode">Rendering mode (defaults to headless)</param>
    public HeadlessAvaloniaDriver(string projectRelativePath, string screenshotDir, AvaloniaE2eMode? mode = null)
    {
        _projectRelativePath = projectRelativePath;
        _screenshotDir = screenshotDir;
        _mode = mode ?? GetModeFromEnv();
        Directory.CreateDirectory(_screenshotDir);
    }

    private static AvaloniaE2eMode GetModeFromEnv()
    {
        string? env = Environment.GetEnvironmentVariable("HARBOR_AVALONIA_E2E_MODE");
        if (env is not null)
        {
            if (Enum.TryParse<AvaloniaE2eMode>(env, ignoreCase: true, out var parsed))
                return parsed;
        }
        return AvaloniaE2eMode.Headless; // default
    }

    /// <summary>
    ///     Whether the chosen mode is supported on the current OS.
    /// </summary>
    public bool IsSupportedOnCurrentOs
    {
        get
        {
            return _mode switch
            {
                AvaloniaE2eMode.Headless => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                AvaloniaE2eMode.Xvfb => OperatingSystem.IsLinux(),
                _ => false
            };
        }
    }

    /// <summary>
    ///     The active rendering mode.
    /// </summary>
    public AvaloniaE2eMode Mode => _mode;

    /// <inheritdoc />
    public bool IsRunning => _proxy?.IsRunning ?? false;

    /// <inheritdoc />
    public async Task StartAsync(string[] args, IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        if (!IsSupportedOnCurrentOs)
        {
            throw new PlatformNotSupportedException(
                $"Avalonia E2E mode '{_mode}' is not supported on the current OS. " +
                $"Headless works on Windows/Linux/macOS, Xvfb requires Linux.");
        }

        switch (_mode)
        {
            case AvaloniaE2eMode.Xvfb:
                await StartXvfbAsync(ct).ConfigureAwait(false);
                break;
            case AvaloniaE2eMode.Headless:
                // No setup needed for headless mode
                break;
            default:
                throw new InvalidOperationException($"Unknown mode: {_mode}");
        }

        // Prepare environment with DISPLAY if using Xvfb
        var effectiveEnv = env ?? new Dictionary<string, string>();
        if (_display is not null)
        {
            effectiveEnv = new Dictionary<string, string>(effectiveEnv)
            {
                ["DISPLAY"] = _display
            };
        }

        // Use CliDriver to run the app (it will use DISPLAY if set)
        _proxy = new CliDriver(_projectRelativePath);
        await _proxy.StartAsync(args, effectiveEnv, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendInputAsync(string input, CancellationToken ct = default)
    {
        EnsureProxy();
        return _proxy!.SendInputAsync(input, ct);
    }

    /// <inheritdoc />
    public Task SendKeyAsync(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None, CancellationToken ct = default)
    {
        EnsureProxy();
        return _proxy!.SendKeyAsync(key, modifiers, ct);
    }

    /// <inheritdoc />
    public Task<string> ReadScreenAsync(CancellationToken ct = default)
    {
        EnsureProxy();
        return _proxy!.ReadScreenAsync(ct);
    }

    /// <inheritdoc />
    public Task<bool> WaitForTextAsync(string pattern, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        EnsureProxy();
        return _proxy!.WaitForTextAsync(pattern, timeout, ct);
    }

    /// <inheritdoc />
    public Task<int> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        EnsureProxy();
        return _proxy!.WaitForExitAsync(timeout, ct);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default) => _proxy?.StopAsync(ct) ?? Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null)
            await _proxy.DisposeAsync().ConfigureAwait(false);

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
    }

    /// <summary>
    ///     Take a screenshot of the Avalonia window.
    ///     In Xvfb mode, uses xwininfo/import to capture the window.
    ///     In headless mode, this is not supported (returns null).
    /// </summary>
    /// <param name="name">Screenshot filename (without extension)</param>
    /// <returns>Path to the saved PNG, or null if screenshot failed</returns>
    public async Task<string?> ScreenshotAsync(string name, CancellationToken ct = default)
    {
        if (_mode != AvaloniaE2eMode.Xvfb)
            return null; // Screenshots only supported in Xvfb mode

        if (_display is null)
            throw new InvalidOperationException("Xvfb not started");

        string outputPath = Path.Combine(_screenshotDir, $"{name}.png");

        // Try to capture window using import (ImageMagick)
        // First, find the window ID
        var findWindowPsi = new ProcessStartInfo
        {
            FileName = "xwininfo",
            Arguments = "-root -tree",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        string? windowId = null;
        using (var findWindow = Process.Start(findWindowPsi))
        {
            if (findWindow is not null)
            {
                string output = await findWindow.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await findWindow.WaitForExitAsync(ct).ConfigureAwait(false);

                // Look for Harbor window (naive approach - first window with "Harbor" in title)
                foreach (string line in output.Split('\n'))
                {
                    if (line.Contains("Harbor") && line.Contains("0x"))
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

        if (windowId is null)
        {
            // Fallback: capture entire screen
            var capturePsi = new ProcessStartInfo
            {
                FileName = "import",
                Arguments = $"-window root \"{outputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var capture = Process.Start(capturePsi);
            if (capture is not null)
            {
                await capture.WaitForExitAsync(ct).ConfigureAwait(false);
                if (capture.ExitCode == 0 && File.Exists(outputPath))
                    return outputPath;
            }
        }
        else
        {
            // Capture specific window
            var capturePsi = new ProcessStartInfo
            {
                FileName = "import",
                Arguments = $"-window {windowId} \"{outputPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false
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

    private async Task StartXvfbAsync(CancellationToken ct)
    {
        // Try to find an available display starting from :99
        for (int displayNum = 99; displayNum >= 90; displayNum--)
        {
            string display = $":{displayNum}";
            string lockFile = $"/tmp/.X{displayNum}-lock";

            // Check if display is available
            if (!File.Exists(lockFile))
            {
                _display = display;
                break;
            }
        }

        if (_display is null)
            throw new InvalidOperationException("Could not find available display for Xvfb");

        // Start Xvfb
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

        // Wait a bit for Xvfb to start
        await Task.Delay(500, ct).ConfigureAwait(false);

        if (_xvfbProcess.HasExited)
            throw new InvalidOperationException($"Xvfb exited immediately with code {_xvfbProcess.ExitCode}");
    }

    private void EnsureProxy()
    {
        if (_proxy is null)
            throw new InvalidOperationException("HeadlessAvaloniaDriver not started.");
    }
}
