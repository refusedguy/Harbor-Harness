namespace Harbor.Ipc;

/// <summary>
///     Harbor IPC server. Hosts the <see cref="MessagePackRpcServer" />,
///     which exposes the in-process <c>IAgent</c> / <c>ISessionStore</c> /
///     <c>IProviderRegistry</c> / <c>IToolRegistry</c> / <c>IEventBus</c>
///     to out-of-process <see cref="IHarborClient" /> instances via
///     MessagePack-over-pipe (Windows) or MessagePack-over-Unix-domain-socket
///     (Linux/Mac).
/// </summary>
/// <remarks>
///     <para>
///         <b>Lifecycle:</b>
///     </para>
///     <list type="number">
///         <item>Construct with the host's <see cref="IServiceProvider" /> and a pipe name.</item>
///         <item><see cref="StartAsync" /> — bind transport, start RPC server, subscribe to event bus.</item>
///         <item>Serve clients (multiple concurrent, thread-safe).</item>
///         <item><see cref="StopAsync" /> — drain in-flight requests, close transport, dispose broadcaster.</item>
///     </list>
/// </remarks>
public sealed class HarborIpcServer : IHarborServer
{
    private readonly EventBroadcaster _broadcaster;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MessagePackRpcServer _rpc;
    private readonly IServiceProvider _serviceProvider;
    private readonly ServerPipeTransport _transport;
    private int _disposed;
    private int _running;

    /// <summary>
    ///     Construct a server backed by the host's service provider.
    /// </summary>
    /// <param name="serviceProvider">The host's DI container (must expose IAgent, ISessionStore, IProviderRegistry, IToolRegistry, IEventBus, IAgentRegistry).</param>
    /// <param name="pipeName">Pipe name (Windows) or socket file basename (Unix). Defaults to <c>harbor-ipc</c>.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public HarborIpcServer(IServiceProvider serviceProvider, string pipeName = "harbor-ipc", ILoggerFactory? loggerFactory = null)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory ?? LoggerFactory.Create(b => b.AddSimpleConsole());
        _transport = new ServerPipeTransport(
            pipeName,
            _loggerFactory.CreateLogger<ServerPipeTransport>());
        _broadcaster = new EventBroadcaster(
            serviceProvider.GetRequiredService<IEventBus>(),
            _loggerFactory.CreateLogger<EventBroadcaster>());
        var dispatcher = new RequestDispatcher(serviceProvider, _broadcaster);
        _rpc = new MessagePackRpcServer(
            _transport, dispatcher, _broadcaster,
            _loggerFactory.CreateLogger<MessagePackRpcServer>());
    }

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <inheritdoc />
    public string Endpoint => _transport.Endpoint;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("Server is already running");
        }

        await _rpc.RunAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 0) != 1) return;
        await _rpc.StopAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
    }
}
