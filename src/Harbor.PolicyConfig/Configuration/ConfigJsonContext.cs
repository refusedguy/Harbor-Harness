using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;

namespace Harbor.Application.Configuration;

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
[JsonSerializable(typeof(Dictionary<string, List<PermissionRule>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PermissionRule))]
// #1734: the per-plugin configuration sections. A section is a plugin's own
// settings, and what it means is the plugin's schema, so the host stores it
// as it was written rather than binding it to a type of its own. The
// JsonElement is the whole section, and it is written back unchanged.
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
