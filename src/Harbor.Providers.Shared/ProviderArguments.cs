// Shared source: compiled INTO each OpenAI-format provider assembly via
// <Compile Include> link items (ROP-A ПР.1). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the single-source
// helper travels as a linked file instead of a shared assembly — same shape
// as SsePump/OpenAiWire/OpenAiImageContent, linked into OpenAI +
// OpenAiCompatible only (Anthropic/Ollama have their own wire types).

using System.Buffers;
using System.Text.Json;

namespace Harbor.Providers.Internal;

/// <summary>
///     Tool-call <c>arguments</c> writer for the OpenAI-format request
///     builders (#1102).
/// </summary>
internal static class ProviderArguments
{
    /// <summary>
    ///     Write an <c>arguments</c> element as a JSON string without the
    ///     per-tool-call UTF-16 round-trip.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The old shape at both call sites was
    ///         <c>WriteString("arguments", args.GetRawText())</c>:
    ///         <c>GetRawText()</c> decodes the element's UTF-8 segment into
    ///         a UTF-16 <c>string</c> (one allocation per tool call), and the
    ///         writer then re-encodes that string into escaped UTF-8 —
    ///         double-encode by string. Here the element transcodes straight
    ///         into a pooled UTF-8 buffer (<c>ArrayBufferWriter</c> rents
    ///         from the pool, so a large args payload stays off the LOH)
    ///         and the buffer is escaped into the payload in one pass — a
    ///         single copy, no intermediate string.
    ///     </para>
    ///     <para>
    ///         Parity: the transcode re-serializes canonically — the same
    ///         documented parity the Ollama chunk parser claims for the
    ///         object-arguments case. Byte-identical to <c>GetRawText()</c>
    ///         for the compact canonical args the models emit; a
    ///         non-canonical raw form (whitespace, <c>\/</c>, upper-case
    ///         <c>\u</c> hex) normalizes to the same JSON value. The
    ///         provider parses the string as JSON either way, so the tool
    ///         sees identical arguments.
    ///     </para>
    /// </remarks>
    public static void WriteArgumentsString(Utf8JsonWriter writer, string propertyName, JsonElement arguments)
    {
        writer.WritePropertyName(propertyName);

        var buffer = new ArrayBufferWriter<byte>();
        using (var tmp = new Utf8JsonWriter(buffer))
        {
            arguments.WriteTo(tmp);
        }

        writer.WriteStringValue(buffer.WrittenSpan);
    }
}
