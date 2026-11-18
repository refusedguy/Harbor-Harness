using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Harbor.Abstractions.Resilience;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Round-trip contract shared by the remote MCP transports (streamable HTTP
///     and legacy SSE). Public since #477 so a host can plug a third transport
///     in through <see cref="IMcpTransportFactory" /> — it was internal, which
///     made the existing seam unusable from outside this assembly.
/// </summary>
/// <remarks>
///     The seam carries <c>Result&lt;Maybe&lt;JsonElement&gt;&gt;</c> and nothing
///     else (#587). Three outcomes have to stay tellable apart, and a nullable
///     return collapses the first two into one <c>null</c>:
///     <list type="bullet">
///     <item><c>Failure</c> — the transport failed, and the error names the endpoint,
///     the HTTP status, the attempt count and the latency.</item>
///     <item><c>Success(Maybe.None)</c> — the server answered, and the answer
///     legitimately carries no document (<c>202 Accepted</c>, empty body).</item>
///     <item><c>Success(Maybe.From(el))</c> — the response (no disposal: a source-gen element).</item>
///     </list>
///     A nullable-returning member is banned here by
///     <c>tests/Harbor.Architecture.Tests/RemoteTransportResultRules.cs</c>, which
///     reflects over this interface and fails if one reappears.
/// </remarks>
public interface IMcpRemoteTransport : IAsyncDisposable
{
    /// <summary>
    ///     Send one JSON-RPC request and return the matching response.
    ///     <c>Maybe.None</c> is a successful round-trip whose answer
    ///     carries no document — not a failure. User cancellation and disposal
    ///     still throw; every other wire failure is a <c>Failure</c>.
    /// </summary>
    Task<Result<Maybe<JsonElement>>> TryRoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     MCP "Streamable HTTP" client transport (MCP spec 2025-03-26): every JSON-RPC
///     message is POSTed to the server endpoint; the response comes back either as a
///     single <c>application/json</c> body or as a <c>text/event-stream</c>.
///     Connections are lazy — nothing is opened until the first call. Transient
///     failures (network errors, 5xx, 408, client-side timeout) are retried with
///     exponential backoff. The server-assigned <c>Mcp-Session-Id</c> is captured on
///     the first response and replayed on later requests.
///     Authentication: an explicit <c>Authorization</c> header wins; otherwise an
///     OAuth token provider result is attached as <c>Bearer</c> (backed by
///     <see cref="McpOAuthHandler" /> when the server has an <c>auth</c> config,
///     else the <c>HARBOR_MCP_OAUTH_TOKEN</c> environment fallback).
/// </summary>
public sealed class McpHttpTransport : IMcpRemoteTransport
{
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<CancellationToken, Task<Result<Maybe<string>>>>? _oauthTokenProvider;
    private readonly ILogger? _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly McpHttpClientFactory _clients;
    private readonly McpRoundTripTemplate _template;

    // One transport instance is cached and reused for many overlapping calls
    // (McpRegistry.ServerEntry). Both fields cross those call boundaries, so
    // they are accessed through Volatile/Interlocked rather than as plain
    // fields: the session id is first-writer-wins, disposal is one-shot.
    private string? _sessionId;
    private int _disposed;

    public McpHttpTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<CancellationToken, Task<Result<Maybe<string>>>>? oauthTokenProvider = null,
        ILogger? logger = null,
        TimeSpan? requestTimeout = null)
    {
        _endpoint = endpoint;
        _headers = headers;
        _oauthTokenProvider = oauthTokenProvider;
        _logger = logger;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);

        // The client is bounded by the request timeout — this transport's
        // request IS the whole exchange. The client itself is assembled by the
        // factory (#1567); the timeout is the only thing that differs from the
        // legacy SSE transport.
        _clients = new McpHttpClientFactory(_requestTimeout);
        _template = new McpRoundTripTemplate(
            endpoint,
            _requestTimeout,
            "HTTP",
            logger is null
                ? null
                : (attempt, cause) => McpHttpTransportLog.RequestFailed(logger, endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts, cause),
            logger is null
                ? null
                : (attempt, ex) => McpHttpTransportLog.RequestFailedRetrying(logger, ex, endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts),
            logger is null ? null : error => McpHttpTransportLog.TransportFailure(logger, error));
    }

    /// <summary>
    ///     Result railway for the round-trip (#201 C4, sealed to the interface in
    ///     #587): expected network failures (5xx/408, client-side timeout, no-frame
    ///     SSE body, unreachable endpoint) surface as
    ///     <c>Failure(endpoint + attempts + latency + cause)</c> instead of throwing.
    ///     Retry/timeout policy stays inside; user cancellation and disposal still
    ///     throw.
    ///     <c>Maybe.None</c> means "succeeded, and the answer legitimately carries no
    ///     document" (202 Accepted, empty body) — a different state from a failure,
    ///     which is why the value is a <see cref="Maybe{T}" /> and not a null.
    /// </summary>
    public async Task<Result<Maybe<JsonElement>>> TryRoundTripAsync(
        JsonElement request,
        int? expectedId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var sw = Stopwatch.StartNew();
        // ROP-A ПР.11: serialize the JSON-RPC envelope straight to UTF-8 bytes.
        // GetRawText() materialized a UTF-16 string that StringContent then
        // re-encoded to UTF-8, one copy too many on the request path.
        var bufferWriter = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            request.WriteTo(writer);
        }
        byte[] body = bufferWriter.WrittenSpan.ToArray();

        // Resolved before the loop, as before #2599: a rejected grant is an
        // answer, not an outage, and must not spend a round-trip proving it.
        Result<Maybe<string>> oauth = await TryGetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (oauth.IsFailure)
            return _template.Fail<Maybe<JsonElement>>(sw, 0, oauth.Error);

        Maybe<string> oauthToken = oauth.Value;
        return await _template
            .RunAsync(ct => TryOnceAsync(body, oauthToken, expectedId, ct), sw, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     One streamable-HTTP attempt. Everything transport-specific lives
    ///     here: building the POST, applying headers and the captured session,
    ///     and parsing the answer. Whether the failure earns another attempt is
    ///     the verdict handed to the shared loop beside the Result.
    /// </summary>
    private async Task<McpAttempt> TryOnceAsync(
        byte[] body,
        Maybe<string> oauthToken,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        HttpClient client = _clients.Client;
        using HttpRequestMessage httpRequest = BuildRequest(body, oauthToken);
        using HttpResponseMessage httpResponse = await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (httpResponse.StatusCode == HttpStatusCode.Accepted)
            return McpAttempt.Succeeded(Result.Success(Maybe<JsonElement>.None));

        if (IsTransientStatus(httpResponse.StatusCode))
        {
            string cause = $"server returned {(int)httpResponse.StatusCode}";
            return McpAttempt.Transient(Result.Failure<Maybe<JsonElement>>(cause));
        }

        CaptureSession(httpResponse);
        Result<Maybe<JsonElement>> read =
            await TryReadResponseAsync(httpResponse, expectedId, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            // The response was consumed: a no-frame SSE body is terminal.
            return McpAttempt.Terminal(read);
        }

        return McpAttempt.Succeeded(read);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _clients.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Weights a refused HTTP response while it is still in hand (#714).
    ///     The verdict itself moved to
    ///     <see cref="TransientFailurePolicy.IsTransientStatus" /> in #2599;
    ///     this declared forwarder stays so the ownership rule can still call
    ///     both transports and prove they ask the same owner.
    /// </summary>
    private static bool IsTransientStatus(HttpStatusCode status)
        => TransientFailurePolicy.IsTransientStatus(status);

    private HttpRequestMessage BuildRequest(byte[] body, Maybe<string> oauthToken)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        // Same wire header StringContent used to emit, so the bytes and the
        // media type on the wire stay exactly as they were.
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        bool hasAuthorization = false;
        if (_headers is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> header in _headers)
            {
                if (httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    hasAuthorization |= string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        if (!hasAuthorization && oauthToken.HasValue && oauthToken.Value.Length > 0)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken.Value);
        }

        string? sessionId = Volatile.Read(ref _sessionId);
        if (sessionId is not null)
        {
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        return httpRequest;
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (Volatile.Read(ref _sessionId) is not null)
            return;
        if (!response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values))
            return;

        string? announced = values.FirstOrDefault();
        if (announced is null || announced.Length == 0)
            return;

        // Two initialize calls may answer at the same time. Only the first
        // captured id wins; a later answer must not replace it mid-burst and
        // send subsequent requests to a different session.
        if (Interlocked.CompareExchange(ref _sessionId, announced, null) is null
            && _logger is not null)
        {
            McpHttpTransportLog.SessionCaptured(_logger, announced);
        }
    }

    /// <summary>
    ///     Read one response body on the Result railway (#201 A7): an empty payload
    ///     is a valid notification-style <c>Success(Maybe.None)</c>; an SSE body without
    ///     a matching frame is a terminal <c>Failure</c> (was: throw in the same method
    ///     that returned null — nulls and throws no longer share one signature).
    /// </summary>
    private async Task<Result<Maybe<JsonElement>>> TryReadResponseAsync(
        HttpResponseMessage response,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        // #180: read bytes and parse from UTF-8 directly — the JSON branch
        // never materializes the body as a string.
        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (IsBlank(payload))
        {
            return Result.Success(Maybe<JsonElement>.None);
        }

        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Same DOM as JsonDocument.Parse, but through the source-generated
                // JsonElement info (PERF-002; TRIM-SERIALIZATION TypeInfo spelling).
                return Result.Success(Maybe<JsonElement>.From(
                    JsonSerializer.Deserialize(payload, McpJsonSerializerContext.Default.JsonElement)));
            }
            catch (JsonException ex)
            {
                return Result.Failure<Maybe<JsonElement>>($"response was not valid JSON: {ex.Message}");
            }
        }

        // SSE-bodied response: pick the first message frame answering expectedId.
        // #2611: walk the body as a span and hand one line at a time to the
        // incremental reader. string.Split would allocate the line array plus
        // every substring up front, even when the first frame already matches.
        // The loop feeds the same sequence Split produced, including the final
        // empty line for a body that ends with '\n'. Feed still trims '\r'.
        ReadOnlySpan<char> remaining = Encoding.UTF8.GetString(payload);
        var reader = new SseEventReader();
        while (true)
        {
            int newline = remaining.IndexOf('\n');
            ReadOnlySpan<char> line = newline < 0 ? remaining : remaining[..newline];
            if (reader.Feed(line.ToString()) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return Result.Success(Maybe<JsonElement>.From(doc));
            }

            if (newline < 0)
                break;
            remaining = remaining[(newline + 1)..];
        }

        return Result.Failure<Maybe<JsonElement>>("SSE response carried no matching JSON-RPC frame.");
    }

    /// <summary>
    /// Empty-or-ASCII-whitespace body — mirrors the old
    /// <c>string.IsNullOrWhiteSpace</c> check for every payload JSON admits
    /// (JSON whitespace is space/tab/CR/LF only).
    /// </summary>
    private static bool IsBlank(ReadOnlySpan<byte> payload)
    {
        foreach (byte b in payload)
        {
            if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The provider's outcome, verbatim. The seam speaks
    ///     <c>Result&lt;Maybe&lt;string&gt;&gt;</c> end to end, so "no token yet"
    ///     and "the grant was rejected" arrive as two distinct states instead of
    ///     both arriving as the <c>null</c> this method used to manufacture out of
    ///     either one (#566). The catch-all stays as a floor for a hand-written
    ///     provider that throws; it is not a shape this transport produces.
    /// </summary>
    private async Task<Result<Maybe<string>>> TryGetOAuthTokenAsync(CancellationToken cancellationToken)
    {
        if (_oauthTokenProvider is null)
            return Result.Success(Maybe<string>.None);
        try
        {
            return await _oauthTokenProvider(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Failure<Maybe<string>>($"MCP OAuth token failed: {ex.Message}");
        }
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpHttpTransport" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls
///     (including the skip-when-null semantics of the optional logger).
/// </summary>
internal static partial class McpHttpTransportLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "MCP HTTP request to {Endpoint} failed (attempt {Attempt}/{Max}): {Cause}; retrying")]
    public static partial void RequestFailed(ILogger logger, Uri endpoint, int attempt, int max, string cause);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "MCP HTTP request to {Endpoint} failed (attempt {Attempt}/{Max}); retrying")]
    public static partial void RequestFailedRetrying(ILogger logger, Exception ex, Uri endpoint, int attempt, int max);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "MCP HTTP session captured: {SessionId}")]
    public static partial void SessionCaptured(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "MCP HTTP transport failure: {Error}")]
    public static partial void TransportFailure(ILogger logger, string error);
}
