namespace Harbor.Ipc.Transport;
using System.Net;
using System.Net.Sockets;

/// <summary>
///     Server-side TCP transport: accepts inbound connections on a
///     host:port endpoint. Used for networked listeners (loopback, LAN,
///     tailscale0) — every such listener MUST be PSK-gated by the RPC
///     layer; the transport itself performs no authentication.
/// </summary>
/// <remarks>
///     <b>Bind exclusivity:</b> a plain socket bind (no SO_REUSEADDR) —
///     the OS refuses a second listener on the same endpoint, so one
///     daemon cannot silently steal another's port.
/// </remarks>
public sealed class TcpServerTransport : IIpcServerTransport
{
    private readonly Channel<Stream> _acceptChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<TcpServerTransport> _logger;
    private readonly string _host;
    private readonly int _port;
    private Task? _acceptLoopTask;
    private bool _bound;
    private Socket? _listener;

    /// <summary>
    ///     Construct a server transport bound to <paramref name="host" />
    ///     (<c>0.0.0.0</c>, an interface IP, or any resolvable DNS name)
    ///     and <paramref name="port" />.
    /// </summary>
    public TcpServerTransport(string host, int port, ILogger<TcpServerTransport> logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
        Endpoint = $"{host}:{port}";
        _acceptChannel = Channel.CreateUnbounded<Stream>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <inheritdoc />
    public string Endpoint { get; }

    /// <summary>The OS-assigned port actually bound (useful when constructed with port 0); null before bind.</summary>
    public int? BoundPort { get; private set; }

    /// <inheritdoc />
    public bool IsBound => Volatile.Read(ref _bound);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await UnbindAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>
    ///     Resolve, bind, and listen synchronously so bind failures surface
    ///     to the caller; then start the background accept loop.
    /// </summary>
    public async Task<ChannelReader<Stream>> BindAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _bound, true))
        {
            throw new InvalidOperationException("Transport is already bound");
        }

        IPAddress address = await ResolveHostAsync(_host).ConfigureAwait(false);
        var endpoint = new IPEndPoint(address, _port);

        _listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Exclusive bind on purpose: a second daemon must not steal this
            // endpoint. No SO_REUSEADDR / REUSEPORT here.
            _listener.Bind(endpoint);
            _listener.Listen(16);
        }
        catch (SocketException ex)
        {
            await DisposeListenerAsync().ConfigureAwait(false);
            Volatile.Write(ref _bound, false);
            throw new IOException(
                $"Failed to bind TCP IPC endpoint {Endpoint}: {ex.SocketErrorCode}. " +
                "Another daemon may already own it.", ex);
        }
        catch
        {
            await DisposeListenerAsync().ConfigureAwait(false);
            Volatile.Write(ref _bound, false);
            throw;
        }

        _acceptLoopTask = AcceptLoopAsync(_cts.Token);
        BoundPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _logger.LogInformation("TCP transport listening on {Endpoint}", Endpoint);
        return _acceptChannel.Reader;
    }

    /// <summary>Stop accepting and close the listener.</summary>
    public async Task UnbindAsync(CancellationToken ct = default)
    {
        if (!Volatile.Read(ref _bound)) return;
        Volatile.Write(ref _bound, false);

        _cts.Cancel();
        _acceptChannel.Writer.TryComplete();

        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Accept loop ended with error"); }
        }

        await DisposeListenerAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                TimeSpan delay = ServerPipeTransport.ComputeAcceptBackoff(consecutiveFailures);
                _logger.LogWarning(
                    ex, "AcceptAsync failed ({Count} consecutive); backing off {Delay}ms",
                    consecutiveFailures, delay.TotalMilliseconds);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var stream = new NetworkStream(client, ownsSocket: true);
            await _acceptChannel.Writer.WriteAsync(stream, ct).ConfigureAwait(false);
        }
    }

    private async Task<IPAddress> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return parsed!;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        IPAddress? ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
        return ipv4 ?? addresses[0]
               ?? throw new IOException($"Cannot resolve TCP IPC host '{host}'.");
    }

    private async Task DisposeListenerAsync()
    {
        if (_listener is null) return;
        try { _listener.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Suppress listener dispose error"); }
        _listener = null;
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
