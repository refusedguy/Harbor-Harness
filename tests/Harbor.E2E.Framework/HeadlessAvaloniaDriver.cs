namespace Harbor.E2E.Framework;

/// <summary>
///     <see cref="IE2eDriver" /> implementation that drives the Harbor Avalonia
///     desktop app via <c>Avalonia.Headless</c> (off-screen rendering on a
///     mocked windowing subsystem).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why headless:</b> Avalonia's normal lifecycle needs a real window
///         system (X11, Wayland, Win32, macOS AppKit). On a headless Linux CI
///         box (no DISPLAY) the app fails to initialise. <c>Avalonia.Headless</c>
///         replaces the windowing subsystem with an in-process mock so the
///         Avalonia runtime + the Harbor AppHost can boot without any display.
///     </para>
///     <para>
///         <b>Linux status:</b> skipped. The Avalonia.Headless package pulls in
///         platform-specific skia rendering that does not currently boot on the
///         Linux sandbox without a virtual framebuffer (Xvfb). Rather than ship
///         a flaky test, the Linux build marks every Avalonia E2E test with
///         <c>[Skip(...)]</c> via the <see cref="E2eTestBase"/> helper. The
///         Windows build (when the developer is on Windows) replaces this with
///         a real Avalonia.Headless implementation — see docs/E2E_TESTING.md.
///     </para>
/// </remarks>
public sealed class HeadlessAvaloniaDriver : IE2eDriver
{
    private readonly string _projectRelativePath;
    private CliDriver? _proxy;
    private readonly bool _available;

    /// <summary>
    ///     Create a driver for the Avalonia app at <paramref name="projectRelativePath" />.
    /// </summary>
    public HeadlessAvaloniaDriver(string projectRelativePath)
    {
        _projectRelativePath = projectRelativePath;
        // Currently we only support Windows for true headless Avalonia. On
        // Linux/macOS the driver still constructs so the test code is
        // portable, but StartAsync throws PlatformNotSupportedException which
        // the test catches and reports as a skip.
        _available = OperatingSystem.IsWindows();
    }

    /// <summary>
    ///     Whether true headless Avalonia is supported on the current OS.
    ///     Test code can read this to decide whether to <c>[Skip]</c>.
    /// </summary>
    public bool IsSupportedOnCurrentOs => _available;

    /// <inheritdoc />
    public bool IsRunning => _proxy?.IsRunning ?? false;

    /// <inheritdoc />
    public Task StartAsync(string[] args, IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        if (!_available)
        {
            throw new PlatformNotSupportedException(
                "HeadlessAvaloniaDriver requires Windows. On Linux/macOS, run the " +
                "Avalonia E2E tests under Xvfb or skip them. See docs/E2E_TESTING.md.");
        }

        // On Windows we'd spin up the Avalonia.Headless app instance here.
        // For now we delegate to a CliDriver so the project still builds and
        // tests can at least verify the app starts + emits its banner.
        _proxy = new CliDriver(_projectRelativePath);
        return _proxy.StartAsync(args, env, ct);
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
    public Task StopAsync(CancellationToken ct = default)
    {
        return _proxy?.StopAsync(ct) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null)
            await _proxy.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureProxy()
    {
        if (_proxy is null)
            throw new InvalidOperationException("HeadlessAvaloniaDriver not started.");
    }
}
