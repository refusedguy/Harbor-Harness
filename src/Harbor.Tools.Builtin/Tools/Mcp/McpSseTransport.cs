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
///     MCP legacy "HTTP + SSE" client transport: a long-lived
///     <c>GET /sse</c> event stream carries server→client frames; the first
///     <c>endpoint</c> event names the URL that requests are POSTed to.
///     Connections are lazy and per round-trip — the SSE channel is opened on
///     demand, and a <i>transient</i> failure (stream closed early, 5xx, timeout)
///     reconnects and retries the whole round-trip with exponential backoff.
///     A refusal is not transient and is not retried: a non-transient status
///     (401/403/404), a rejected OAuth grant or a closed stream costs exactly one
///     attempt, because repeating it cannot change the answer (#714). Note: unlike
///     streamable HTTP, a retried request may reach the server twice — the legacy
///     transport has no idempotency guarantee, which is the other reason the
///     transient set is kept this narrow.
///     Authentication mirrors <see cref="McpHttpTransport" />: explicit
///     <c>Authorization</c> header wins, else the OAuth token provider result
///     is attached as <c>Bearer</c>.
/// </summary>
public sealed class McpSseTransport : IMcpRemoteTransport
{
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly Func<CancellationToken, Task<Result<Maybe<string>>>>? _oauthTokenProvider;
    private readonly TimeSpan _requestTimeout;
    private readonly McpHttpClientFactory _clients;
    private readonly McpRoundTripTemplate _template;

    // The cached transport is reused by overlapping calls, so disposal must
    // be one-shot under contention rather than a check-then-write race.
    private int _disposed;

