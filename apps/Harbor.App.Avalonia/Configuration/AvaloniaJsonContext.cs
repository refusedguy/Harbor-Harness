// AvaloniaJsonContext.cs — source-generated JSON metadata for AvaloniaConfig
// (#414).
//
// The Avalonia counterpart of `apps/Harbor.App.Cli/Configuration/CliJsonContext.cs`,
// and for the same reason. `ConfigRegistration` built
// `JsonAppConfigStore<AvaloniaConfig>` WITHOUT the optional `JsonTypeInfo<T>`, so
// ~/.harbor/avalonia.json was read and written through
// `JsonSerializer.Deserialize<T>(json, JsonOptions)` — a reflective contract that
// `JsonSerializer.IsReflectionEnabledByDefault` disables under `PublishTrimmed`.
// Both construction sites in that file hit the same fallback, so this one
// context closes both.
//
// `ConfigJsonContext` (Harbor.Hosting) cannot serve this: it is `internal` by
// design after #534, and it covers `CommonConfig` only. `AvaloniaConfig` is
// app-local, so its context is app-local too — the arrangement the
// `jsonTypeInfo` parameter's documentation already described.
//
// OPTIONS PARITY
// -------------
// Same four settings as `ConfigJsonContext` and as the reflection path's
// `JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true,
// DefaultIgnoreCondition = WhenWritingNull }`. camelCase + case-insensitive is
// what `JsonSerializerDefaults.Web` contributes; dropping either would stop an
// existing ~/.harbor/avalonia.json round-tripping, which is a silent config
// regression rather than a visible failure.
//
// `ImmutableList<string>` members need no converter: `IImmutableList<T>` is
// listed as serializing AND deserializing in the supported-types table, and its
// wire shape is the same JSON array the hand-written
// `ImmutableListConverter<T>` in JsonAppConfigStore writes. The on-disk file is
// unchanged, and that converter stays registered on the reflection path it
// belongs to.

using System.Text.Json.Serialization;

namespace Harbor.App.Avalonia.Configuration;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext" /> for
///     <see cref="AvaloniaConfig" />, passed to
///     <see cref="Harbor.Hosting.Configuration.JsonAppConfigStore{T}" /> so
///     ~/.harbor/avalonia.json is read and written without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AvaloniaConfig))]
internal sealed partial class AvaloniaJsonContext : JsonSerializerContext;