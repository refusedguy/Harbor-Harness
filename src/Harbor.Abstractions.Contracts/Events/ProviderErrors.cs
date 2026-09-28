namespace Harbor.Abstractions.Events;

/// <summary>
///     Transport-level classification of an LLM provider failure (ROP-A ПР.5,
///     CSE bible §4.4 — the single zone where typed errors are sanctioned).
///     Carried on <see cref="ErrorEvent" /> so the agent loop's retry policy can
///     distinguish transient failures (rate limit, server overload, network
///     blip, timeout) from fatal ones (bad key, malformed stream) without
///     parsing error strings.
/// </summary>
public enum ProviderErrorKind
{
    /// <summary>Unclassified failure — treated as fatal by the retry policy.</summary>
    Unknown = 0,

    /// <summary>HTTP 429 / provider-reported rate limiting — retried.</summary>
    RateLimit,

    /// <summary>Missing or rejected API key (401/403-style) — not retried.</summary>
    Auth,

    /// <summary>Provider timeout (TaskCanceledException without caller cancellation) — retried.</summary>
    Timeout,

    /// <summary>Network-level failure (DNS, connection reset, TLS, mid-stream IO) — retried.</summary>
    Network,

    /// <summary>HTTP 5xx server-side overload — retried.</summary>
    ServerError,

    /// <summary>Malformed wire payload (unparseable SSE chunk) — not retried.</summary>
    Malformed,

    /// <summary>Caller cancellation surfaced through the stream — never retried.</summary>
    Cancelled
}

/// <summary>
///     Canonical classification helpers shared by all ILlmClient implementations.
///     Single source of truth so every provider maps the same wire condition to
///     the same kind (and therefore the same retry verdict).
/// </summary>
public static class ProviderErrors
{
    /// <summary>Retry verdict for a transport error kind.</summary>
    public static bool IsTransient(ProviderErrorKind kind) =>
        kind is ProviderErrorKind.RateLimit or ProviderErrorKind.Timeout
            or ProviderErrorKind.Network or ProviderErrorKind.ServerError;

    /// <summary>Classify an HTTP status code returned by a provider endpoint.</summary>
    public static ProviderErrorKind FromStatus(System.Net.HttpStatusCode status)
    {
        int code = (int)status;
        if (code == 429) return ProviderErrorKind.RateLimit;
        if (code == 401 || code == 403) return ProviderErrorKind.Auth;
        if (code >= 500) return ProviderErrorKind.ServerError;
        return ProviderErrorKind.Unknown;
    }

    /// <summary>
    ///     Classify an exception thrown by the transport layer. Caller
    ///     cancellation (<paramref name="cancellationToken" /> cancelled) is
    ///     <see cref="ProviderErrorKind.Cancelled" />; a cancellation without it
    ///     is a provider <see cref="ProviderErrorKind.Timeout" />; HTTP/network
    ///     exceptions are <see cref="ProviderErrorKind.Network" />; anything else
    ///     stays <see cref="ProviderErrorKind.Unknown" />.
    /// </summary>
    public static ProviderErrorKind FromException(Exception ex, CancellationToken cancellationToken = default)
    {
        if (ex is OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? ProviderErrorKind.Cancelled
                : ProviderErrorKind.Timeout;
        }

        return ex switch
        {
            System.Net.Http.HttpRequestException => ProviderErrorKind.Network,
            IOException => ProviderErrorKind.Network,
            System.Net.Sockets.SocketException => ProviderErrorKind.Network,
            _ => ProviderErrorKind.Unknown
        };
    }

    /// <summary>
    ///     Maximum provider error-body characters surfaced on the user-facing
    ///     message (#259). A single 429 JSON body carries KBs of nested detail;
    ///     past the head it carries no actionable signal, while every renderer
    ///     paints <c>ErrorEvent.Message</c> inline (<c>[error]</c> lines in
    ///     plain/ANSI, <c>!</c> lines inline, chat lines in Spectre) and only
    ///     CellForge routes <c>AgentErrorEvent</c> through a collapsed card.
    ///     Bounding here keeps every backend single-screen; the full body is
    ///     retained on <see cref="ErrorEvent.Exception" /> for diagnostics.
    /// </summary>
    public const int MaxProviderErrorBodyChars = 1000;

    /// <summary>
    ///     Trims <paramref name="body" /> head-kept / tail-cut: the actionable
    ///     head (status line, first error detail) is preserved verbatim, the tail
    ///     is replaced by a marker carrying the original length. Short payloads
    ///     pass through untouched (same reference, zero allocation).
    /// </summary>
    /// <param name="body">Full provider error body text.</param>
    /// <returns>Original text when within budget, otherwise head + marker.</returns>
    public static string TruncateErrorBody(string body)
    {
        if (body.Length <= MaxProviderErrorBodyChars)
        {
            return body;
        }

        return string.Concat(
            body.AsSpan(0, MaxProviderErrorBodyChars),
            $"\n…[truncated {body.Length - MaxProviderErrorBodyChars} chars; showing first {MaxProviderErrorBodyChars}]");
    }

    /// <summary>
    ///     Builds the user-facing wire-error message for a non-success HTTP
    ///     response (#259): <c>"{label} error {status}: {truncated body}"</c>.
    ///     Single choke point so every provider bounds the blob identically.
    /// </summary>
    /// <param name="apiErrorLabel">Provider label, e.g. <c>"OpenAI API"</c>.</param>
    /// <param name="statusCode">HTTP status code of the failed response.</param>
    /// <param name="errorBody">Full raw response body.</param>
    /// <returns>Bounded user-facing message.</returns>
    public static string BuildProviderErrorMessage(string apiErrorLabel, int statusCode, string errorBody) =>
        $"{apiErrorLabel} error {statusCode}: {TruncateErrorBody(errorBody)}";
}
