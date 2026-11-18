using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Converts an existing CLI agent's JSONL history into Harbor's own
///     <see cref="AgentMessage" /> shape (#2674 F13). The CLI verb owns the file
///     read, the new session and the append, so the format mapping stays a pure
///     function of (source, text, session id) and is cheap to test.
/// </summary>
/// <remarks>
///     Only the conversational spine is imported: user prompts, assistant text
///     and tool calls. Provider bookkeeping (tokens, stop reasons, image parts)
///     is not recoverable from either foreign format, so a default usage and a
///     normal stop reason are stamped on assistant messages. Non-conversational
///     records (summaries, session metadata, event markers) are counted as
///     skipped rather than failing the whole import.
/// </remarks>
internal static class ForeignSessionImport
{
    /// <summary>Source id for Claude Code JSONL session files.</summary>
    internal const string Claude = "claude";

    /// <summary>Source id for Codex JSONL rollout files.</summary>
    internal const string Codex = "codex";

    /// <summary>The conversation projected out of a foreign JSONL file.</summary>
    /// <param name="Messages">Harbor messages in file order.</param>
    /// <param name="Imported">Number of messages projected.</param>
    /// <param name="Skipped">Number of lines that carried no conversation.</param>
    internal sealed record ForeignImportResult(
        IReadOnlyList<AgentMessage> Messages,
        int Imported,
        int Skipped);

    /// <summary>Parse one foreign JSONL document into Harbor messages.</summary>
    /// <param name="source">Either <see cref="Claude" /> or <see cref="Codex" />.</param>
    /// <param name="jsonl">The full file contents.</param>
    /// <param name="sessionId">The Harbor session id to stamp on every message.</param>
    /// <returns>The projected conversation, or a failure for an unknown source.</returns>
    internal static Result<ForeignImportResult> Parse(string source, string jsonl, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Result.Failure<ForeignImportResult>("import: a session id is required.");
        if (source == Claude)
            return Result.Success(ParseClaude(jsonl, sessionId));
        if (source == Codex)
            return Result.Success(ParseCodex(jsonl, sessionId));
        return Result.Failure<ForeignImportResult>(
            $"import: unknown source '{source}'. Expected 'claude' or 'codex'.");
    }

