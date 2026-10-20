// DomainModelsJsonContext.cs — the source-generated context for the domain
// models this project round-trips (#1822).
//
// WHY IT IS PROJECT-LOCAL
// -----------------------
// `PolymorphicRoundTripTests` serializes `AgentMessage`, `ContentPart` and
// `ToolChoice` — the polymorphic roots of `Harbor.Abstractions.Contracts`.
// Two things keep the context here rather than in the shared kit:
//
//   1. The shared kit (`Harbor.TestKit.Support`) is closure-neutral by
//      construction: it reaches `Harbor.Abstractions` and nothing else.
//      These three types live in `Harbor.Abstractions.Contracts`, and this
//      project already references that assembly directly — so a context here
//      adds NO edge at all.
//   2. `Harbor.Domain.Tests` is mid-flight in the TestKit split (#1889),
//      which adds a `Harbor.TestKit` reference to this project's csproj.
//      Touching that file from here would collide with that change for no
//      reason.
//
// THE OPTIONS ARE `JsonSerializerDefaults.Web`, SPELLED OUT
// ---------------------------------------------------------
// The test serialized through `new JsonSerializerDefaults.Web`, which is
// camelCase + case-insensitive + "a number may arrive as a string". The
// attribute below spells those three out, because a context that forgot one
// of them would silently change what the test proves:
//
//   * `PropertyNamingPolicy = CamelCase` — `AgentMessage.Id` is written
//     `id`, exactly as the reflection path wrote it.
//   * `PropertyNameCaseInsensitive = true` — a payload that says `Id` still
//     binds to `Id`.
//   * `NumberHandling = AllowReadingFromString` — a numeric member may
//     arrive quoted.
//
// The polymorphic roots carry `[JsonPolymorphic]` + `[JsonDerivedType]`, and
// the source generator builds the discriminator handling from those
// attributes — the same attributes the reflection path read at run time.

using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;

namespace Harbor.Domain.Tests;

/// <summary>
///     Source-generated metadata for the domain message models this project
///     round-trips.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentMessage), TypeInfoPropertyName = "AgentMessage")]
[JsonSerializable(typeof(ContentPart), TypeInfoPropertyName = "ContentPart")]
[JsonSerializable(typeof(ToolChoice), TypeInfoPropertyName = "ToolChoice")]
internal sealed partial class DomainModelsJsonContext : JsonSerializerContext
{
}
