using System.Net.Http;
using System.Net.Http.Headers;

namespace Harbor.Tools.Mcp;

/// <summary>
///     The ONE place a remote MCP transport's <see cref="HttpClient" /> is assembled
///     (#1567) — and the reason the transports need no shared base class (#1835).
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
///         The assembly therefore lives here, once, and each transport holds one
///         of these. The variation the transports own is the timeout — the only
///         thing they genuinely disagree about: the streamable-HTTP transport
///         bounds a whole request with it, while the legacy SSE transport keeps
///         its GET stream open for longer than any single request and bounds
///         each attempt with its own linked
///         <see cref="CancellationTokenSource" /> instead.
///     </para>
///     <para>
///         Nothing else moved. Each transport still opens exactly one client,
///         lazily, on its first request; no pooling was introduced (a
///         measurement is required before that, and none has been taken), and
///         the per-attempt timeout, the retry policy and the wire format are
///         untouched.
///     </para>
///     <para>
///         <b>Composition, not inheritance (#1835).</b> This type is
///         <c>internal</c>, so the shared assembly costs the package's public
///         surface nothing. The alternative — a public base class — was forced
///         by C#'s rule that a public class may not derive from an internal
///         one (CS0060), and it put a type no consumer could use into the
///         NuGet surface of <c>Harbor.Tools.Builtin</c>. The one extension
///         point the transports have is <see cref="IMcpRemoteTransport" />, and
///         it stays the only one.
///     </para>
/// </remarks>
internal sealed class McpHttpClientFactory : IDisposable
{
    private readonly TimeSpan _timeout;
    private HttpClient? _client;

    /// <summary>
    ///     A factory for one transport's client, built with
    ///     <paramref name="timeout" />.
    /// </summary>
    /// <param name="timeout">
    ///     The timeout the client is built with — the one thing the
    ///     streamable-HTTP and legacy-SSE transports disagree about.
    /// </param>
    internal McpHttpClientFactory(TimeSpan timeout) => _timeout = timeout;

    /// <summary>
    ///     The transport's single client, built on first use and reused after.
    /// </summary>
    /// <remarks>
    ///     Lazy on purpose: a transport that has not made a request has no
    ///     client, and therefore nothing to dispose.
    /// </remarks>
    internal HttpClient Client
    {
        get
        {
            if (_client is { } existing)
            {
                return existing;
            }

            HttpClient client = Create(_timeout);
            _client = client;
            return client;
        }
    }

    /// <summary>
    ///     Dispose the client, if one was ever built. Disposing twice is
    ///     harmless — <see cref="HttpClient.Dispose" /> is idempotent.
    /// </summary>
    public void Dispose()
    {
        _client?.Dispose();

        // No finalizer of its own, so nothing was queued for the finalizer
        // thread; the call says so explicitly rather than leaving it to a
        // reader to infer.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     The assembly every remote transport shares: a
    ///     <see cref="SocketsHttpHandler" /> with a five-minute
    ///     pooled-connection lifetime, the client's timeout, and the two
    ///     <c>Accept</c> headers an MCP server may answer with.
    /// </summary>
    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler)
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }
}
