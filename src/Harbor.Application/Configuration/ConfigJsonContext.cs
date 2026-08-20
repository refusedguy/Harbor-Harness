using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Core.Configuration;

/// <summary>
///     AOT-safe JsonSerializerContext for the Harbor application configuration
///     types. All types serialized by <see cref="JsonConfigStore" /> (which
///     persists HarborConfig to ~/.harbor/config.json) are registered here so
///     the trimmer keeps them and NativeAOT pre-generates the serialization code.
/// </summary>
[JsonSerializable(typeof(RawConfigDto))]
[JsonSerializable(typeof(CompactionConfig))]
[JsonSerializable(typeof(ProviderConfigEntry))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, ProviderConfigEntry>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
    private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    ///     Pre-configured options used by <see cref="JsonConfigStore" /> for
    ///     human-readable config.json output. Use <c>new(ConfigJsonContext._opts)</c>
    ///     when a non-default constructor instance of the context is needed,
    ///     or access <c>Default.RawConfigDto</c> which is bound to these options.
    /// </summary>
    public static readonly JsonSerializerOptions ConfigOptions = _opts;

    /// <summary>
    ///     Context instance that carries the Web-default options (camelCase, etc.).
    ///     Prefer this over <see cref="Default" /> which uses plain <c>new()</c>.
    /// </summary>
    public static ConfigJsonContext OptionsContext => new(_opts);
}
