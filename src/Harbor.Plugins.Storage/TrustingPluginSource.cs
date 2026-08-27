using System.Runtime.CompilerServices;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Storage;

/// <summary>
///     <see cref="IPluginSource" /> decorator that consults an
///     <see cref="IPluginTrustPolicy" /> for every script before yielding it. Untrusted
///     scripts are dropped with a warning — the downstream compile/instantiate pipeline
///     never sees them.
/// </summary>
/// <remarks>
///     This is the security seam between "a file exists in a plugin directory" and
///     "Harbor will execute its code". Wrap any untrusted origin (e.g. project-local
///     <see cref="FileSystemPluginSource" />) with it at the composition root.
/// </remarks>
public sealed class TrustingPluginSource : IPluginSource
{
    private readonly IPluginSource _inner;
    private readonly ILogger<TrustingPluginSource> _logger;
    private readonly IPluginTrustPolicy _policy;

    /// <summary>
    ///     Construct a trust-gated view over <paramref name="inner" />.
    /// </summary>
    /// <param name="inner">Source to enumerate.</param>
    /// <param name="policy">Trust policy consulted per script, in stream order.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public TrustingPluginSource(IPluginSource inner, IPluginTrustPolicy policy, ILogger<TrustingPluginSource> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginScript> GetScriptsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var script in _inner.GetScriptsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var decision = await _policy.DecideAsync(script, ct).ConfigureAwait(false);
            if (decision == PluginTrustDecision.Trusted)
            {
                yield return script;
            }
            else
            {
                _logger.LogWarning(
                    "Skipped untrusted CS-source plugin {Path} (hash {Hash}) — approve via the trust prompt or remove it",
                    script.Path,
                    script.Hash);
            }
        }
    }
}
