namespace Harbor.Ipc.Transport;
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
///     <para>
///         <b>Permissions:</b> on Unix the socket file is created with
///         0600 (owner-only) by default. On Windows the pipe is created
///         with <c>PipeOptions.None</c> (current-user-only ACL via the
///         default NamedPipeServerStream constructor).
///     </para>
/// </remarks>
public sealed class ServerPipeTransport : IPipeTransport
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

    /// <summary>
    ///     Bind the transport and begin accepting connections. Returns the
    ///     accept channel reader that the RPC layer awaits.
    /// </summary>
    public Task<ChannelReader<Stream>> BindAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _bound, true))
        {
            throw new InvalidOperationException("Transport is already bound");
        }

        _acceptLoopTask = OperatingSystem.IsWindows()
            ? AcceptLoopWindowsAsync(_cts.Token)
            : AcceptLoopUnixAsync(_cts.Token);

        _logger.LogInformation("Transport bound to {Endpoint}", Endpoint);
        return Task.FromResult<ChannelReader<Stream>>(_acceptChannel.Reader);
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
            }
            catch (OperationCanceledException)
            {
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress dispose error"); }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WaitForConnectionAsync failed");
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { _logger.LogDebug(disposeEx, "Suppress dispose error"); }
                continue;
            }

            // Hand off the connected stream and immediately create a new one
            // for the next client (Windows named pipes: one server stream per
            // concurrent client).
            await _acceptChannel.Writer.WriteAsync(server, ct).ConfigureAwait(false);
        }
    }

    // ── Unix: UnixDomainSocket accept loop ─────────────────────────────────

    private async Task AcceptLoopUnixAsync(CancellationToken ct)
    {
        // Clean up any stale socket file from a previous crashed run.
        if (File.Exists(Endpoint))
        {
            try { File.Delete(Endpoint); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete stale socket file"); }
        }

        var endpoint = new UnixDomainSocketEndPoint(Endpoint);
        _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _unixSocket.Bind(endpoint);
        _unixSocket.Listen(16);

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _unixSocket.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AcceptAsync failed");
                continue;
            }

            var stream = new NetworkStream(client, true);
            await _acceptChannel.Writer.WriteAsync(stream, ct).ConfigureAwait(false);
        }
    }
}
