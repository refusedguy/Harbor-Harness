// RemoteJsonContext.cs — AOT-compatible JsonSerializerContext for the remote
// transport wire format. RemoteClient.SendAsync serializes one
// UiTransportPacket per packet on a hot path; the reflection-based
// JsonSerializer.Serialize(packet) overload pays per-call metadata lookup
// and breaks NativeAOT (IL2026). The source-generated type info below
// serializes straight to UTF-8 bytes with zero reflection.

using System.Text.Json.Serialization;

namespace Harbor.Transport.Remote;

/// <summary>
///     Source-generated JSON metadata for <see cref="UiTransportPacket" />.
///     Public so transport round-trip tests reuse the exact serializer the
///     client sends with — no second encoding exists.
/// </summary>
/// <remarks>
///     The polymorphic <c>AgentEvent</c> payload rides the
///     <c>JsonPolymorphic</c>/<c>JsonDerivedType</c> attributes declared on
///     the event hierarchy; the generator follows them transitively, so one
///     root registration covers the whole event graph. Options stay default
///     (PascalCase), matching the previous reflection-based wire shape.
/// </remarks>
[JsonSerializable(typeof(UiTransportPacket))]
public sealed partial class RemoteJsonContext : JsonSerializerContext
{
}