    private static ForeignImportResult ParseClaude(string jsonl, string sessionId)
    {
        var messages = new List<AgentMessage>();
        int skipped = 0;
        int index = 0;

        foreach (string raw in jsonl.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out JsonElement typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    skipped++;
                    continue;
                }

                string type = typeElement.GetString() ?? string.Empty;
                JsonElement content = GetNested(root, "message", "content");
                DateTimeOffset timestamp = ParseTimestamp(root);
                string model = GetNestedString(root, "message", "model") ?? Claude;

                if (type == "user")
                {
                    string text = ExtractText(content);
                    if (text.Length == 0)
                    {
                        skipped++;
                        continue;
                    }

                    messages.Add(new UserMessage(
                        MessageId(Claude, index++), sessionId, timestamp, text, "code", model));
                }
                else if (type == "assistant")
                {
                    var parts = new List<ContentPart>();
                    bool toolUse = ExtractClaudeAssistantParts(content, parts);
                    if (parts.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    messages.Add(new AssistantMessage(
                        MessageId(Claude, index++), sessionId, timestamp, parts,
                        toolUse ? StopReason.ToolUse : StopReason.Stop,
                        ParseUsage(GetNested(root, "message", "usage")), model));
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new ForeignImportResult(messages, messages.Count, skipped);
    }

    private static bool ExtractClaudeAssistantParts(JsonElement content, List<ContentPart> parts)
    {
        bool toolUse = false;
        if (content.ValueKind == JsonValueKind.String)
        {
            string text = content.GetString() ?? string.Empty;
            if (text.Length > 0)
                parts.Add(new TextPart(text));
            return toolUse;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return toolUse;

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;

            string blockType = GetString(block, "type") ?? string.Empty;
            if (blockType == "text" || blockType == "output_text")
            {
                string text = GetString(block, "text") ?? string.Empty;
                if (text.Length > 0)
                    parts.Add(new TextPart(text));
            }
            else if (blockType == "thinking")
            {
                string text = GetString(block, "thinking") ?? GetString(block, "text") ?? string.Empty;
                if (text.Length > 0)
                    parts.Add(new ThinkingPart(text));
            }
            else if (blockType == "tool_use")
            {
                string id = GetString(block, "id") ?? MessageId("tool", parts.Count);
                string name = GetString(block, "name") ?? "unknown";
                parts.Add(ToolCallPart.Create(id, name, GetPropertyOrEmptyObject(block, "input")));
                toolUse = true;
            }
        }

        return toolUse;
    }

    private static ForeignImportResult ParseCodex(string jsonl, string sessionId)
    {
        var messages = new List<AgentMessage>();
        int skipped = 0;
        int index = 0;

        foreach (string raw in jsonl.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                string type = GetString(root, "type") ?? string.Empty;
                JsonElement payload = GetProperty(root, "payload");
                DateTimeOffset timestamp = ParseTimestamp(root);

                if (type == "response_item" && (GetString(payload, "type") ?? string.Empty) == "message")
                {
                    string role = GetString(payload, "role") ?? string.Empty;
                    string text = ExtractText(GetProperty(payload, "content"));
                    if (text.Length == 0)
                    {
                        skipped++;
                        continue;
                    }

                    if (role == "user")
                    {
                        messages.Add(new UserMessage(
                            MessageId(Codex, index++), sessionId, timestamp, text, "code", Codex));
                    }
                    else if (role == "assistant")
                    {
                        messages.Add(new AssistantMessage(
                            MessageId(Codex, index++), sessionId, timestamp, [new TextPart(text)],
                            StopReason.Stop, new Usage(0, 0), Codex));
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else if (type == "event_msg" && (GetString(payload, "type") ?? string.Empty) == "user_message")
                {
                    string text = GetString(payload, "message") ?? string.Empty;
                    if (text.Length == 0)
                    {
                        skipped++;
                        continue;
                    }

                    messages.Add(new UserMessage(
                        MessageId(Codex, index++), sessionId, timestamp, text, "code", Codex));
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new ForeignImportResult(messages, messages.Count, skipped);
    }

    /// <summary>
    ///     Read conversational text from a Claude string or a content-block
    ///     array. Claude uses <c>text</c>, Codex uses <c>input_text</c> /
    ///     <c>output_text</c>; tool results may carry the text in <c>content</c>.
    /// </summary>
    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var text = new StringBuilder();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;

            string blockType = GetString(block, "type") ?? string.Empty;
            if (blockType is not ("" or "text" or "input_text" or "output_text" or "tool_result"))
                continue;

            string value = GetString(block, "text") ?? GetString(block, "content") ?? string.Empty;
            if (value.Length == 0)
                continue;
            if (text.Length > 0)
                text.Append('\n');
            text.Append(value);
        }

        return text.ToString();
    }

    private static Usage ParseUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return new Usage(0, 0);
        return new Usage(GetInt(usage, "input_tokens"), GetInt(usage, "output_tokens"));
    }

    private static DateTimeOffset ParseTimestamp(JsonElement root)
    {
        string? stamp = GetString(root, "timestamp");
        if (stamp is not null
            && DateTimeOffset.TryParse(
                stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private static string MessageId(string prefix, int index) =>
        $"{prefix}-{index.ToString(CultureInfo.InvariantCulture)}";

    private static JsonElement GetNested(JsonElement root, string parent, string name) =>
        root.TryGetProperty(parent, out JsonElement p) && p.ValueKind == JsonValueKind.Object
            ? GetProperty(p, name)
            : default;

    private static string? GetNestedString(JsonElement root, string parent, string name) =>
        root.TryGetProperty(parent, out JsonElement p) && p.ValueKind == JsonValueKind.Object
            ? GetString(p, name)
            : null;

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    private static string? GetString(JsonElement element, string name)
    {
        JsonElement value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int GetInt(JsonElement element, string name)
    {
        JsonElement value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : 0;
    }

    private static JsonElement GetPropertyOrEmptyObject(JsonElement element, string name)
    {
        JsonElement value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.Object)
            return value;

        using JsonDocument empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }
}
