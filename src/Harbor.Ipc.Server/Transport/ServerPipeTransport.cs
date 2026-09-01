namespace Harbor.Ipc.Transport;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
/// <summary>
///     Server-side transport that accepts inbound connections over a Named
///     Pipe (Windows) or Unix Domain Socket (Linux/Mac). Each accepted
///     connection yields a <see cref="Stream" /> the RPC layer reads/writes.
/// </summary>
/// <remarks>
///     <para>
///         <b>Endpoint resolution:</b> on Windows the endpoint is the pipe
///         name (e.g. <c>harbor-ipc</c> → <c>\\.\pipe\harbor-ipc</c>); on
///         Unix it is a socket file path (e.g.
///         <c>/tmp/harbor-ipc.sock</c>). The same <c>pipeName</c> argument
///         works on both OSes — the transport picks the right mechanism.
///     </para>
    /// <para>
    ///         <b>Permissions:</b> on Unix the socket file is chmod'ed 0600
    ///         (owner-only) immediately after bind. On Windows the pipe is
    ///         created with <c>PipeOptions.None</c> (current-user-only ACL via the
    ///         default NamedPipeServerStream constructor).
    ///     </para>
    ///     <para>
    ///         <b>Stale endpoint policy:</b> a leftover socket file is deleted
    ///         only after a probe <c>connect()</c> proves no live listener is
    ///         serving it. A live peer makes <see cref="BindAsync" /> fail with
    ///         an explicit error instead of silently stealing the endpoint.
    ///     </para>
/// </remarks>
public sealed class ServerPipeTransport : IIpcServerTransport
{
    private readonly Channel<Stream> _acceptChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<ServerPipeTransport> _logger;
    private Task? _acceptLoopTask;
    private bool _bound;
    private bool _disposed;
    private Socket? _unixSocket;

    /// <summary>
    ///     Construct a server transport. Call <see cref="BindAsync" /> to
    ///     start accepting connections.
    /// </summary>
    /// <param name="pipeName">Pipe name (Windows) or socket file basename (Unix).</param>
    /// <param name="logger">Logger.</param>
    public ServerPipeTransport(string pipeName, ILogger<ServerPipeTransport> logger)
    {
        _logger = logger;
        Endpoint = OperatingSystem.IsWindows()
            ? pipeName
            : Path.Combine(Path.GetTempPath(), pipeName + ".sock");
        _acceptChannel = Channel.CreateUnbounded<Stream>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <inheritdoc />
    public string Endpoint
    {
        get;
    }

    /// <inheritdoc />
    public bool IsBound => Volatile.Read(ref _bound);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await UnbindAsync().ConfigureAwait(false);
        _cts.Dispose();
        Volatile.Write(ref _disposed, true);
    }

    // Accept-failure backoff: repeated AcceptAsync errors (EMFILE/ENFILE fd
    // exhaustion, transient socket-state faults) must not spin the loop hot.
    // 100 ms doubling capped at 5 s; reset on every successful accept.
    private const int MaxAcceptBackoffMs = 5000;

