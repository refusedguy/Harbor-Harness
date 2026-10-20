using System.Net.Http;
using System.Net.Http.Headers;

namespace Harbor.Tools.Mcp;

/// <summary>
///     The shared skeleton of the remote MCP transports (#1567) — and the ONE
///     place a remote transport's <see cref="HttpClient" /> is assembled.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="McpHttpTransport" /> (streamable HTTP) and
///         <see cref="McpSseTransport" /> (legacy HTTP+SSE) each carried their
///         own <c>GetClient()</c>: the same <see cref="SocketsHttpHandler" />
///         with the same five-minute pooled-connection lifetime, the same
///         <see cref="HttpClient" />, and the same two <c>Accept</c> headers
///         (<c>application/json</c> and <c>text/event-stream</c>). The only
///         difference was the timeout — and a difference that is invisible at a
///         glance, so a handler or header change had to be made twice and the
///         two copies drifted. (The timeouts HAD already drifted: deliberately,
///         but silently.)
///     </para>
///     <para>
///         The Factory Method is <see cref="CreateHttpClient" />: the base owns
///         the assembly, and the variation is <see cref="ClientTimeout" /> — the
///         only thing the two transports genuinely disagree about. A transport
///         states its timeout and inherits the rest.
///     </para>
///     <para>
///         Nothing else moved. Each transport still opens exactly one client,
///         lazily, on its first request; no pooling was introduced (a
///         measurement is required before that, and none has been taken), and
///         the per-attempt timeout, the retry policy and the wire format are
///         untouched.
///     </para>
///     <para>
///         Open for inheritance BY DESIGN, and only by design: the base declares
///         exactly two extension points — the protected virtual
///         <see cref="CreateHttpClient" /> and the abstract
///         <see cref="ClientTimeout" /> — and a derived transport inherits the
///         client, its lifetime and its disposal. There is nothing else to
///         override, so there is nothing else to break.
///     </para>
/// </remarks>
public abstract class McpRemoteTransportBase : IMcpRemoteTransport
{
    private HttpClient? _client;

    /// <summary>
    ///     The timeout the transport's client is built with — the one thing the
    ///     streamable-HTTP and legacy-SSE transports disagree about.
    /// </summary>
    /// <remarks>
    ///     Streamable HTTP bounds a whole request, so the client's own timeout
    ///     is the request timeout. The legacy SSE transport keeps its GET
    ///     stream open for longer than any single request, so its client
    ///     timeout is infinite and each attempt is bounded by its own linked
    ///     <see cref="CancellationTokenSource" /> instead.
    /// </remarks>
    protected abstract TimeSpan ClientTimeout { get; }

    /// <summary>
    ///     Factory Method — build the transport's <see cref="HttpClient" />.
    /// </summary>
    /// <remarks>
    ///     The base implementation is the assembly every remote transport
    ///     shares: a <see cref="SocketsHttpHandler" /> with a five-minute
    ///     pooled-connection lifetime, the client's timeout, and the two
    ///     <c>Accept</c> headers an MCP server may answer with. A transport
    ///     that needs a different client overrides this; the builtin two do
    ///     not — they only state a <see cref="ClientTimeout" />.
    /// </remarks>
    protected virtual HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler)
        {
            Timeout = ClientTimeout,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    /// <summary>
    ///     The transport's single client, built on first use and reused after.
    /// </summary>
    protected HttpClient GetClient()
    {
        if (_client is { } existing)
        {
            return existing;
        }

        HttpClient client = CreateHttpClient();
        _client = client;
        return client;
    }

    /// <summary>
    ///     Dispose the client, if one was ever built. A transport that has not
    ///     made a request has no client and nothing to dispose.
    /// </summary>
    protected void DisposeClient() => _client?.Dispose();

    /// <inheritdoc />
    public abstract Task<Result<Maybe<JsonElement>>> TryRoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();
}
