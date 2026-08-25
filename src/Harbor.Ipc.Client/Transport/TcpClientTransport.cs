namespace Harbor.Ipc.Transport;
using System.Net.Sockets;

/// <summary>
///     Client-side TCP transport: opens an outbound connection to a
///     networked Harbor daemon (host:port). The RPC layer performs the
///     PSK handshake immediately after the stream is open when a key is
///     configured — the transport itself carries no credentials.
/// </summary>
public sealed class TcpClientTransport : IIpcClientTransport
{
    private readonly ILogger _logger;
    private readonly string _host;
    private readonly int _port;
    private bool _disposed;
    private Stream? _stream;

    /// <summary>Construct a client transport targeting <paramref name="host"/>:<paramref name="port"/>.</summary>
    public TcpClientTransport(string host, int port, ILogger logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
        Endpoint = $"{host}:{port}";
    }

    /// <inheritdoc />
    public string Endpoint { get; }

    /// <inheritdoc />
    public bool IsBound => _stream is not null && _stream.CanRead && _stream.CanWrite;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;
        await DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Open a connection to the daemon. Idempotent — returns the
    ///     existing stream if already connected and healthy.
    /// </summary>
    public async Task<Stream> ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null && _stream.CanRead && _stream.CanWrite)
        {
            return _stream;
        }

        var socket = new Socket(Socket.OSSupportsIPv4 ? AddressFamily.InterNetwork : AddressFamily.Unspecified,
            SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _stream = new NetworkStream(socket, ownsSocket: true);
        _logger.LogInformation("Connected to {Endpoint}", Endpoint);
        return _stream;
    }

    /// <summary>Close the current connection; the next ConnectAsync dials afresh.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_stream is null) return;
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Suppress disconnect error"); }
        _stream = null;
    }
}