    /// <summary>
    ///     Backoff delay for the Nth consecutive accept failure (1-based).
    ///     Pure function — exposed for tests.
    /// </summary>
    public static TimeSpan ComputeAcceptBackoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1) return TimeSpan.FromMilliseconds(100);
        int shift = Math.Min(consecutiveFailures - 1, 6);
        return TimeSpan.FromMilliseconds(Math.Min(100 << shift, MaxAcceptBackoffMs));
    }

    /// <summary>
    ///     Bind the transport and begin accepting connections. Returns the
    ///     accept channel reader that the RPC layer awaits.
    /// </summary>
    /// <remarks>
    ///     On Unix, endpoint acquisition (stale-file probe, bind, 0600 mode,
    ///     listen) happens <b>synchronously</b> inside this call so a stolen/
    ///     busy endpoint surfaces as an exception to the caller instead of
    ///     failing silently inside the background accept loop.
    /// </remarks>
    public async Task<ChannelReader<Stream>> BindAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _bound, true))
        {
            throw new InvalidOperationException("Transport is already bound");
        }

        if (!OperatingSystem.IsWindows())
        {
            await PrepareUnixEndpointAsync(ct).ConfigureAwait(false);
            _acceptLoopTask = AcceptOnlyUnixLoopAsync(_cts.Token);
        }
        else
        {
            // Single-owner endpoint policy (mirrors the Unix stale-file
            // probe): Windows named pipes natively allow multiple server
            // instances on the same name — a naive second bind would
            // silently share (steal) the endpoint. Refuse when a listener
            // is already serving it.
            if (IsPipeListenerAlive(Endpoint))
            {
                throw new IOException(
                    $"IPC endpoint '{Endpoint}' is already served by another listener; refusing to steal it. " +
                    "Stop the other instance or choose a different pipe name.");
            }

            _acceptLoopTask = AcceptLoopWindowsAsync(_cts.Token);
        }

        _logger.LogInformation("Transport bound to {Endpoint}", Endpoint);
        return _acceptChannel.Reader;
    }

    /// <summary>
    ///     Stop accepting, drain pending connections, and unbind the transport.
    /// </summary>
    public async Task UnbindAsync(CancellationToken ct = default)
    {
        if (!Volatile.Read(ref _bound)) return;
        Volatile.Write(ref _bound, false);

        _cts.Cancel();
        _acceptChannel.Writer.TryComplete();

        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException)
            { /* expected on shutdown */
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Accept loop ended with error"); }
        }

        if (_unixSocket is not null)
        {
            try { _unixSocket.Dispose(); }
            catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress socket dispose error"); }
            _unixSocket = null;
        }

        // Clean up the socket file on Unix.
        if (!OperatingSystem.IsWindows() && File.Exists(Endpoint))
        {
            try { File.Delete(Endpoint); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete socket file {Path}", Endpoint); }
        }
    }

    // ── Windows: NamedPipeServerStream accept loop ─────────────────────────

    private async Task AcceptLoopWindowsAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                Endpoint,
                PipeDirection.InOut,
                4,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress dispose error"); }
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                TimeSpan delay = ComputeAcceptBackoff(consecutiveFailures);
                _logger.LogWarning(
                    ex, "WaitForConnectionAsync failed ({Count} consecutive); backing off {Delay}ms",
                    consecutiveFailures, delay.TotalMilliseconds);
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress dispose error"); }
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            // Hand off the connected stream and immediately create a new one
            // for the next client (Windows named pipes: one server stream per
            // concurrent client).
            await _acceptChannel.Writer.WriteAsync(server, ct).ConfigureAwait(false);
        }
    }

    // ── Unix: UnixDomainSocket accept loop ─────────────────────────────────

    /// <summary>
    ///     Synchronous endpoint acquisition for the Unix transport: stale
    ///     file handling, bind, owner-only mode, listen. Called from
    ///     <see cref="BindAsync" /> so failures surface to the caller.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private async Task PrepareUnixEndpointAsync(CancellationToken ct)
    {
        await RemoveStaleSocketAsync(ct).ConfigureAwait(false);

        var endpoint = new UnixDomainSocketEndPoint(Endpoint);
        _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        // A bind failure (e.g. endpoint grabbed between the stale probe and
        // this call) propagates to the caller — the accept loop never starts.
        _unixSocket.Bind(endpoint);

        // Owner-only access (0600): the socket file lives in a shared temp
        // directory and the RPC protocol has no per-user authentication —
        // file permissions are the isolation boundary.
        try
        {
            File.SetUnixFileMode(Endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restrict socket file mode on {Endpoint}", Endpoint);
        }

        _unixSocket.Listen(16);
    }

    private async Task AcceptOnlyUnixLoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _unixSocket!.AcceptAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                TimeSpan delay = ComputeAcceptBackoff(consecutiveFailures);
                _logger.LogWarning(
                    ex, "AcceptAsync failed ({Count} consecutive); backing off {Delay}ms",
                    consecutiveFailures, delay.TotalMilliseconds);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var stream = new NetworkStream(client, true);
            await _acceptChannel.Writer.WriteAsync(stream, ct).ConfigureAwait(false);
        }
    }

    // ── Windows: single-owner endpoint probe ────────────────────────────────

    // WaitNamedPipe reports whether the pipe name has a listening server
    // instance WITHOUT consuming the pending connection a client connect()
    // probe would steal from the live server's WaitForConnection.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipeW(string lpNamedPipeName, int nTimeOut);

    private const int ErrorFileNotFound = 2;

    /// <summary>
    ///     Returns <see langword="true" /> when a listening server instance
    ///     exists for the Windows named pipe <paramref name="pipeName" />.
    ///     An unregistered pipe name (ERROR_FILE_NOT_FOUND) means nothing is
    ///     listening; any other outcome — including the name existing but
    ///     being briefly busy in the mid-handoff window — is treated
    ///     conservatively as alive so the caller never steals an endpoint
    ///     it does not own.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsPipeListenerAlive(string pipeName)
    {
        string fullName = pipeName.StartsWith(@"\\.\pipe\", StringComparison.Ordinal)
            ? pipeName
            : @"\\.\pipe\" + pipeName;
        if (WaitNamedPipeW(fullName, 50))
        {
            return true;
        }

        return Marshal.GetLastWin32Error() != ErrorFileNotFound;
    }

    /// <summary>
    ///     Handle a leftover socket file from a previous run. The file is
    ///     removed only when a probe <c>connect()</c> confirms nothing is
    ///     serving it; if a live listener answers the probe, binding would
    ///     silently steal its endpoint — refuse instead.
    /// </summary>
    private async Task RemoveStaleSocketAsync(CancellationToken ct)
    {
        if (!File.Exists(Endpoint)) return;

        if (await IsEndpointAliveAsync(ct).ConfigureAwait(false))
        {
            throw new IOException(
                $"IPC endpoint '{Endpoint}' is already served by another listener; refusing to steal it. " +
                "Stop the other instance or choose a different pipe name.");
        }

        try { File.Delete(Endpoint); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete stale socket file {Endpoint}", Endpoint); }
    }

    /// <summary>
    ///     Probe-connect to the existing socket file. A successful connect
    ///     proves a live listener; refusal/reset/timeout proves staleness.
    /// </summary>
    private async Task<bool> IsEndpointAliveAsync(CancellationToken ct)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Probe timeout: nothing answered in time — treat as dead.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe connect to {Endpoint} failed — socket file is stale", Endpoint);
            return false;
        }
    }
}
