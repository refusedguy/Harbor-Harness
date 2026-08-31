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

        switch (field)
        {
            case "event":
                _eventName = string.IsNullOrEmpty(value) ? "message" : value;
                break;
            case "data":
                _data.Add(value);
                break;
            // "id" / "retry" / unknown fields are ignored per the SSE spec
        }

        return null;
    }
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
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }

        if (expectedId is not { } id)
        {
            return doc;
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("id", out JsonElement idEl)
            && idEl.ValueKind == JsonValueKind.Number
            && idEl.TryGetInt32(out int actual)
            && actual == id)
        {
            return doc;
        }

        doc.Dispose();
        return null;
    }
}
