// PluginManifestReader.cs — reads a plugin's manifest from disk (#1739).
//
// WHAT THIS READS
// ---------------
// A plugin that lives in `<dir>/<Name>.cs` may ship a manifest next to it, in
// `<dir>/<Name>.harbor-plugin.json`. The manifest is DATA: a JSON document the
// host can read without compiling the plugin, without loading its assembly and
// without running a line of its code. That is what makes the compatibility
// check possible before the plugin is built, and what makes an index of the
// installed plugins possible at all.
//
// WHY THE FILE IS NAMED AFTER THE SOURCE
// --------------------------------------
// A plugin in Harbor is ONE `.cs` file, and a directory may hold several of
// them. A manifest named `harbor-plugin.json` would belong to the DIRECTORY,
// and a directory with two plugins would have to share one declaration of name
// and version — which is exactly the ambiguity the manifest is supposed to
// remove. Naming the file after the source it describes gives one manifest one
// plugin, with no directory-level rule to remember and no second file to keep
// in sync.
//
// WHY THE PARSE IS REFLECTION-FREE
// --------------------------------
// The manifest is read through a source-generated `JsonSerializerContext`, so
// the read works under NativeAOT and never touches reflection. The document is
// walked as a `JsonElement` rather than bound to a DTO, for two reasons: the
// error messages can name the offending FIELD ("the manifest field 'harbor'
// must be a version string"), and the fields this build does not know can be
// listed instead of silently swallowed — a manifest written for a newer Harbor
// still loads here.
//
// WHAT IS REFUSED, AND WHY
// ------------------------
// A manifest that cannot be understood is refused, never guessed at:
//
//   * a JSON document that is not an object, or does not parse;
//   * a schema version this build does not implement;
//   * a version or a Harbor floor that is not a version;
//   * a capability name that is not in `PluginCapabilities` — the same strict
//     reading the comment directive gets, because an unknown capability is a
//     request the host cannot honour;
//   * an extension axis that is not in `PluginAxisRegistry`.
//
// A refused manifest is a REFUSAL of the plugin, not a fallback to the
// no-manifest behaviour: the plugin shipped a declaration, the declaration
// could not be read, and loading it anyway would mean running a plugin whose
// own constraints the host failed to apply.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Storage;

/// <summary>
///     Reads a plugin manifest — the JSON document a plugin ships next to its
///     source — into a <see cref="PluginManifest" />.
/// </summary>
/// <remarks>
///     <para>
///         The manifest is optional. A plugin without one is a plugin the host
///         knows nothing extra about, and it loads exactly as it did before
///         the manifest existed.
///     </para>
/// </remarks>
public static class PluginManifestReader
{
    /// <summary>
    ///     The suffix of a manifest file. The manifest of
    ///     <c>MyPlugin.cs</c> is <c>MyPlugin.harbor-plugin.json</c>, next to it.
    /// </summary>
    public const string FileSuffix = ".harbor-plugin.json";

    /// <summary>The manifest fields this build understands.</summary>
    private static readonly string[] KnownFields =
        ["schemaVersion", "name", "version", "harbor", "capabilities", "axes"];

    /// <summary>
    ///     The manifest path that belongs to a plugin source path: the same
    ///     directory, the same base name, the manifest suffix.
    /// </summary>
    /// <param name="scriptPath">The path of the plugin's source file.</param>
    /// <returns>The path the manifest would have, whether or not it exists.</returns>
    public static string PathFor(string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        string directory = Path.GetDirectoryName(scriptPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(scriptPath);
        return Path.Combine(directory, name + FileSuffix);
    }

    /// <summary>
    ///     Read and validate the manifest at <paramref name="manifestPath" />.
    /// </summary>
    /// <param name="manifestPath">The manifest file to read.</param>
    /// <param name="logger">
    ///     Optional logger. When supplied, manifest fields this build does not
    ///     understand are reported, so a typo in a field name is visible
    ///     instead of silently ignored.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success with the manifest, or failure with a reason that names the
    ///     file and the field at fault. A manifest that cannot be read is a
    ///     refusal of the plugin, not a fallback to the no-manifest behaviour.
    /// </returns>
    public static async Task<Result<PluginManifest>> ReadAsync(
        string manifestPath,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<PluginManifest>(
                $"Failed to read the plugin manifest '{manifestPath}': {ex.Message}");
        }

        string contentHash = ContentHashOf(bytes);
        string text = Encoding.UTF8.GetString(bytes);
        return Parse(text, contentHash, manifestPath, logger);
    }

