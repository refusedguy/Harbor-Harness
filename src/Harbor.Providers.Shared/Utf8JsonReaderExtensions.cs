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
