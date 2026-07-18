using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
namespace Harbor.Plugins.Runtime.Storage;

/// <summary>
///     <see cref="IPluginSource" /> that reads <c>.cs</c> files from
/// <see cref="Assembly.GetManifestResourceStream(string)" /> entries. Each entry is
/// decoded as UTF-8 and wrapped in a <see cref="PluginScript" /> whose
/// <see cref="PluginScript.Path" /> is the resource name (for diagnostics only).
/// </summary>
/// <remarks>
///     <para>
///         This source is useful when shipping "always-on" plugins embedded inside a
///         host assembly — e.g. default tooling that should not require the user to drop
///         files into <c>~/.harbor/plugins/</c>.
///     </para>
///     <para>
///         Resources are matched by suffix: any resource whose name ends with
///         <c>.cs</c> (configurable via the <c>resourceSuffix</c> constructor parameter)
///         is treated as a plugin script. The match is case-sensitive.
///     </para>
/// </remarks>
public sealed class EmbeddedResourcePluginSource : IPluginSource
{
    private readonly Assembly _assembly;
    private readonly string _resourceSuffix;
    private readonly ILogger<EmbeddedResourcePluginSource> _logger;

    /// <summary>
    ///     Construct a new embedded-resource plugin source.
    /// </summary>
    /// <param name="assembly">Assembly whose manifest resources contain the plugin scripts.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="resourceSuffix">Resource name suffix to match. Defaults to <c>.cs</c>.</param>
    public EmbeddedResourcePluginSource(
        Assembly assembly,
        ILogger<EmbeddedResourcePluginSource> logger,
        string resourceSuffix = ".cs")
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resourceSuffix = resourceSuffix ?? throw new ArgumentNullException(nameof(resourceSuffix));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PluginScript> GetScriptsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string[] resourceNames = _assembly.GetManifestResourceNames();
        foreach (var name in resourceNames)
        {
            ct.ThrowIfCancellationRequested();
            if (!name.EndsWith(_resourceSuffix, StringComparison.Ordinal))
                continue;

            using var stream = _assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                _logger.LogWarning("Embedded resource {Name} reported but stream was null", name);
                continue;
            }

            string source;
            try
            {
                using var reader = new StreamReader(stream, leaveOpen: false);
                source = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read embedded resource {Name}", name);
                continue;
            }

            yield return new PluginScript(name, source);
        }
    }
}
