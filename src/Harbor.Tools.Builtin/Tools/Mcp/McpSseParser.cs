using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Harbor.Tools.Mcp;

/// <summary>A single Server-Sent-Events frame parsed from a line stream.</summary>
internal readonly record struct SseEvent(string Event, string Data);

/// <summary>
///     Minimal incremental parser for <c>text/event-stream</c>: feed lines as they
///     arrive; a blank line completes the event. Handles the <c>event:</c> field and
///     multi-line <c>data:</c> payloads; <c>id:</c>, <c>retry:</c> and comment
///     (keep-alive) lines are ignored. Shared by the MCP HTTP and SSE transports.
/// </summary>
internal sealed class SseEventReader
{
    private readonly List<string> _data = [];
    private string _eventName = "message";

    /// <summary>Feed one raw line (without its terminator). Returns the completed event, or null when the line does not close one.</summary>
    public SseEvent? Feed(string line)
    {
        line = line.TrimEnd('\r');
        if (line.Length == 0)
        {
            if (_data.Count == 0)
            {
                return null;
            }

            SseEvent completed = new(_eventName, string.Join("\n", _data));
            _data.Clear();
            _eventName = "message";
            return completed;
        }

        if (line[0] == ':')
        {
            return null; // SSE comment — keep-alive frames carry no payload
        }

        int colon = line.IndexOf(':');
        string field = colon < 0 ? line : line[..colon];
        string value = colon < 0
            ? string.Empty
            : colon + 1 < line.Length && line[colon + 1] == ' '
                ? line[(colon + 2)..]
                : line[(colon + 1)..];

        if (FieldHandlers.TryGetValue(field, out var handle))
        {
            handle(this, value);
        }
        // "id" / "retry" / unknown fields are ignored per the SSE spec

        return null;
    }

    /// <summary>
    ///     Field-handler map (#197): a new SSE field adds one row here, never
    ///     an edit to <see cref="Feed" />. Unknown fields stay ignored.
    /// </summary>
    private static readonly FrozenDictionary<string, Action<SseEventReader, string>> FieldHandlers =
        new Dictionary<string, Action<SseEventReader, string>>(StringComparer.Ordinal)
        {
            ["event"] = static (r, v) => r._eventName = string.IsNullOrEmpty(v) ? "message" : v,
            ["data"] = static (r, v) => r._data.Add(v),
        }.ToFrozenDictionary(StringComparer.Ordinal);
}

/// <summary>JSON-RPC helpers over SSE payloads, shared by the MCP remote transports.</summary>
internal static class McpSse
{
    /// <summary>
    ///     Parse an SSE <c>data</c> payload as JSON-RPC and return it (caller disposes)
    ///     when it answers the request with <paramref name="expectedId" />. Returns null
    ///     for non-JSON frames (keep-alives) and for responses belonging to other
    ///     in-flight requests.
    /// </summary>
    public static JsonDocument? TryParseResponse(string data, int? expectedId)
    {
        if (expectedId is not { } id)
        {
            try
            {
                return JsonDocument.Parse(data);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Filtered path (#180): encode once into a pooled buffer, scan the
        // envelope for the top-level "id" with Utf8JsonReader (no DOM), and
        // only then parse the same bytes. Non-JSON frames and id mismatches
        // return null without ever materializing a document (previously:
        // full parse + dispose, plus an exception on malformed frames).
        int byteCount = Encoding.UTF8.GetByteCount(data);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Encoding.UTF8.GetBytes(data, rented);
            ReadOnlySpan<byte> utf8 = rented.AsSpan(0, byteCount);
            if (!IdMatches(utf8, id))
                return null;
            try
            {
                return JsonDocument.Parse(rented.AsMemory(0, byteCount));
            }
            catch (JsonException)
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Scan a UTF-8 JSON-RPC frame for its top-level <c>"id"</c> without
    /// building a DOM. Mirrors the old <c>TryGetProperty("id") + Number +
    /// TryGetInt32</c> check exactly, including last-wins on duplicate keys
    /// and rejection of trailing garbage; malformed input scans as no-match
    /// (the old path mapped both to null as well).
    /// </summary>
    private static bool IdMatches(ReadOnlySpan<byte> utf8, int expectedId)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            bool closed = false;
            bool hasNumericId = false;
            int lastId = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    closed = true;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                bool isId = reader.ValueSpan.SequenceEqual("id"u8);
                if (!reader.Read())
                    return false; // truncated frame
                if (!isId)
                {
                    reader.Skip();
                    continue;
                }

                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                {
                    hasNumericId = true;
                    lastId = value;
                }
                else
                {
                    // String/null/bool/container "id" never matched the old
                    // Number+TryGetInt32 check; keep scanning so a later
                    // duplicate still decides (last-wins, like TryGetProperty).
                    hasNumericId = false;
                    reader.Skip();
                }
            }

            // A closed root object whose trailing bytes hold another token is
            // malformed (the old Parse threw) — not a match.
            return closed && hasNumericId && lastId == expectedId && !reader.Read();
        }
        catch (Exception)
        {
            // Best-effort fast path: anything the scanner does not understand
            // (malformed frames, novel token shapes) scans as no-match — the
            // old path mapped every one of those to null as well.
            return false;
        }
    }
}
