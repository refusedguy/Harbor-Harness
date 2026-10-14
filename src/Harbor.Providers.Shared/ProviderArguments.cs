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
/// <remarks>
///     The scratch buffer below is amortized, not just pooled: a fresh
///     <c>ArrayBufferWriter</c> per call re-rents its growth chain on every
///     large payload (1 K → 2 K → 4 K …), which costs more than the string
///     it replaces — the first version of this helper proved exactly that
///     in CI (whole-build slope 4.15 B/char vs the 3.0 gate). Retaining one
///     buffer per thread at the high-water mark makes the steady-state
///     transcode allocation-free.
/// </remarks>
internal static class ProviderArguments
{
    /// <summary>
    ///     Scratch transcode buffer, retained per thread at the high-water
    ///     mark of the largest arguments payload seen on that thread.
    /// </summary>
    /// <remarks>
    ///     Thread-confinement is total because request-build is fully
    ///     synchronous — no await sits between borrow and release, so the
    ///     buffer can never observe a thread switch. Correctness never
    ///     depends on reuse: the worst case is a fresh buffer, i.e. the
    ///     pre-amortization cost, never wrong bytes. One buffer per thread
    ///     per linked copy (OpenAI, OpenAiCompatible).
    /// </remarks>
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_scratch;

    /// <summary>
    ///     Write an <c>arguments</c> element as a JSON string without the
    ///     per-tool-call UTF-16 round-trip and without per-call buffer
    ///     growth.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The old shape at both call sites was
    ///         <c>WriteString("arguments", args.GetRawText())</c>:
    ///         <c>GetRawText()</c> decodes the element's UTF-8 segment into
    ///         a UTF-16 <c>string</c> (one allocation per tool call, scaling
    ///         with the args size), and the writer then re-encodes that
    ///         string into escaped UTF-8 — double-encode by string. Here the
    ///         element transcodes straight into the thread-local scratch
    ///         buffer and the buffer is escaped into the payload in one
    ///         pass: the steady-state transcode allocates nothing, however
    ///         large the args.
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

        ArrayBufferWriter<byte> buffer = t_scratch ??= new ArrayBufferWriter<byte>();
        buffer.Clear();

        using (var tmp = new Utf8JsonWriter(buffer))
        {
            arguments.WriteTo(tmp);
        }

        writer.WriteStringValue(buffer.WrittenSpan);
    }
}
