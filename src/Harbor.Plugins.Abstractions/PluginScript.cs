using System.Security.Cryptography;
using System.Text;
namespace Harbor.Plugins.Abstractions;
/// <summary>
///     Immutable wrapper around a single CS-source plugin file: its on-disk path, raw source
///     text, and a deterministic SHA-256 content hash used for caching compiled assemblies.
/// </summary>
/// <remarks>
///     <para>
///         The capability manifest is declared as a comment directive in the source:
///         <c>// harbor:capabilities read_files,http_requests</c> (first match wins).
///         Parsing is strict — an unrecognized capability name makes the manifest
///         invalid and the plugin must be denied (fail-closed). A plugin that declares
///         no capabilities gets none: it can still compute and return strings, but
///         cannot touch files, processes or the network through sandbox-guarded APIs.
///     </para>
/// </remarks>
public sealed class PluginScript
{
    /// <summary>
    ///     Manifest directive prefix scanned in the source (line-anchored).
    /// </summary>
    public const string CapabilityDirective = "harbor:capabilities";

    private static readonly System.Text.RegularExpressions.Regex CapabilitiesPattern = new(
        // Colon after the directive keyword is accepted but not required — the
        // documented form is '// harbor:capabilities read_files,http_requests'.
        @"^\s*//\s*harbor:capabilities:?\s*(?<caps>[^\r\n]+)",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    ///     Construct a new <see cref="PluginScript" /> from a file path.
    /// </summary>
    /// <param name="path">Absolute path to the .cs file.</param>
    /// <param name="source">Raw source text of the file (UTF-8 decoded).</param>
    public PluginScript(string path, string source)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Hash = ComputeHash(source);
        DeclaredCapabilities = ParseCapabilities(source);
    }

    /// <summary>
    ///     Capabilities declared by the plugin manifest. Empty set = no capabilities
    ///     (fail-closed). An invalid manifest token results in an empty set plus
    ///     <see cref="HasInvalidManifest" /> set to <see langword="true" />.
    /// </summary>
    public IReadOnlySet<PluginCapability> DeclaredCapabilities { get; }

    /// <summary>
    ///     <see langword="true" /> when the manifest directive contains an unknown
    ///     capability token. Such plugins must be rejected by the trust policy
    ///     regardless of user choice — fail-closed.
    /// </summary>
    public bool HasInvalidManifest { get; private set; }

    /// <summary>
    ///     Extract the declared capabilities from the plugin source. Sets
    ///     <see cref="HasInvalidManifest" /> when a directive exists but contains an
    ///     unknown token; in that case the returned set is empty (deny-all).
    /// </summary>
    private IReadOnlySet<PluginCapability> ParseCapabilities(string source)
    {
        var match = CapabilitiesPattern.Match(source);
        if (!match.Success)
            return FrozenEmpty;

        var parse = PluginCapabilities.TryParse(match.Groups["caps"].Value);
        if (parse.IsSuccess)
            return parse.Value;

        HasInvalidManifest = true;
        return FrozenEmpty;
    }

    /// <summary>The absolute on-disk path of the plugin source file.</summary>
    public string Path { get; }

    /// <summary>The raw source text of the plugin.</summary>
    public string Source { get; }

    /// <summary>
    ///     Lowercase hex SHA-256 of <see cref="Source" />. Used as the cache key for the
    ///     compiled assembly — files with the same hash skip recompilation on subsequent loads.
    /// </summary>
    public string Hash { get; }

    /// <summary>
    ///     Load a <see cref="PluginScript" /> from disk, reading the file as UTF-8.
    /// </summary>
    /// <param name="path">Absolute path to the .cs file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with the script wrapper, or failure if the file cannot be read.</returns>
    public static async Task<Result<PluginScript>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<PluginScript>("Plugin path cannot be empty.");

        if (!File.Exists(path))
            return Result.Failure<PluginScript>($"Plugin file not found: {path}");

        try
        {
            string source = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            return Result.Success(new PluginScript(path, source));
        }
        catch (IOException ex)
        {
            return Result.Failure<PluginScript>($"Failed to read plugin '{path}': {ex.Message}");
        }
    }

    /// <summary>
    ///     SHA-256 of the source text, returned as lowercase hex. Used as cache key.
    /// </summary>
    private static string ComputeHash(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static readonly IReadOnlySet<PluginCapability> FrozenEmpty =
        new HashSet<PluginCapability>();
}
