// Shared source: compiled INTO each Harbor.Providers.* assembly via
// <Compile Include> link items (ROP-A ПР.1). The architecture matrix forbids
// Infrastructure→Infrastructure project references, so the single-source
// helpers travel as a linked file instead of a shared assembly. One source of
// truth, four identical internal copies — no cross-provider coupling.

using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Harbor.Providers.Internal;

/// <summary>
///     Tolerant <see cref="Utf8JsonReader" /> helpers shared by every wire
///     parser (#1563). Some servers send counts as floats (<c>7.0</c>) or
///     strings (<c>"7"</c>); these never throw — a single fix here reaches
///     all providers instead of drifting per file.
/// </summary>
internal static class Utf8JsonReaderExtensions
{
    internal static void SkipContainer(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            reader.Skip();
    }

    /// <summary>
    ///     Best-effort integer read: some servers send counts as floats
    ///     (<c>7.0</c>) or strings (<c>"7"</c>). Never throws — returns 0
    ///     when the value is missing or unparseable (DOM parity).
    /// </summary>
    internal static int ReadTolerantInt(ref Utf8JsonReader reader) =>
        ReadTolerant(ref reader).Match(static v => v, static () => 0);

    /// <summary>
    ///     The name of the property the reader is on, WITHOUT the throwaway
    ///     string the old path allocated for every property of every chunk.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A chunk carries a handful of property names, and the parsers
    ///         only ever COMPARED them against literals (<c>"choices"</c>,
    ///         <c>"usage"</c>, …). The old path turned each name into a fresh
    ///         <see cref="string" /> first — one allocation per property per
    ///         chunk, on the hottest line of the whole product: every token of
    ///         every answer arrives as a chunk, and a chunk has 6–14
    ///         properties.
    ///     </para>
    ///     <para>
    ///         <paramref name="known" /> is the literal the caller's own
    ///         span table matched — an interned string, so nothing is
    ///         allocated. When it is null the name is not one the caller
    ///         knows, and the only remaining way it can still be a known name
    ///         is an ESCAPE sequence (<c>"cho\u0069ces"</c>) or a non-ASCII
    ///         byte: in both cases the raw bytes are not the name's text, and
    ///         only the reader can say what it is. That is the old path, and
    ///         it is the rare one — no provider writes a name that way.
    ///     </para>
    ///     <para>
    ///         The name a caller does not know is returned as
    ///         <see cref="string.Empty" />, exactly as the old path's
    ///         <c>GetString() ?? string.Empty</c> returned an empty string for
    ///         a name it could not read. Every parser only ever COMPARES the
    ///         name, so an unknown name and an empty one take the same branch.
    ///     </para>
    /// </remarks>
    /// <param name="reader">The reader, positioned on the property name.</param>
    /// <param name="known">
    ///     The interned literal the caller's span table matched, or null when
    ///     the name is not one it knows.
    /// </param>
    internal static string PropertyName(ref Utf8JsonReader reader, string? known)
    {
        if (known is not null)
        {
            return known;
        }

        // Not one of the names the caller knows — and the raw bytes are not
        // the name's text either, so the reader has to say what it is.
        return NeedsTextDecoding(reader.ValueSpan)
            ? reader.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    ///     Whether the raw bytes of a property name are not the name's text:
    ///     an escape sequence (the bytes still carry it) or a non-ASCII byte
    ///     (the text has to be decoded, and decoding is where an invalid
    ///     UTF-8 byte becomes an error).
    /// </summary>
    /// <remarks>
    ///     A name that is plain ASCII and carries no escape IS its own text,
    ///     so it can be matched against a literal without decoding it — and
    ///     the comparison is then the same comparison the old path made,
    ///     byte for byte.
    /// </remarks>
    internal static bool NeedsTextDecoding(ReadOnlySpan<byte> rawName)
    {
        foreach (byte b in rawName)
        {
            if (b == (byte)'\\' || b >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Nullable variant of <see cref="ReadTolerantInt" />: returns
    ///     <c>null</c> instead of 0 when the value is missing or unparseable
    ///     (cache-token counters that must stay absent, not zero).
    /// </summary>
    internal static int? ReadTolerantNullableInt(ref Utf8JsonReader reader) =>
        ReadTolerant(ref reader).Match(static v => (int?)v, static () => (int?)null);

    private static Maybe<int> ReadTolerant(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out int direct))
                return direct;
            if (reader.TryGetDouble(out double dbl))
                return (int)dbl;
            return Maybe<int>.None;
        }

        if (reader.TokenType == JsonTokenType.String &&
            int.TryParse(reader.GetString(), out int parsed))
        {
            return parsed;
        }

        return Maybe<int>.None;
    }
}
