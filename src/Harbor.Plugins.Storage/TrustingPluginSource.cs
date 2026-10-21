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
    private readonly IPluginAuditLog? _audit;

    /// <summary>
    ///     Construct a trust-gated view over <paramref name="inner" />.
    /// </summary>
    /// <param name="inner">Source to enumerate.</param>
    /// <param name="policy">Trust policy consulted per script, in stream order.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="audit">
    ///     Optional audit sink. When supplied, every trust decision is recorded as a
    ///     <c>read_files</c> capability entry on the plugin source file — the first
    ///     capability use in the plugin's lifecycle (Harbor itself reads the file).
    /// </param>
    public TrustingPluginSource(
        IPluginSource inner,
        IPluginTrustPolicy policy,
        ILogger<TrustingPluginSource> logger,
        IPluginAuditLog? audit = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginScript> GetScriptsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var script in _inner.GetScriptsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            // Fail-closed: an unknown capability token in the manifest directive rejects
            // the plugin regardless of user choice (contracted by PluginScript docs), and
            // a manifest file the host could not read rejects it for the same reason —
            // the plugin shipped a declaration the host failed to apply, so there is
            // nothing to ask the user about (#1739).
            bool declarationInvalid = script.HasInvalidManifest || script.ManifestError is not null;
            var decision = declarationInvalid
                ? PluginTrustDecision.Untrusted
                : await _policy.DecideAsync(script, ct).ConfigureAwait(false);
            var trusted = decision == PluginTrustDecision.Trusted;

            if (_audit is not null)
            {
                await _audit.WriteAsync(
                    Path.GetFileNameWithoutExtension(script.Path),
                    PluginCapability.ReadFiles,
                    script.Path,
                    trusted ? "allow" : "deny",
                    trusted ? null : DeclarationError(script),
                    ct).ConfigureAwait(false);
            }

            if (trusted)
            {
                // Narrow the capability set to what the user actually approved before
                // the script reaches the compiler: the sandbox ALC and the tool sandbox
                // both read DeclaredCapabilities downstream, so approval granularity
                // must be baked in here, at the single trust seam.
                yield return script.WithGrantedCapabilities(_policy.GetGrantedCapabilities(script));
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

    /// <summary>
    ///     Why the plugin was not loaded, for the audit trail.
    /// </summary>
    /// <remarks>
    ///     A declaration the host could not read is named as such, because it is
    ///     a different defect from a plugin the user declined to approve.
    /// </remarks>
    /// <param name="script">The refused script.</param>
    /// <returns>The reason to record.</returns>
    private static string DeclarationError(PluginScript script)
    {
        if (script.HasInvalidManifest)
            return "invalid capability manifest — unknown token";
        if (script.ManifestError is not null)
            return $"invalid plugin manifest — {script.ManifestError}";
        return "trust denied — plugin not loaded";
    }
}
