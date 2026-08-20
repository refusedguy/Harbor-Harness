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
}
