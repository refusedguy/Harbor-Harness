namespace Harbor.Ipc.Transport;
/// <summary>
///     Client-side transport. Opens an outbound connection to a server
///     <see cref="ServerPipeTransport" /> (named pipe on Windows, Unix
///     domain socket on Linux/Mac) and exposes the resulting bidirectional
///     <see cref="Stream" /> to the RPC client.
/// </summary>
/// <remarks>
///     <para>
///         <b>Connection strategy:</b> a single long-lived stream per
///         <see cref="ClientPipeTransport" /> instance. The transport
///         reconnects on demand: <see cref="ConnectAsync" /> is idempotent
///         and will only open a new stream if the previous one is closed.
///     </para>
///     <para>
///         <b>Same-endpoint compatibility:</b> the constructor's
///         <c>pipeName</c> argument works on both Windows and Unix — the
///         transport picks the right mechanism (matches <see cref="ServerPipeTransport" />).
///     </para>
/// </remarks>
public sealed class ClientPipeTransport : IIpcClientTransport
{
    private readonly ILogger _logger;
    private bool _disposed;
    private Stream? _stream;

    /// <summary>
    ///     Construct a client transport targeting the given pipe / socket.
    /// </summary>
    public ClientPipeTransport(string pipeName, ILogger logger)
    {
        _logger = logger;
        Endpoint = OperatingSystem.IsWindows()
            ? pipeName
            : Path.Combine(Path.GetTempPath(), pipeName + ".sock");
    }

    /// <inheritdoc />
    public string Endpoint
    {
        get;
    }

    /// <inheritdoc />
    public bool IsBound => _stream is not null && _stream.CanRead && _stream.CanWrite;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;
        await DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Open a connection to the server. Idempotent — returns the
    ///     existing stream if already connected.
    /// </summary>
    public async Task<Stream> ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null && _stream.CanRead && _stream.CanWrite)
            return _stream;

        _stream = OperatingSystem.IsWindows()
            ? await ConnectWindowsAsync(ct).ConfigureAwait(false)
            : await ConnectUnixAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Connected to {Endpoint}", Endpoint);
        return _stream;
    }

    /// <summary>
    ///     Close the current connection. Future calls to
    ///     <see cref="ConnectAsync" /> will open a fresh stream.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_stream is null) return;
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Suppress disconnect error"); }
        _stream = null;
    }

    private async Task<Stream> ConnectWindowsAsync(CancellationToken ct)
    {
        var client = new NamedPipeClientStream(
            ".",
            Endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        return client;
    }

    private async Task<Stream> ConnectUnixAsync(CancellationToken ct)
    {
        var endpoint = new UnixDomainSocketEndPoint(Endpoint);
        // Do NOT `using` the socket — the NetworkStream below takes ownership
        // via ownsSocket: true and disposes it when the stream is disposed.
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        return new NetworkStream(socket, true);
    }
}
