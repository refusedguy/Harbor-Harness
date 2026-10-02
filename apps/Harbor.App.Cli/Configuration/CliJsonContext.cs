// CliJsonContext.cs — source-generated JSON metadata for CliConfig (#414).
//
// WHY THIS FILE EXISTS
// --------------------
// `JsonAppConfigStore<T>` takes an optional `JsonTypeInfo<T>` and, when it is
// absent, falls back to `JsonSerializer.Deserialize<T>(json, JsonOptions)` —
// which resolves the contract BY REFLECTION at run time. Under `PublishTrimmed`
// reflection-based serialization is off by default
// (`JsonSerializer.IsReflectionEnabledByDefault`), so that fallback is an
// `InvalidOperationException` on the first read of ~/.harbor/cli.json rather
// than a slow path.
//
// `HostBuilder.AddCliConfiguration` did not pass the argument, so the fallback
// was not a dead branch: it was the path every CLI run took, and the store's
// own startup warning about it fired on every launch. The fallback is still
// there for callers that genuinely have no generated contract, but the CLI's
// own config now supplies one, so the reflection branch is unreachable from
// the composition root.
//
// `ConfigJsonContext` in Harbor.Hosting cannot be used for this: it is
// `internal` (deliberately — #534 moved it and kept it internal so no public
// type is added to a package), and it covers `CommonConfig`, an assembly it
// does not reference. `CliConfig` is app-local, so the context is app-local too
// — which is the arrangement the `jsonTypeInfo` parameter's documentation
// already described ("from an app-local JsonSerializerContext").
//
// OPTIONS PARITY, and why it is not optional
// ------------------------------------------
// The generated options replicate what the reflection path's
// `JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true,
// DefaultIgnoreCondition = WhenWritingNull }` produced, and are the same four
// settings `ConfigJsonContext` declares. camelCase + case-insensitive is what
// `JsonSerializerDefaults.Web` contributes; without both, an existing
// ~/.harbor/cli.json stops round-tripping — a silent config regression, which
// is a worse failure than the one being fixed.
//
// `ImmutableList<string> DisabledTools` needs no converter here. The comment in
// `JsonAppConfigStore` saying it has "no built-in converter" predates
// System.Text.Json's immutable-collection support; `IImmutableList<T>` is
// listed as serializing AND deserializing in the supported-types table, and
// its wire shape is the same JSON array the hand-written
// `ImmutableListConverter<T>` wrote, so the on-disk file is unchanged. The
// converter stays registered on the reflection path it belongs to.

using System.Text.Json.Serialization;

namespace Harbor.App.Cli.Configuration;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext" /> for
///     <see cref="CliConfig" />, passed to
///     <see cref="Harbor.Hosting.Configuration.JsonAppConfigStore{T}" /> so
///     ~/.harbor/cli.json is read and written without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
internal sealed partial class CliJsonContext : JsonSerializerContext;