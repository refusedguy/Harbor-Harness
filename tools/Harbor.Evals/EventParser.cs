using System.Text.Json;

namespace Harbor.Evals;

/// <summary>Parses [bracket] markers from plain-renderer stdout into events.jsonl.</summary>
internal static class EventParser
{
    public sealed record ParsedEvents(List<string> JsonLines, List<string> Unknown, bool SawAgentEnd);

    /// <summary>One events.jsonl line. Named record instead of the previous
    /// anonymous shape — source-gen cannot speak compiler-generated types; the
    /// JSON keys ("kind", "text") and order are identical.</summary>
    public sealed record ParseEvent(string kind, string text);

    public static ParsedEvents Parse(string stdout)
    {
        var lines = new List<string>();
        var unknown = new List<string>();
        bool end = false;
        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('['))
                continue;
            int close = line.IndexOf(']');
            if (close < 2)
            {
                unknown.Add(line);
                continue;
            }

            string kind = line[1..close];
            string rest = line[(close + 1)..].Trim();
            if (kind == "agent_end")
                end = true;
            lines.Add(JsonSerializer.Serialize(new ParseEvent(kind, rest), EventJsonContext.Default.ParseEvent));
        }

        return new ParsedEvents(lines, unknown, end);
    }
}