    public McpSseTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<CancellationToken, Task<Result<Maybe<string>>>>? oauthTokenProvider = null,
        ILogger? logger = null,
        TimeSpan? requestTimeout = null)
    {
        _endpoint = endpoint;
        _headers = headers;
        _oauthTokenProvider = oauthTokenProvider;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);

        // The client is NOT bounded: the GET stream outlives any single
        // request, so the client's own timeout is infinite and each attempt is
        // bounded by its own linked cancellation source instead. The client
        // itself is assembled by the factory (#1567); the timeout is the only
        // thing that differs from the streamable-HTTP transport.
        _clients = new McpHttpClientFactory(Timeout.InfiniteTimeSpan);
        _template = new McpRoundTripTemplate(
            endpoint,
            _requestTimeout,
            "SSE",
            logger is null
                ? null
                : (attempt, cause) => McpSseTransportLog.RoundTripFailed(logger, endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts, cause),
            logger is null
                ? null
                : (attempt, ex) => McpSseTransportLog.RoundTripFailedRetrying(logger, ex, endpoint, attempt, TransientFailurePolicy.DefaultMaxAttempts),
            logger is null ? null : error => McpSseTransportLog.TransportFailure(logger, error));
    }

    /// <summary>
    ///     Result railway for the round-trip (#201 C4, sealed to the interface in
    ///     #587): expected network failures (closed SSE stream, message-endpoint
    ///     errors, client-side timeout) surface as
    ///     <c>Failure(endpoint + attempts + latency + cause)</c> instead of throwing
    ///     (<c>IOException</c>/<c>HttpRequestException</c>/<c>TimeoutException</c> no
    ///     longer escape). Retry/timeout policy stays inside; user cancellation and
    ///     disposal still throw.
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

        return await _template
            .RunAsync(ct => TryOnceAsync(body, expectedId, ct), sw, cancellationToken)
            .ConfigureAwait(false);
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
    ///     One legacy-SSE attempt: open the channel, POST to the announced
    ///     endpoint, then read the response frame. A closed stream or a non-OK
    ///     endpoint is a <c>Failure</c>; mid-stream transport exceptions still
    ///     propagate to <see cref="McpRoundTripTemplate" />, which owns the
    ///     retry policy. The verdict rides beside the Result because a status
    ///     code cannot survive inside a <c>Failure</c> string.
    /// </summary>
    private async Task<McpAttempt> TryOnceAsync(
        byte[] body,
        int? expectedId,
        CancellationToken cancellationToken)
    {
        HttpClient client = _clients.Client;

        // 1. GET the SSE channel and wait for the endpoint announcement.
        // Resolution stays per attempt, as before #2599.
        Result<Maybe<string>> oauth = await TryGetOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
        if (oauth.IsFailure)
        {
            // A rejected grant is an answer, not an outage: terminal on the
            // first attempt, so the provider is not asked again.
            return McpAttempt.Terminal(oauth.ConvertFailure<Maybe<JsonElement>>());
        }

        Maybe<string> oauthToken = oauth.Value;
        using HttpRequestMessage sseRequest = new(HttpMethod.Get, _endpoint);
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(sseRequest, oauthToken);
        using HttpResponseMessage sseResponse = await client
            .SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        Result getOk = EnsureSuccessResult(sseResponse, "SSE channel");
        if (getOk.IsFailure)
        {
            return AttemptFor(sseResponse.StatusCode, getOk.ConvertFailure<Maybe<JsonElement>>());
        }

        var reader = new SseEventReader();
        Uri? postEndpoint = null;
        using StreamReader streamReader = new(
            await sseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        while (postEndpoint is null)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // The channel opened and then closed: a blip, and the one failure on
                // this path that genuinely earns its retries.
                return McpAttempt.Transient(
                    Result.Failure<Maybe<JsonElement>>("SSE stream closed before announcing an endpoint."));
            }

            if (reader.Feed(line) is { } ev && ev.Event == "endpoint")
            {
                postEndpoint = new Uri(_endpoint, ev.Data.Trim());
            }
        }

        // 2. POST the JSON-RPC request to the announced endpoint.
        using HttpRequestMessage postRequest = new(HttpMethod.Post, postEndpoint)
        {
            Content = new ByteArrayContent(body),
        };
        // Same wire header StringContent used to emit, so the bytes and the
        // media type on the wire stay exactly as they were.
        postRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyHeaders(postRequest, oauthToken);
        using HttpResponseMessage postResponse = await client
            .SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        Result postOk = EnsureSuccessResult(postResponse, "message endpoint");
        if (postOk.IsFailure)
        {
            return AttemptFor(postResponse.StatusCode, postOk.ConvertFailure<Maybe<JsonElement>>());
        }

        // 3. Keep reading the SSE channel for the response frame.
        while (true)
        {
            string? line = await streamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return McpAttempt.Transient(
                    Result.Failure<Maybe<JsonElement>>("SSE stream closed before a response arrived."));
            }

            if (reader.Feed(line) is { Event: "message" } ev
                && McpSse.TryParseResponse(ev.Data, expectedId) is { } doc)
            {
                return McpAttempt.Succeeded(Result.Success(Maybe<JsonElement>.From(doc)));
            }
        }
    }

    /// <summary>
    ///     Weights a refused HTTP response while it is still in hand (#714).
    ///     The verdict itself moved to
    ///     <see cref="TransientFailurePolicy.IsTransientStatus" /> in #2599.
    /// </summary>
    private static McpAttempt AttemptFor(HttpStatusCode status, Result<Maybe<JsonElement>> failure)
        => IsTransientStatus(status)
            ? McpAttempt.Transient(failure)
            : McpAttempt.Terminal(failure);

    /// <summary>
    ///     Declared forwarder to the shared status verdict. The ownership rule
    ///     calls this on both transports to prove they ask the same owner;
    ///     there is no second expression of the set to drift (#2599).
    /// </summary>
    private static bool IsTransientStatus(HttpStatusCode status)
        => TransientFailurePolicy.IsTransientStatus(status);

    private void ApplyHeaders(HttpRequestMessage request, Maybe<string> oauthToken)
    {
        bool hasAuthorization = false;
        if (_headers is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> header in _headers)
            {
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    hasAuthorization |= string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        if (!hasAuthorization && oauthToken.HasValue && oauthToken.Value.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken.Value);
        }
    }

    private static Result EnsureSuccessResult(HttpResponseMessage response, string what)
    {
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Accepted)
        {
            return Result.Success();
        }

        return Result.Failure($"{what} returned {(int)response.StatusCode}");
    }

    /// <summary>
    ///     The provider's outcome, verbatim — see
    ///     <see cref="McpHttpTransport" /> for the contract. The nullable overload
    ///     this replaced forced the transport to manufacture the same
    ///     <c>Maybe</c> from a <c>null</c>, and the exception arm beside it could
    ///     never fire (#566).
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
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="McpSseTransport" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls
///     (including the skip-when-null semantics of the optional logger).
/// </summary>
internal static partial class McpSseTransportLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "MCP SSE round-trip to {Endpoint} failed (attempt {Attempt}/{Max}): {Cause}; reconnecting")]
    public static partial void RoundTripFailed(ILogger logger, Uri endpoint, int attempt, int max, string cause);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "MCP SSE round-trip to {Endpoint} failed (attempt {Attempt}/{Max}); reconnecting")]
    public static partial void RoundTripFailedRetrying(ILogger logger, Exception ex, Uri endpoint, int attempt, int max);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "MCP SSE transport failure: {Error}")]
    public static partial void TransportFailure(ILogger logger, string error);
}
