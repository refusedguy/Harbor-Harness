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
}
