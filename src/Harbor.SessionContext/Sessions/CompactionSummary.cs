using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Application.Sessions;

/// <summary>
///     A file touched during the compacted part of a session (goose-features #59,
///     port of <c>goose-context-management/src/structured.rs</c>'s
///     <c>FileActivity</c>).
/// </summary>
/// <param name="Path">Canonical path of the file.</param>
/// <param name="Summary">What happened to the file, in the model's words.</param>
/// <param name="KeyCode">Optional short snippet worth keeping verbatim.</param>
public sealed record FileActivity(string Path, string Summary, string? KeyCode = null);

/// <summary>
///     Structured output of the compaction LLM call (goose-features #59, port of
///     <c>goose-context-management/src/structured.rs</c>'s <c>StructuredSummary</c>).
/// </summary>
/// <remarks>
/// <para>
///     The ten content fields mirror goose exactly: <c>user_intent</c>,
///     <c>technical_concepts</c>, <c>files</c>, <c>errors_and_fixes</c>,
///     <c>problem_solving</c>, <c>user_messages</c>, <c>pending_tasks</c>,
///     <c>current_work</c>, <c>next_step</c> and <c>extra</c> (unknown fields the
///     model added). Every list is ordered most-important-first so a consumer can
///     cut from the tail.
/// </para>
/// <para>
///     Parsing is lenient and one-shot on a non-hot path. A field asked for as a
///     string is stringified when the model sends an object or an array instead,
///     unknown fields land in <see cref="Extra" />, and blank entries are dropped.
///     A response with no usable JSON object keeps the original text in
///     <see cref="RawText" /> and <see cref="Render" /> returns it verbatim, so a
///     model that ignores the schema can never lose its summary.
/// </para>
/// <para>
///     No reflection: the reader walks UTF-8 by hand and the AOT serializer is
///     generated through <see cref="CompactionSummaryJsonContext" />.
/// </para>
/// </remarks>
public sealed record CompactionSummary(
    IReadOnlyList<string> UserIntent,
    IReadOnlyList<string> TechnicalConcepts,
    IReadOnlyList<FileActivity> Files,
    IReadOnlyList<string> ErrorsAndFixes,
    IReadOnlyList<string> ProblemSolving,
    IReadOnlyList<string> UserMessages,
    IReadOnlyList<string> PendingTasks,
    string? CurrentWork,
    string? NextStep,
    IReadOnlyDictionary<string, JsonElement> Extra,
    string RawText,
    bool IsStructured)
{
    /// <summary>An empty, unstructured summary; used as the raw-text fallback seed.</summary>
    public static CompactionSummary Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<FileActivity>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null,
        new Dictionary<string, JsonElement>(),
        string.Empty,
        false);

    /// <summary>True when no content field survived normalization.</summary>
    public bool IsEmpty =>
        UserIntent.Count == 0
        && TechnicalConcepts.Count == 0
        && Files.Count == 0
        && ErrorsAndFixes.Count == 0
        && ProblemSolving.Count == 0
        && UserMessages.Count == 0
        && PendingTasks.Count == 0
        && CurrentWork is null
        && NextStep is null;

    /// <summary>
    ///     Parse a model response into a structured summary, falling back to the
    ///     raw text when no non-empty JSON object can be extracted from it.
    /// </summary>
    public static CompactionSummary Parse(string? raw)
    {
        string text = raw ?? string.Empty;
        if (ModelJsonExtractor.TryExtract(text, out string? json)
            && TryParseObject(json!, out ParsedFields? fields)
            && !fields.IsEmpty)
        {
            return new CompactionSummary(
                fields.UserIntent,
                fields.TechnicalConcepts,
                fields.Files,
                fields.ErrorsAndFixes,
                fields.ProblemSolving,
                fields.UserMessages,
                fields.PendingTasks,
                fields.CurrentWork,
                fields.NextStep,
                fields.Extra,
                text,
                true);
        }

        return Empty with { RawText = text };
    }

    /// <summary>
    ///     Render the summary for the next agent: structured sections when JSON
    ///     was parsed, otherwise the original response verbatim (lossless fallback).
    /// </summary>
    public string Render()
    {
        if (!IsStructured)
        {
            return RawText;
        }

        var text = new StringBuilder();
        AppendList(text, "## User intent", UserIntent);
        AppendList(text, "## Technical concepts", TechnicalConcepts);

        if (Files.Count != 0)
        {
            text.Append("## Files\n");
            for (int i = 0; i < Files.Count; i++)
            {
                FileActivity file = Files[i];
                text.Append("- `").Append(file.Path).Append('`');
                if (file.Summary.Length != 0) text.Append(" — ").Append(file.Summary);
                if (file.KeyCode is not null) text.Append(" (").Append(file.KeyCode).Append(')');
                text.Append('\n');
            }
            text.Append('\n');
        }

        AppendList(text, "## Errors and fixes", ErrorsAndFixes);
        AppendList(text, "## Problem solving", ProblemSolving);
        AppendList(text, "## User messages", UserMessages);
        AppendList(text, "## Pending tasks", PendingTasks);
        AppendScalar(text, "## Current work", CurrentWork);
        AppendScalar(text, "## Next step", NextStep);
        return text.ToString().TrimEnd();
    }

    /// <summary>AOT-safe write path for a structured summary.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(this, CompactionSummaryJsonContext.Default.CompactionSummary);

    private static void AppendList(StringBuilder text, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        text.Append(heading).Append('\n');
        for (int i = 0; i < items.Count; i++)
        {
            text.Append("- ").Append(items[i]).Append('\n');
        }
        text.Append('\n');
    }

    private static void AppendScalar(StringBuilder text, string heading, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        text.Append(heading).Append('\n').Append(value).Append("\n\n");
    }

    // ── Lenient Utf8JsonReader parsing ─────────────────────────────────────

    /// <summary>Mutable scrape target; normalized and frozen into the record on success.</summary>
    private sealed class ParsedFields
    {
        public List<string> UserIntent { get; } = new();
        public List<string> TechnicalConcepts { get; } = new();
        public List<FileActivity> Files { get; } = new();
        public List<string> ErrorsAndFixes { get; } = new();
        public List<string> ProblemSolving { get; } = new();
        public List<string> UserMessages { get; } = new();
        public List<string> PendingTasks { get; } = new();
        public string? CurrentWork { get; set; }
        public string? NextStep { get; set; }
        public Dictionary<string, JsonElement> Extra { get; } = new();

        public bool IsEmpty =>
            UserIntent.Count == 0
            && TechnicalConcepts.Count == 0
            && Files.Count == 0
            && ErrorsAndFixes.Count == 0
            && ProblemSolving.Count == 0
            && UserMessages.Count == 0
            && PendingTasks.Count == 0
            && CurrentWork is null
            && NextStep is null;

        public void Normalize()
        {
            DropBlanks(UserIntent);
            DropBlanks(TechnicalConcepts);
            DropBlanks(ErrorsAndFixes);
            DropBlanks(ProblemSolving);
            DropBlanks(UserMessages);
            DropBlanks(PendingTasks);

            for (int i = Files.Count - 1; i >= 0; i--)
            {
                FileActivity file = Files[i];
                if (file.Path.Trim().Length == 0 && file.Summary.Trim().Length == 0 && file.KeyCode is null)
                {
                    Files.RemoveAt(i);
                }
            }

            if (string.IsNullOrWhiteSpace(CurrentWork)) CurrentWork = null;
            if (string.IsNullOrWhiteSpace(NextStep)) NextStep = null;
        }

        private static void DropBlanks(List<string> items)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(items[i])) items.RemoveAt(i);
            }
        }
    }

    // A name → enum table keeps the dispatch data-driven instead of a
    // string-dispatched switch (the repo's check-string-switch gate).
    private enum SummaryField
    {
        UserIntent,
        TechnicalConcepts,
        Files,
        ErrorsAndFixes,
        ProblemSolving,
        UserMessages,
        PendingTasks,
        CurrentWork,
        NextStep,
        Extra
    }

    private static readonly Dictionary<string, SummaryField> FieldByName = new(StringComparer.Ordinal)
    {
        ["userintent"] = SummaryField.UserIntent,
        ["technicalconcepts"] = SummaryField.TechnicalConcepts,
        ["files"] = SummaryField.Files,
        ["errorsandfixes"] = SummaryField.ErrorsAndFixes,
        ["problemsolving"] = SummaryField.ProblemSolving,
        ["usermessages"] = SummaryField.UserMessages,
        ["pendingtasks"] = SummaryField.PendingTasks,
        ["currentwork"] = SummaryField.CurrentWork,
        ["nextstep"] = SummaryField.NextStep
    };

    private enum FileField
    {
        Path,
        Summary,
        KeyCode,
        Other
    }

    private static readonly Dictionary<string, FileField> FileFieldByName = new(StringComparer.Ordinal)
    {
        ["path"] = FileField.Path,
        ["summary"] = FileField.Summary,
        ["keycode"] = FileField.KeyCode
    };

    private static bool TryParseObject(string json, [NotNullWhen(true)] out ParsedFields? fields)
    {
        fields = null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            var parsed = new ParsedFields();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                string name = reader.GetString() ?? string.Empty;
                if (!reader.Read()) return false;

                // Accept both goose's snake_case and a camelCase variant.
                string key = name.Replace("_", string.Empty).ToLowerInvariant();
                SummaryField field = FieldByName.TryGetValue(key, out SummaryField mapped)
                    ? mapped
                    : SummaryField.Extra;
                switch (field)
                {
                    case SummaryField.UserIntent: ReadStringList(ref reader, parsed.UserIntent); break;
                    case SummaryField.TechnicalConcepts: ReadStringList(ref reader, parsed.TechnicalConcepts); break;
                    case SummaryField.Files: ReadFileList(ref reader, parsed.Files); break;
                    case SummaryField.ErrorsAndFixes: ReadStringList(ref reader, parsed.ErrorsAndFixes); break;
                    case SummaryField.ProblemSolving: ReadStringList(ref reader, parsed.ProblemSolving); break;
                    case SummaryField.UserMessages: ReadStringList(ref reader, parsed.UserMessages); break;
                    case SummaryField.PendingTasks: ReadStringList(ref reader, parsed.PendingTasks); break;
                    case SummaryField.CurrentWork: parsed.CurrentWork = ReadOptionalString(ref reader); break;
                    case SummaryField.NextStep: parsed.NextStep = ReadOptionalString(ref reader); break;
                    default:
                        using (JsonDocument doc = JsonDocument.ParseValue(ref reader))
                        {
                            parsed.Extra[name] = doc.RootElement.Clone();
                        }
                        break;
                }
            }

            parsed.Normalize();
            fields = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ReadStringList(ref Utf8JsonReader reader, List<string> target)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            int depth = reader.CurrentDepth;
            while (reader.Read() && !(reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth))
            {
                target.Add(StringifyValue(ref reader));
            }
        }
        else if (reader.TokenType != JsonTokenType.Null)
        {
            target.Add(StringifyValue(ref reader));
        }
    }

    private static void ReadFileList(ref Utf8JsonReader reader, List<FileActivity> target)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            int depth = reader.CurrentDepth;
            while (reader.Read() && !(reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth))
            {
                target.Add(ReadFileActivity(ref reader));
            }
        }
        else if (reader.TokenType != JsonTokenType.Null)
        {
            target.Add(ReadFileActivity(ref reader));
        }
    }

    private static FileActivity ReadFileActivity(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return new FileActivity(StringifyValue(ref reader), string.Empty, null);
        }

        string path = string.Empty;
        string summary = string.Empty;
        string? keyCode = null;
        int depth = reader.CurrentDepth;
        while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string name = reader.GetString() ?? string.Empty;
            if (!reader.Read()) break;
            FileField fileField = FileFieldByName.TryGetValue(
                name.Replace("_", string.Empty).ToLowerInvariant(), out FileField mapped)
                ? mapped
                : FileField.Other;
            switch (fileField)
            {
                case FileField.Path: path = StringifyValue(ref reader); break;
                case FileField.Summary: summary = StringifyValue(ref reader); break;
                case FileField.KeyCode:
                    string code = StringifyValue(ref reader);
                    keyCode = code.Trim().Length == 0 ? null : code;
                    break;
                default: StringifyValue(ref reader); break;
            }
        }

        return new FileActivity(path, summary, keyCode);
    }

    private static string? ReadOptionalString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        string value = StringifyValue(ref reader);
        return value.Trim().Length == 0 ? null : value;
    }

    /// <summary>
    ///     Consume one JSON value and stringify it leniently. The reader is left
    ///     on the value's final token so the caller can advance to the next one.
    /// </summary>
    private static string StringifyValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() ?? string.Empty;
            case JsonTokenType.Null:
                return string.Empty;
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Number:
                return reader.HasValueSequence
                    ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                    : Encoding.UTF8.GetString(reader.ValueSpan);
            case JsonTokenType.StartObject:
            {
                var parts = new List<string>();
                int depth = reader.CurrentDepth;
                while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string key = reader.GetString() ?? string.Empty;
                    if (!reader.Read()) break;
                    parts.Add(key + ": " + StringifyValue(ref reader));
                }
                return string.Join("; ", parts);
            }
            case JsonTokenType.StartArray:
            {
                var parts = new List<string>();
                int depth = reader.CurrentDepth;
                while (reader.Read() && !(reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth))
                {
                    parts.Add(StringifyValue(ref reader));
                }
                return string.Join("; ", parts);
            }
            default:
                return string.Empty;
        }
    }
}

/// <summary>
///     AOT-safe serialization metadata for the structured compaction summary.
///     Parsing stays on <see cref="Utf8JsonReader" /> (the schema is lenient), so
///     this context exists for the deterministic write path only.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompactionSummary))]
[JsonSerializable(typeof(FileActivity))]
internal sealed partial class CompactionSummaryJsonContext : JsonSerializerContext
{
}
