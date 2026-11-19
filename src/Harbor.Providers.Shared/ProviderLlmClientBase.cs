// Shared source: compiled INTO each Harbor.Providers.* assembly via
// <Compile Include> link items, exactly like SsePump.cs. Template Method (GOF):
// the StreamAsync skeleton — channel + Task.Run pump + auth resolution + a
// per-call ChunkStreamState + SsePump + terminal ErrorEvent + writer completion
// + reader loop — lives here once (#1499). Providers override only BuildRequest,
// WriteEventsAsync and the small hooks that carry their request/observability
// differences.
//
// Hot path: the SSE data callback is ONE delegate created per stream (not per
// line), and ChunkStreamState is created fresh per StreamAsync call — never
// instance state (§OOP-001). No per-line allocation is added by this extract.

using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.Providers.Internal;

// Public because the four public clients derive from it (CS0060). The clients
// declare ILlmClient themselves; this base deliberately does not, so each
// provider stays a direct seam implementer in the generated seam graph.
public abstract class ProviderLlmClientBase
{
    // Null only for keyless clients (Ollama talks to a local daemon that needs
    // no API key). Every keyed client passes a resolver and keeps the exact
    // auth-failure shape below.
    private readonly IAuthResolver? _auth;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    protected ProviderLlmClientBase(HttpClient http, IAuthResolver auth, ILogger logger, ProviderId providerId)
    {
        _http = http;
        _auth = auth;
        _logger = logger;
        ProviderId = providerId;
    }

    /// <summary>Constructor for keyless clients — no auth resolution is attempted (#3767).</summary>
    protected ProviderLlmClientBase(HttpClient http, ILogger logger, ProviderId providerId)
    {
        _http = http;
        _auth = null;
        _logger = logger;
        ProviderId = providerId;
    }

    public ProviderId ProviderId { get; }

    /// <summary>
    ///     Logger shared with the template's hooks. Source-generated
    ///     <c>[LoggerMessage]</c> methods take a non-generic <see cref="ILogger" />,
    ///     so provider log classes can use this directly.
    /// </summary>
    protected ILogger Logger => _logger;

    public abstract Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Template method: the single StreamAsync skeleton behind the native
    ///     LLM clients. The variation points are the abstract members and the
    ///     virtual hooks below; stream/transport behaviour is identical for all.
    /// </summary>
    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<LlmEvent> channel = CreateChannel();
        var writer = channel.Writer;

