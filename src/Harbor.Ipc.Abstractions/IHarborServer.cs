namespace Harbor.Ipc;
/// <summary>
///     Host-side Harbor server interface. Implemented by
///     <c>HarborIpcServer</c> (MessagePack RPC over pipe) and — for in-process
///     mode — by a trivial pass-through that just wraps the host's
///     <c>IAgent</c> + <c>ISessionStore</c> + registries directly.
/// </summary>
/// <remarks>
///     <para>
///         The server is the long-lived process that owns the
///         <c>AgentLoop</c>, the <c>ISessionStore</c>, the
///         <c>IProviderRegistry</c>, and the <c>IToolRegistry</c>. It accepts
///         connections from one or more <see cref="IHarborClient" /> instances
///         and dispatches their requests to the in-process services.
///     </para>
///     <para>
///         <b>Lifecycle:</b> the host process calls <see cref="StartAsync" />
///         once at startup (after the DI container is built) and
///         <see cref="StopAsync" /> once at shutdown. Multiple clients may
///         connect concurrently; each gets its own request dispatcher and
///         its own event stream.
///     </para>
/// </remarks>
public interface IHarborServer : IAsyncDisposable
{

    /// <summary>True when the server is bound and accepting connections.</summary>
    public bool IsRunning { get; }

    /// <summary>The transport endpoint (pipe name on Windows, socket path on Unix).</summary>
    public string Endpoint { get; }
    /// <summary>
    ///     Bind the transport and begin accepting client connections.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the server is ready to accept connections.</returns>
    public Task StartAsync(CancellationToken ct = default);

    /// <summary>
    ///     Stop accepting new connections, drain in-flight requests, and close
    ///     the transport.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the server has fully shut down.</returns>
    public Task StopAsync(CancellationToken ct = default);
}