    /// <summary>
    ///     Parse a manifest from its text. The content hash is computed over
    ///     the UTF-8 bytes of <paramref name="text" />.
    /// </summary>
    /// <param name="text">The manifest document.</param>
    /// <param name="origin">The file name used in the messages.</param>
    /// <param name="logger">Optional logger for unknown fields.</param>
    /// <returns>Success with the manifest, or failure with a reason.</returns>
    public static Result<PluginManifest> Parse(string text, string origin, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text, ContentHashOf(Encoding.UTF8.GetBytes(text)), origin, logger);
    }

    /// <summary>
    ///     The SHA-256 of the manifest's raw bytes, as lowercase hex. It is
    ///     part of the plugin's content hash, so editing the manifest
    ///     invalidates the trust decision that was given for the old one.
    /// </summary>
    /// <param name="bytes">The manifest's raw bytes.</param>
    /// <returns>The lowercase hex digest.</returns>
    private static string ContentHashOf(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Result<PluginManifest> Parse(
        string text,
        string contentHash,
        string origin,
        ILogger? logger)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize(text, PluginManifestJsonContext.Default.JsonElement);
        }
        catch (JsonException ex)
        {
            return Result.Failure<PluginManifest>(
                $"The plugin manifest '{origin}' is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<PluginManifest>(
                $"The plugin manifest '{origin}' must be a JSON object, but it is {root.ValueKind}.");
        }

        Dictionary<string, JsonElement> fields = new(StringComparer.OrdinalIgnoreCase);
        List<string> unknown = [];
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (KnownFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                fields[property.Name] = property.Value;
            }
            else
            {
                unknown.Add(property.Name);
            }
        }

        if (unknown.Count > 0)
        {
            logger?.LogWarning(
                "The plugin manifest {Manifest} declares fields this Harbor does not know: {Fields}. They are ignored.",
                origin,
                string.Join(", ", unknown));
        }

        return Build(fields, contentHash, origin);
    }

    /// <summary>
    ///     Turn the collected fields into a manifest, or refuse the plugin with
    ///     the field at fault.
    /// </summary>
    /// <param name="fields">The fields the manifest declares.</param>
    /// <param name="contentHash">The manifest's content hash.</param>
    /// <param name="origin">The file name used in the messages.</param>
    /// <returns>Success with the manifest, or failure with a reason.</returns>
    private static Result<PluginManifest> Build(
        IReadOnlyDictionary<string, JsonElement> fields,
        string contentHash,
        string origin)
    {
        int schemaVersion = PluginManifest.SupportedSchemaVersion;
        if (fields.TryGetValue("schemaVersion", out JsonElement schemaElement))
        {
            if (schemaElement.ValueKind != JsonValueKind.Number || !schemaElement.TryGetInt32(out schemaVersion))
            {
                return Result.Failure<PluginManifest>(
                    $"The plugin manifest '{origin}' declares a 'schemaVersion' that is not a whole number.");
            }

            if (schemaVersion != PluginManifest.SupportedSchemaVersion)
            {
                return Result.Failure<PluginManifest>(
                    $"The plugin manifest '{origin}' declares schema version {schemaVersion}, and this Harbor "
                    + $"understands version {PluginManifest.SupportedSchemaVersion}. The plugin was not loaded.");
            }
        }

        string? name = null;
        if (fields.TryGetValue("name", out JsonElement nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<PluginManifest>(
                    $"The plugin manifest '{origin}' declares a 'name' that is not a string.");
            }

            name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Result.Failure<PluginManifest>(
                    $"The plugin manifest '{origin}' declares an empty 'name'.");
            }
        }

        Version? version = null;
        if (fields.TryGetValue("version", out JsonElement versionElement))
        {
            var read = ReadVersion(versionElement, "version", origin);
            if (read.IsSuccess)
            {
                version = read.Value;
            }
            else
            {
                return Result.Failure<PluginManifest>(read.Error);
            }
        }

        Version? harbor = null;
        if (fields.TryGetValue("harbor", out JsonElement harborElement))
        {
            var read = ReadVersion(harborElement, "harbor", origin);
            if (read.IsSuccess)
            {
                harbor = read.Value;
            }
            else
            {
                return Result.Failure<PluginManifest>(read.Error);
            }
        }

        IReadOnlySet<PluginCapability>? capabilities = null;
        if (fields.TryGetValue("capabilities", out JsonElement capabilitiesElement))
        {
            var read = ReadCapabilities(capabilitiesElement, origin);
            if (read.IsSuccess)
            {
                capabilities = read.Value;
            }
            else
            {
                return Result.Failure<PluginManifest>(read.Error);
            }
        }

        IReadOnlyList<string>? axes = null;
        if (fields.TryGetValue("axes", out JsonElement axesElement))
        {
            var read = ReadAxes(axesElement, origin);
            if (read.IsSuccess)
            {
                axes = read.Value;
            }
            else
            {
                return Result.Failure<PluginManifest>(read.Error);
            }
        }

        return Result.Success(new PluginManifest
        {
            SchemaVersion = schemaVersion,
            Name = name,
            Version = version,
            RequiredHarborVersion = harbor,
            Capabilities = capabilities,
            Axes = axes,
            ContentHash = contentHash,
        });
    }

    /// <summary>
    ///     Read one version-shaped field.
    /// </summary>
    /// <param name="element">The field value.</param>
    /// <param name="field">The field name, for the message.</param>
    /// <param name="origin">The file name used in the message.</param>
    /// <returns>Success with the version, or failure with a reason.</returns>
    private static Result<Version> ReadVersion(JsonElement element, string field, string origin)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return Result.Failure<Version>(
                $"The plugin manifest '{origin}' declares a '{field}' that is not a string.");
        }

        string text = element.GetString() ?? string.Empty;
        if (!Version.TryParse(text, out Version? version))
        {
            return Result.Failure<Version>(
                $"The plugin manifest '{origin}' declares '{field}' as '{text}', which is not a version. "
                + "A version is written as 'major.minor' or 'major.minor.build'.");
        }

        return Result.Success<Version>(version);
    }

    /// <summary>
    ///     Read the declared capabilities.
    /// </summary>
    /// <param name="element">The field value.</param>
    /// <param name="origin">The file name used in the message.</param>
    /// <returns>Success with the capabilities, or failure with a reason.</returns>
    private static Result<IReadOnlySet<PluginCapability>> ReadCapabilities(JsonElement element, string origin)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return Result.Failure<IReadOnlySet<PluginCapability>>(
                $"The plugin manifest '{origin}' declares 'capabilities' that is not an array.");
        }

        var names = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<IReadOnlySet<PluginCapability>>(
                    $"The plugin manifest '{origin}' declares a 'capabilities' entry that is not a string.");
            }

            names.Add(item.GetString() ?? string.Empty);
        }

        // Strict, exactly like the comment directive: an unknown capability is a
        // request the host cannot honour, so the whole manifest is refused.
        Result<IReadOnlySet<PluginCapability>> parsed = PluginCapabilities.TryParse(string.Join(",", names));
        if (parsed.IsSuccess)
        {
            return Result.Success(parsed.Value);
        }

        return Result.Failure<IReadOnlySet<PluginCapability>>(
            $"The plugin manifest '{origin}' declares a capability this Harbor does not know: "
            + $"{parsed.Error}");
    }

    /// <summary>
    ///     Read the declared extension axes.
    /// </summary>
    /// <param name="element">The field value.</param>
    /// <param name="origin">The file name used in the message.</param>
    /// <returns>Success with the axis names, or failure with a reason.</returns>
    private static Result<IReadOnlyList<string>> ReadAxes(JsonElement element, string origin)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return Result.Failure<IReadOnlyList<string>>(
                $"The plugin manifest '{origin}' declares 'axes' that is not an array.");
        }

        var axes = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<IReadOnlyList<string>>(
                    $"The plugin manifest '{origin}' declares an 'axes' entry that is not a string.");
            }

            string axis = item.GetString() ?? string.Empty;
            if (!PluginAxisRegistry.All.Any(a => string.Equals(a.Marker, axis, StringComparison.Ordinal)))
            {
                string known = string.Join(", ", PluginAxisRegistry.All.Select(a => a.Marker));
                return Result.Failure<IReadOnlyList<string>>(
                    $"The plugin manifest '{origin}' declares the extension axis '{axis}', and this Harbor "
                    + $"has no such axis. The axes are: {known}.");
            }

            axes.Add(axis);
        }

        return Result.Success<IReadOnlyList<string>>(axes);
    }
}

/// <summary>
///     Source-generated JSON metadata for the plugin manifest (AOT-safe).
/// </summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;
