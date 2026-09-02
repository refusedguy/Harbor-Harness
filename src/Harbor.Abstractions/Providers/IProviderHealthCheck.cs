using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Providers;
/// <summary>
///     Cheap "test connection" probe for a registered provider: resolves the
///     provider's model list (a low-cost metadata call) and reports latency +
///     model count, or a human-readable failure reason classified by cause
///     (invalid key / unreachable / unknown host). Used by the onboarding
///     wizard and the /model picker so a bad key surfaces immediately instead
///     of failing on the first chat turn.
/// </summary>
public interface IProviderHealthCheck
{
    /// <summary>Default budget for a single check — generous enough for cold caches.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Probe the provider. Success carries round-trip latency in
    ///     milliseconds and how many models the provider exposes; failure
    ///     carries an already-classified, user-presentable reason.
    /// </summary>
    /// <param name="providerId">Registered provider id to probe.</param>
    /// <param name="cancellationToken">Cooperative cancellation for the caller (wizard close, etc.).</param>
    Task<Result<ProviderHealth>> CheckAsync(ProviderId providerId, CancellationToken cancellationToken = default);
}
/// <summary>Outcome of a successful <see cref="IProviderHealthCheck.CheckAsync" /> probe.</summary>
/// <param name="LatencyMs">Round-trip latency of the models request, in milliseconds.</param>
/// <param name="ModelsCount">Number of models reported by the provider.</param>
public readonly record struct ProviderHealth(int LatencyMs, int ModelsCount);
