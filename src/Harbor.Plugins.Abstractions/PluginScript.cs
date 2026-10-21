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
///     <para>
///         <b>The manifest file wins over the directive (#1739).</b> A plugin may
///         ship a JSON manifest next to its source, and when that manifest declares
///         a capability set, the directive is not read at all: the declaration moves
///         out of the comment and into data the host can read without the code. A
///         manifest that does not declare capabilities leaves the directive in
///         charge, so every plugin written before the manifest existed behaves
///         exactly as it did.
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
    /// <param name="manifest">
    ///     The manifest that was read for this plugin, or <c>null</c> when the
    ///     plugin ships none.
    /// </param>
    /// <param name="manifestError">
    ///     Why the manifest could not be read, or <c>null</c> when there is no
    ///     manifest or the manifest is valid. A script carrying an error is a
    ///     plugin the host must refuse: the plugin shipped a declaration, and
    ///     the host failed to apply it.
    /// </param>
    public PluginScript(
        string path,
        string source,
        PluginManifest? manifest = null,
        string? manifestError = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Manifest = manifest;
        ManifestError = manifestError;
        Hash = ComputeHash(source, manifest);

        // The manifest file replaces the comment directive when it declares
        // capabilities. A manifest that does not declare them leaves the
        // directive in charge — the pre-manifest behaviour, unchanged.
        DeclaredCapabilities = manifest?.Capabilities ?? ParseCapabilities(source);
    }

    private PluginScript(
        string path,
        string source,
        PluginManifest? manifest,
        string? manifestError,
        IReadOnlySet<PluginCapability> narrowed)
        : this(path, source, manifest, manifestError) => DeclaredCapabilities = narrowed;

    /// <summary>
    ///     Copy of this script whose capability set is narrowed to the user-approved
    ///     grants (intersected with the declared manifest — the trust gate narrows,
    ///     never widens). Path, source, hash and manifest are shared; downstream enforcement
    ///     points (sandbox ALC, tool sandbox) read <see cref="DeclaredCapabilities" />
    ///     and therefore enforce the approved subset.
    /// </summary>
    public PluginScript WithGrantedCapabilities(IReadOnlySet<PluginCapability> granted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        return new PluginScript(
            Path,
            Source,
            Manifest,
            ManifestError,
            granted.Where(DeclaredCapabilities.Contains).ToHashSet());
    }

    /// <summary>
    ///     Copy of this script with the manifest that was read for it attached.
    /// </summary>
    /// <remarks>
    ///     The content hash is recomputed, because the manifest is part of the
    ///     plugin's content. The capability set is re-derived: a manifest that
    ///     declares capabilities replaces the comment directive, and a manifest
    ///     that does not leaves the directive in charge.
    /// </remarks>
    /// <param name="manifest">The manifest, or <c>null</c> when the plugin ships none.</param>
    /// <param name="manifestError">
    ///     Why the manifest could not be read, or <c>null</c> when there is no
    ///     manifest or the manifest is valid.
    /// </param>
    /// <returns>The script with the manifest attached.</returns>
    public PluginScript WithManifest(PluginManifest? manifest, string? manifestError = null) =>
        new(Path, Source, manifest, manifestError);

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
    ///     The manifest the plugin shipped next to its source, or <c>null</c> when
    ///     the plugin ships none.
    /// </summary>
    /// <remarks>
    ///     The manifest is read as data, before the plugin is compiled, so the
    ///     host can refuse an incompatible plugin without paying to build it.
    /// </remarks>
    public PluginManifest? Manifest { get; }

    /// <summary>
    ///     Why the manifest could not be read, or <c>null</c> when the plugin
    ///     ships no manifest or the manifest is valid.
    /// </summary>
    /// <remarks>
    ///     A script carrying an error is a plugin the host must refuse. The
    ///     plugin shipped a declaration and the host could not apply it, and
    ///     loading it anyway would mean running a plugin whose own constraints
    ///     were silently dropped.
    /// </remarks>
    public string? ManifestError { get; }

    /// <summary>
    ///     Lowercase hex SHA-256 of the plugin's content. Used as the cache key for the
    ///     compiled assembly and as the key of the persisted trust decision — files with
    ///     the same hash skip recompilation on subsequent loads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A plugin without a manifest hashes exactly as it always did: the
    ///         SHA-256 of the source text. A plugin WITH a manifest hashes the
    ///         source digest and the manifest digest together, so a manifest
    ///         that changes is a plugin that changed — the compiled cache entry
    ///         and the trust decision that was given for the old manifest are
    ///         both stale, and the host says so instead of reusing them.
    ///     </para>
    ///     <para>
    ///         The manifest is part of the content because the manifest is part
    ///         of the plugin: it declares the capabilities the plugin asked for,
    ///         and a trust decision given for a different declaration is not a
    ///         decision about this plugin.
    ///     </para>
    /// </remarks>
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
    ///     The content hash of the plugin, as lowercase hex.
    /// </summary>
    /// <remarks>
    ///     A plugin without a manifest hashes to the SHA-256 of its source text,
    ///     exactly as before the manifest existed. A plugin with a manifest
    ///     hashes the source digest and the manifest digest together, so an
    ///     edited manifest is an edited plugin.
    /// </remarks>
    private static string ComputeHash(string source, PluginManifest? manifest)
    {
        byte[] sourceHash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        if (manifest is null)
            return Convert.ToHexString(sourceHash).ToLowerInvariant();

        byte[] manifestHash = Encoding.UTF8.GetBytes(manifest.ContentHash);
        byte[] combined = new byte[sourceHash.Length + manifestHash.Length];
        sourceHash.CopyTo(combined, 0);
        manifestHash.CopyTo(combined, sourceHash.Length);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }

    private static readonly IReadOnlySet<PluginCapability> FrozenEmpty =
        new HashSet<PluginCapability>();
}
