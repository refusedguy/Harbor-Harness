using System.Diagnostics;
using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Diagnostics;

namespace Harbor.Telemetry;

/// <summary>
///     Wraps <see cref="IProviderRegistry" /> so every resolved client is
///     instrumented (O13 fix: Anthropic/OpenAI/Ollama had no span/TTFB at all —
///     one decorator gives all providers identical telemetry by construction).
/// </summary>
public sealed class InstrumentedProviderRegistry(IProviderRegistry inner, IMetrics metrics, ITracer tracer)
    : IProviderRegistry
{
    public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => inner.GetRegisteredProviderIds();

    public Result<ILlmClient> GetClient(ProviderId providerId)
    {
        Result<ILlmClient> resolved = inner.GetClient(providerId);
        return resolved.IsSuccess
            ? Result.Success<ILlmClient>(new InstrumentedLlmClient(providerId, resolved.Value, metrics, tracer))
            : Result.Failure<ILlmClient>(resolved.Error);
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
        => inner.GetAllModelsAsync(cancellationToken);

    // ROP-C П.7: catalog caching is the inner registry's job; the decorator
    // only instruments clients resolved through GetClient.
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
        => inner.GetModelsCachedAsync(providerId, cancellationToken);

    // ROP-A ПР.10: instrumentation is exclusively GetClient's job. Wrapping
    // here TOO would double-instrument any client registered through this
    // registry (TTFB histogram ×2, token counters ×2) — the invariant "exactly
    // one decorator layer" is now structural, not convention.
    public void Register(ProviderId providerId, Func<ILlmClient> factory) =>
        inner.Register(providerId, factory);

    public Result Unregister(ProviderId providerId) => inner.Unregister(providerId);
}

/// <summary>
///     Per-stream telemetry: span llm.stream{llm.model,llm.provider}, TTFB
///     histogram llm.ttfb.ms{llm.model}, token counters
///     llm.tokens{llm.model,token.type} aggregated from StepFinishEvent usage.
/// </summary>
public sealed class InstrumentedLlmClient(
    ProviderId providerId,
    ILlmClient inner,
    IMetrics metrics,
    ITracer tracer) : ILlmClient
{
    public ProviderId ProviderId => inner.ProviderId;

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using Harbor.Diagnostics.ITelemetrySpan? span = tracer.StartSpan(
            "llm.stream",
            new KeyValuePair<string, object?>(TelemetryTagNames.Model, request.Model),
            new KeyValuePair<string, object?>(TelemetryTagNames.Provider, providerId.Value));

        // Manual enumeration lets the wrapper catch/observe failures WITHOUT a
        // try/catch around `yield return` (which C# forbids in iterators).
        IAsyncEnumerator<LlmEvent> source = inner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (source.ConfigureAwait(false))
        {
            long start = Stopwatch.GetTimestamp();
            bool ttfbRecorded = false;
            int promptTokens = 0;
            int completionTokens = 0;

            while (true)
            {
                bool hasNext;
                LlmEvent current;
                try
                {
                    hasNext = await source.MoveNextAsync().ConfigureAwait(false);
                    if (!hasNext)
                    {
                        break;
                    }

                    current = source.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    span?.SetTag(TelemetryTagNames.StreamStatus, "cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    span?.SetError(ex.Message);
                    span?.SetTag(TelemetryTagNames.StreamStatus, "exception");
                    metrics.Counter(
                        "llm.streams", 1,
                        new KeyValuePair<string, object?>("stream.status", "exception"),
                        new KeyValuePair<string, object?>(TelemetryTagNames.Provider, providerId.Value));
                    throw;
                }

                if (!ttfbRecorded)
                {
                    ttfbRecorded = true;
                    double ttfbMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    metrics.Histogram(
                        TelemetryTagNames.LlmTtfbMs, ttfbMs,
                        new KeyValuePair<string, object?>(TelemetryTagNames.Model, request.Model));
                    span?.SetTag("llm.ttfb.ms", Math.Round(ttfbMs, 3));
                }

                if (current is StepFinishEvent { Usage: not null } finish)
                {
                    promptTokens += finish.Usage.InputTokens;
                    completionTokens += finish.Usage.OutputTokens;
                }

                yield return current;
            }

            span?.SetTag(TelemetryTagNames.StreamStatus, "ok");
            metrics.Counter(
                "llm.streams", 1,
                new KeyValuePair<string, object?>("stream.status", "ok"),
                new KeyValuePair<string, object?>(TelemetryTagNames.Provider, providerId.Value));
            RecordTokens(request.Model, promptTokens, completionTokens);
        }
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => inner.GetModelsAsync(cancellationToken);

    private void RecordTokens(string model, int promptTokens, int completionTokens)
    {
        if (promptTokens > 0)
        {
            metrics.Counter(
                TelemetryTagNames.LlmTokens, promptTokens,
                new KeyValuePair<string, object?>(TelemetryTagNames.Model, model),
                new KeyValuePair<string, object?>(TelemetryTagNames.LlmTokenType, "prompt"));
        }

        if (completionTokens > 0)
        {
            metrics.Counter(
                TelemetryTagNames.LlmTokens, completionTokens,
                new KeyValuePair<string, object?>(TelemetryTagNames.Model, model),
                new KeyValuePair<string, object?>(TelemetryTagNames.LlmTokenType, "completion"));
        }
    }
}