        // #1104: the pump runs even for a pre-cancelled token — a token handed
        // to Task.Run would skip the delegate and leave the writer uncompleted.
        // The token still flows into every await, and the finally always
        // completes the writer. Clients that historically scheduled with the
        // token opt back in through SchedulePumpWithToken.
        var pump = Task.Run(async () =>
        {
            var activity = StartStreamActivity(request);
            try
            {
                string apiKey = string.Empty;
                if (_auth is not null)
                {
                    var apiKeyResult = await _auth.ResolveApiKeyAsync(ProviderId.Value, cancellationToken).ConfigureAwait(false);
                    if (apiKeyResult.IsFailure)
                    {
                        await writer.WriteAsync(
                            new ErrorEvent($"Auth failed: {apiKeyResult.Error}", Kind: ProviderErrorKind.Auth),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    apiKey = apiKeyResult.Value;
                }

                var httpRequest = BuildRequest(request, apiKey);

                // §OOP-001 (RESOLVED): per-StreamAsync-call parsing state — the
                // tool-call id map and malformed-chunk counters live per call,
                // never on this singleton client.
                var chunkState = new ChunkStreamState();

                if (RawLineMode)
                {
                    // #3767: Ollama's NDJSON has no `data:` prefix and no
                    // [DONE] sentinel — every line is a payload and EOF ends
                    // the stream. One adapter delegate per stream (not per
                    // line) keeps the zero per-line allocation guarantee.
                    await SsePump.RunAsync(
                        writer, _http, httpRequest,
                        (line, token) => WriteRawLineAsync(line, writer, chunkState, request, activity, token),
                        ApiErrorLabel, _logger, cancellationToken,
                        onResponse: ResponseHook(activity),
                        mapSendFailure: SendFailureHook(activity, cancellationToken),
                        onTransportError: TransportErrorHook(activity),
                        onComplete: () => OnStreamComplete(chunkState, activity)).ConfigureAwait(false);
                }
                else
                {
                    await SsePump.RunSseAsync(
                        writer, _http, httpRequest,
                        (data, token) => WriteEventsAsync(data, writer, chunkState, request, activity, token),
                        ApiErrorLabel, _logger, cancellationToken,
                        onResponse: ResponseHook(activity),
                        mapSendFailure: SendFailureHook(activity, cancellationToken),
                        onTransportError: TransportErrorHook(activity),
                        onComplete: () => OnStreamComplete(chunkState, activity)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                OnCanceled(activity);
            }
            catch (Exception ex)
            {
                OnFailed(ex, activity);
                await writer.WriteAsync(new ErrorEvent(
                    $"Stream failed: {ex.Message}", ex.ToString(),
                    ProviderErrors.FromException(ex, cancellationToken)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writer.TryComplete();
                activity?.Dispose();
            }
        }, SchedulePumpWithToken ? cancellationToken : CancellationToken.None);

        if (ObservePumpFault)
        {
            // #1099: keep the fire-and-forget pump observed — a fault-only
            // continuation logs a late pump fault instead of parking it where
            // only finalization could see it.
            _ = pump.ContinueWith(
                t => LogPumpFault(t.Exception?.InnerException ?? t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    // --- variation points ----------------------------------------------------

    /// <summary>Channel shape is provider-specific: bounded backpressure vs unbounded.</summary>
    internal abstract Channel<LlmEvent> CreateChannel();

    /// <summary>Assemble the provider's HTTP request, bearer/auth headers included.</summary>
    internal abstract HttpRequestMessage BuildRequest(LlmRequest request, string apiKey);

    /// <summary>Parse one SSE <c>data:</c> payload and write the resulting events.</summary>
    internal abstract Task WriteEventsAsync(
        string data,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState chunkState,
        LlmRequest request,
        Activity? activity,
        CancellationToken ct);

    /// <summary>Provider label used in non-success HTTP error messages.</summary>
    protected abstract string ApiErrorLabel { get; }

    /// <summary>
    ///     Selects the raw-line pump (<see cref="SsePump.RunAsync" />) for
    ///     NDJSON dialects whose whole line is the payload and whose stream
    ///     ends at EOF. SSE clients leave this false.
    /// </summary>
    protected virtual bool RawLineMode => false;

    /// <summary>Adapts the raw-line handler to the shared event writer contract.</summary>
    private async Task<bool> WriteRawLineAsync(
        string line,
        ChannelWriter<LlmEvent> writer,
        ChunkStreamState chunkState,
        LlmRequest request,
        Activity? activity,
        CancellationToken ct)
    {
        await WriteEventsAsync(line, writer, chunkState, request, activity, ct).ConfigureAwait(false);
        return true; // NDJSON has no sentinel; EOF is the graceful end.
    }

    // --- optional hooks: defaults reproduce the non-traced clients exactly ---

    /// <summary>Start per-call tracing. Null for clients without activity observability.</summary>
    protected virtual Activity? StartStreamActivity(LlmRequest request) => null;

    /// <summary>Native clients historically schedule the pump with the caller token.</summary>
    protected virtual bool SchedulePumpWithToken => false;

    /// <summary>Only the tracing client attaches the fault-only pump observer.</summary>
    protected virtual bool ObservePumpFault => false;

    /// <summary>Optional <c>onResponse</c> observability hook (activity tags).</summary>
    protected virtual Action<HttpResponseMessage>? ResponseHook(Activity? activity) => null;

    /// <summary>Optional send-failure mapping (activity status + provider hint).</summary>
    protected virtual Func<Exception, CancellationToken, ErrorEvent>? SendFailureHook(Activity? activity, CancellationToken ct) => null;

    /// <summary>Optional mid-stream error hook (activity status).</summary>
    protected virtual Action<Exception>? TransportErrorHook(Activity? activity) => null;

    /// <summary>Successful completion: health-summary log, optional activity status.</summary>
    internal virtual void OnStreamComplete(ChunkStreamState chunkState, Activity? activity) { }

    /// <summary>Caller cancellation: optional activity status, no terminal event.</summary>
    protected virtual void OnCanceled(Activity? activity) { }

    /// <summary>Terminal failure: provider log, optional activity status.</summary>
    protected virtual void OnFailed(Exception ex, Activity? activity) { }

    /// <summary>Late pump fault observed by the fault-only continuation.</summary>
    protected virtual void LogPumpFault(Exception ex) { }
}
