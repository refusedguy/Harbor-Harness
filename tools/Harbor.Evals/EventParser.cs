using System.Text.Json;

namespace Harbor.Evals;

/// <summary>Parses [bracket] markers from plain-renderer stdout into events.jsonl.</summary>
internal static class EventParser
{
    public sealed record ParsedEvents(List<string> JsonLines, List<string> Unknown, bool SawAgentEnd);

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
            lines.Add(JsonSerializer.Serialize(new { kind, text = rest }));
        }

        return new ParsedEvents(lines, unknown, end);
    }
}
