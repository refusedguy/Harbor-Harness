using CSharpFunctionalExtensions;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Providers;
/// <summary>
///     Default <see cref="IProviderHealthCheck" /> — probes the provider with a
///     models-list request (the cheapest metadata call every provider supports)
///     through the registered <see cref="ILlmClient" />. Network faults are
///     classified into user-presentable reasons instead of leaking raw
///     exception text into the wizard UI.
/// </summary>
public sealed class ProviderHealthCheck : IProviderHealthCheck
{
    private readonly IProviderRegistry _providers;
    private readonly ILogger<ProviderHealthCheck> _logger;
    private readonly TimeSpan _timeout;

    /// <summary>Construct a health check bound to the provider registry.</summary>
    /// <param name="providers">Registry used to resolve the provider's client.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeout">Per-check budget; defaults to <see cref="IProviderHealthCheck.DefaultTimeout" />.</param>
    public ProviderHealthCheck(
        IProviderRegistry providers,
        ILogger<ProviderHealthCheck> logger,
        TimeSpan? timeout = null)
    {
        _providers = providers;
        _logger = logger;
        _timeout = timeout ?? IProviderHealthCheck.DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<Result<ProviderHealth>> CheckAsync(ProviderId providerId, CancellationToken cancellationToken = default)
    {
        var clientResult = _providers.GetClient(providerId);
        if (clientResult.IsFailure)
            return Result.Failure<ProviderHealth>($"Provider '{providerId.Value}' is not registered.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await clientResult.Value.GetModelsAsync(cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (result.IsFailure)
                return Result.Failure<ProviderHealth>(Classify(result.Error));

            if (result.Value.Count == 0)
                return Result.Failure<ProviderHealth>("Provider responded but exposes no models.");

            _logger.LogDebug("Health check {Provider}: {Count} models in {Ms} ms",
                providerId.Value, result.Value.Count, sw.ElapsedMilliseconds);
            return Result.Success(new ProviderHealth((int)sw.ElapsedMilliseconds, result.Value.Count));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own budget fired — the endpoint did not answer in time.
            return Result.Failure<ProviderHealth>(
                $"Provider '{providerId.Value}' is unreachable (timed out after {_timeout.TotalSeconds:0}s).");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<ProviderHealth>(Classify(ex.Message));
        }
    }

    /// <summary>
    ///     Map a raw failure string onto a user-presentable reason. Order
    ///     matters: auth markers first (a 401 is definitive), then DNS, then
    ///     generic transport.
    /// </summary>
    public static string Classify(string rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return "Unknown error.";

        // 401/403 — the key is present but rejected (or a protected resource
        // was hit without one). Definitive signal; check before transport.
        if (rawError.Contains("401", StringComparison.Ordinal)
            || rawError.Contains("403", StringComparison.Ordinal)
            || rawError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return "API key is invalid or missing (401/403 from provider).";
        }

        // DNS resolution failures (Linux + Windows phrasings).
        if (rawError.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("name resolution", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown host — check the provider URL / your network.";
        }

        // Connection-level failures (refused, reset, unreachable).
        if (rawError.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase)
            || rawError.Contains("ECONNREFUSED", StringComparison.Ordinal))
        {
            return "Provider is unreachable — connection refused/reset.";
        }

        return rawError.EndsWith(".", StringComparison.Ordinal) ? rawError : rawError + ".";
    }
}
