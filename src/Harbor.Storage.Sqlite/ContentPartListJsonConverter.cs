// ContentPartListJsonConverter.cs — property-level converter for AssistantMessage.Parts (#177).
//
// Why this exists: Parts is declared IReadOnlyList<ContentPart> and ContentPart carries
// [JsonPolymorphic]/[JsonDerivedType]. When the source-generated AssistantMessage metadata
// is configured, STJ builds a PolymorphicTypeResolver for the element type; its
// derived-type converter lookup hits the options-level ContentPartJsonConverter, which
// does not support metadata reads/writes → NotSupportedException at first serialization
// (CI, all OSes — same root on every platform). An exact-match converter for the property
// type takes precedence, so element type info is never requested and the resolver is
// never built. Elements keep the legacy hand-written shapes (no $type leak) and reads
// stay legacy-tolerant (PascalCase aliases) through the same per-element converter.
//
// The [JsonPolymorphic] attributes themselves are left untouched: the transport
// (RemoteJsonContext) relies on the same attribute style for the AgentEvent hierarchy
// and has no ContentPart references, so there is no blast radius beyond this store.

using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;

namespace Harbor.Storage.Sqlite;

/// <summary>
///     Exact-match <see cref="JsonConverter{T}" /> for message part lists. Bypasses
///     polymorphic element resolution (see file header) while delegating every element
///     to <see cref="SqliteMappers.ContentPartJsonConverter" /> — byte-identical to
///     the former reflection path on write, legacy-tolerant on read.
/// </summary>
internal sealed class ContentPartListJsonConverter : JsonConverter<IReadOnlyList<ContentPart>>
{
    internal static readonly ContentPartListJsonConverter Instance = new();

    /// <inheritdoc />
    public override IReadOnlyList<ContentPart>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for message parts, got {reader.TokenType}.");

        var parts = new List<ContentPart>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return parts;

            // #550: an element that does not decode is no longer skipped. This
            // converter is the LAST place a lost part could be hidden, and skipping
            // here is what turned "this build cannot read that part" into a silently
            // shorter assistant turn reloaded from the row. The per-element converter
            // now refuses a part it cannot rebuild, naming the tag; the check below is
            // only the belt to those braces, so a null here can never again become a
            // missing part.
            ContentPart? part = SqliteMappers.ContentPartJsonConverter.Instance.Read(ref reader, typeof(ContentPart), options);
            parts.Add(part ?? throw new JsonException("A message part entry did not decode and must not be skipped."));
        }

        throw new JsonException("Unterminated message parts array.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ContentPart> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
            SqliteMappers.ContentPartJsonConverter.Instance.Write(writer, value[i], options);
        writer.WriteEndArray();
    }
}
