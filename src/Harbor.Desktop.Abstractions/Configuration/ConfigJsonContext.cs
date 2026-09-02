// ConfigJsonContext.cs — AOT-safe JSON metadata for Harbor configuration
// persistence.
//
// System.Text.Json's REFLECTION-based serializer is unavailable under
// NativeAOT (PublishAot) or when
// `JsonSerializerIsReflectionEnabledByDefault=false`. Before this context
// existed, JsonCommonConfigStore / JsonAppConfigStore called the reflection
// overloads (JsonSerializer.Deserialize<T>(json, options)), which crash or
// silently fall back to default values on AOT-published builds. Every
// config-persisting call now resolves its metadata from this
// source-generated JsonSerializerContext instead.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Harbor.Desktop.Abstractions.Configuration;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext" /> covering every
///     type persisted by the Harbor config stores. Use
///     <see cref="ConfigJson.CommonConfigInfo" /> (which layers the
///     immutable-collection converters on top of this context) rather than
///     the context directly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Options parity:</b> the generation-time options replicate the
///         semantics the stores previously got from
///         <see cref="JsonSerializerDefaults.Web" /> plus their manual
///         tweaks — camelCase property names, case-insensitive matching on
///         read, indented output, and nulls omitted when writing — so
///         existing config files keep round-tripping unchanged.
///     </para>
///     <para>
///         <b>Immutable collections:</b> <see cref="CommonConfig" /> carries
///         <see cref="ImmutableList{T}" /> /
///         <see cref="ImmutableDictionary{TKey, TValue}" /> properties which
///         have no built-in System.Text.Json support. They are served by the
///         hand-written converters registered in
///         <see cref="ConfigJson.Options" /> (options-level converters take
///         precedence during property resolution). They are deliberately NOT
///         registered here — the generator cannot synthesize a valid contract
///         for constructor-less immutable collections.
///     </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommonConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>
///     Shared, AOT-safe <see cref="JsonSerializerOptions" /> and pre-resolved
///     metadata for the config stores. Single wiring point so both
///     <see cref="JsonCommonConfigStore" /> and
///     <see cref="JsonAppConfigStore{T}" /> stay consistent.
/// </summary>
internal static class ConfigJson
{
    /// <summary>
    ///     Options seeded from the source-generated
    ///     <see cref="ConfigJsonContext" /> (so NativeAOT never falls back to
    ///     reflection), with the immutable-collection converters layered on
    ///     top. Options-level converters win during property resolution, so
    ///     the <see cref="ImmutableList{T}" /> /
    ///     <see cref="ImmutableDictionary{TKey, TValue}" /> properties of
    ///     <see cref="CommonConfig" /> route to them while everything else
    ///     keeps using the generated metadata.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(ConfigJsonContext.Default.Options)
    {
        Converters =
        {
            ImmutableListConverter<string>.Instance,
            ImmutableDictionaryConverter<string, string>.Instance
        }
    };

    /// <summary>
    ///     Pre-resolved, AOT-safe metadata for <see cref="CommonConfig" />.
    ///     Pass to <see cref="JsonSerializer.Deserialize{T}(string, JsonTypeInfo{T})" />
    ///     / <see cref="JsonSerializer.SerializeToUtf8Bytes{T}(T, JsonTypeInfo{T})" />
    ///     — never to the reflection-based generic-options overloads.
    /// </summary>
    public static JsonTypeInfo<CommonConfig> CommonConfigInfo { get; } =
        (JsonTypeInfo<CommonConfig>)Options.GetTypeInfo(typeof(CommonConfig));
}
