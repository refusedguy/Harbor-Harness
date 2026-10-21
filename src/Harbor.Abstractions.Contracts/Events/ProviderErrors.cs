using Harbor.Abstractions.Extensions;

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
    Cancelled,

    /// <summary>
    ///     The request did not fit the model's context window (#1755).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not transient in the transport sense: the SAME request fails the
    ///         same way, so the retry policy must not spend its budget on it.
    ///         What the agent loop does with it is a different decision — it
    ///         compacts the history and retries the request from the compacted
    ///         view (the reactive branch of compaction).
    ///     </para>
    /// </remarks>
    ContextOverflow = 8
}

/// <summary>
///     Canonical classification helpers shared by all ILlmClient implementations.
///     Single source of truth so every provider maps the same wire condition to
///     the same kind (and therefore the same retry verdict).
/// </summary>
public static class ProviderErrors
{
    /// <summary>
    ///     The canonical overflow markers, one per provider shape (#1755).
    /// </summary>
    /// <remarks>
    ///     The scan is over the whole body, so a marker may sit in a nested
    ///     field; the list is kept tight on purpose — a marker that also
    ///     describes a NON-overflow refusal (an output-token limit, say) would
    ///     send the harness down a compaction it does not need.
    /// </remarks>
    private static readonly string[] ContextOverflowMarkers =
    [
        // OpenAI and the gateways that copy its error shape.
        "context_length_exceeded",
        "maximum_context_length_exceeded",
        "model_context_length_exceeded",
        // Anthropic: "prompt is too long: 250000 tokens > 200000 maximum" and
        // "input length and `max_tokens` exceed context limit: 250000 > 200000".
        "prompt is too long",
        "exceed context limit",
        // Ollama / llama.cpp / vLLM: "the input length exceeds the context window".
        "context length exceeded",
        "context window exceeded",
        "exceeds the context window",
        "exceed the context window",
        "context_window_exceeded",
        // Gateways that report the refusal under a code of their own.
        "input_too_long",
        // OpenAI's prose form: "This model's maximum context length is 8192 tokens."
        "maximum context length",
    ];

    /// <summary>Retry verdict for a transport error kind.</summary>
    public static bool IsTransient(ProviderErrorKind kind) =>
        kind is ProviderErrorKind.RateLimit or ProviderErrorKind.Timeout
            or ProviderErrorKind.Network or ProviderErrorKind.ServerError;

    /// <summary>
    ///     Whether the failure is a context-window overflow (#1755) — the one
    ///     provider failure the agent loop answers by compacting and retrying
    ///     rather than by giving up.
    /// </summary>
    /// <param name="kind">The classification to read.</param>
    /// <returns><see langword="true" /> for a context overflow.</returns>
    public static bool IsContextOverflow(ProviderErrorKind kind) =>
        kind == ProviderErrorKind.ContextOverflow;

    /// <summary>
    ///     Classify a non-success HTTP response, reading the provider's error
    ///     body for the canonical context-overflow markers (#1755).
    /// </summary>
    /// <remarks>
    ///     The overflow is not a status code: OpenAI answers 400 with a
    ///     <c>context_length_exceeded</c> body, Anthropic answers 400 with
    ///     "prompt is too long", Ollama answers 400 with "the input length
    ///     exceeds the context window", and some gateways answer 413 or 422.
    ///     The body is therefore the only place the refusal names itself.
    /// </remarks>
    /// <param name="status">The HTTP status the provider answered with.</param>
    /// <param name="errorBody">The provider's error body, verbatim.</param>
    /// <returns>
    ///     <see cref="ProviderErrorKind.ContextOverflow" /> when the body
    ///     carries a canonical overflow marker; otherwise the plain status
    ///     classification (<see cref="FromStatus" />).
    /// </returns>
    public static ProviderErrorKind ClassifyResponse(
        System.Net.HttpStatusCode status,
        string? errorBody) =>
        IsContextOverflowBody(errorBody) ? ProviderErrorKind.ContextOverflow : FromStatus(status);

    /// <summary>
    ///     Whether the provider's error body carries a canonical context-overflow
    ///     marker (#1755).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The markers are the shapes the providers actually send, not a
    ///         guess: OpenAI's <c>"code": "context_length_exceeded"</c>,
    ///         Anthropic's <c>"prompt is too long: 250000 tokens &gt; 200000
    ///         maximum"</c>, and Ollama's <c>"the input length exceeds the
    ///         context window"</c>, plus the code names the OpenAI-compatible
    ///         gateways copy. The check is a substring scan and not a JSON
    ///         parse because the marker travels in three different shapes — a
    ///         <c>code</c> field, an <c>error</c> string, or free prose — and
    ///         a parse would have to know all three.
    ///     </para>
    ///     <para>
    ///         A false positive costs one compaction, never a wrong answer:
    ///         the retry is granted only when the compacted view is measurably
    ///         smaller, and the original refusal is preserved otherwise.
    ///     </para>
    /// </remarks>
    /// <param name="body">The provider's error body.</param>
    /// <returns><see langword="true" /> when the body names an overflow.</returns>
    public static bool IsContextOverflowBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        for (int i = 0; i < ContextOverflowMarkers.Length; i++)
        {
            if (body.Contains(ContextOverflowMarkers[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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
    public static string TruncateErrorBody(string body) =>
        body.TruncateWithDropCount(MaxProviderErrorBodyChars, detail: $"showing first {MaxProviderErrorBodyChars}");

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
