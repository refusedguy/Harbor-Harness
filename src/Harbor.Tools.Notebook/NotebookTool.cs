using System.Text;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Persistent per-session markdown notes. The agent can stash small bits of context
///     (file paths, decisions, intermediate findings) and pull them back later. Notes
///     are stored as JSON in <c>~/.harbor/notes/&lt;sessionId&gt;.json</c> and can be
///     surfaced into the next turn's system prompt by the host.
/// </summary>
public sealed class NotebookTool : ITool
{
    private const int MaxContentChars = 16_384;
    private const int MaxKeyChars = 128;
    private const int MaxNotesPerSession = 256;

    private readonly ILogger<NotebookTool> _logger;
    private readonly string _notesRoot;

    /// <summary>
    ///     Construct a <see cref="NotebookTool" /> rooted at <c>~/.harbor/notes</c>.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public NotebookTool(ILogger<NotebookTool> logger) : this(logger, GetDefaultNotesRoot())
    {
    }

    /// <summary>
    ///     Construct a <see cref="NotebookTool" /> with a custom root directory.
    ///     Used in tests to point at a temp directory.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="notesRoot">Directory where per-session note JSON files live.</param>
    public NotebookTool(ILogger<NotebookTool> logger, string notesRoot)
    {
        _logger = logger;
        _notesRoot = notesRoot;
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("notebook");

    /// <inheritdoc />
    public string DisplayName => "Notebook";

    /// <inheritdoc />
    public string Description =>
        "Persistent markdown notes keyed by string. Actions: get/set/add/clear/list. " +
        "Stored per session at ~/.harbor/notes/<sessionId>.json. " +
        "Use for decisions, file lists, intermediate findings across long tasks.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "notebook: Persistent per-session notes (get/set/add/clear/list)";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `notebook` to remember things across many turns — file lists, decisions, TODOs",
        "set replaces a key; add appends to a key's existing content",
        "list returns all keys with the first line of each note",
        "clear removes a single key (or all if no key given)",
        "Notes are scoped to the current session id — they don't leak across sessions"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "action":  { "type": "string", "description": "One of: get | set | add | clear | list" },
                                                                          "key":     { "type": "string", "description": "Note key (required for get/set/add/clear)" },
                                                                          "content": { "type": "string", "description": "Note content (required for set; appended for add)" }
                                                                        },
                                                                        "required": ["action"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var aEl)
            || aEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(aEl.GetString()))
            return Result.Failure("Missing or empty 'action'.");

        string action = aEl.GetString()!;
        string[] valid = ["get", "set", "add", "clear", "list"];
#pragma warning disable S3267 // Hot-path loop with early-exit; LINQ Where + Any allocates enumerator.
        bool ok = false;
        foreach (string v in valid)
        {
            if (string.Equals(action, v, StringComparison.OrdinalIgnoreCase))
            {
                ok = true;
                break;
            }
        }
#pragma warning restore S3267
        if (!ok)
            return Result.Failure($"Unknown action '{action}'. Valid: get, set, add, clear, list.");

        if (action is "get" or "set" or "add" or "clear")
        {
            if (!args.TryGetProperty("key", out var kEl)
                || kEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(kEl.GetString()))
                return Result.Failure($"Action '{action}' requires non-empty 'key'.");
            if (kEl.GetString()!.Length > MaxKeyChars)
                return Result.Failure($"'key' too long (max {MaxKeyChars} chars).");
        }

        if (action is "set" or "add")
        {
            if (!args.TryGetProperty("content", out var cEl) || cEl.ValueKind != JsonValueKind.String)
                return Result.Failure($"Action '{action}' requires 'content' string.");
            if (cEl.GetString()!.Length > MaxContentChars)
                return Result.Failure($"'content' too long (max {MaxContentChars} chars).");
        }

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string action = args.GetProperty("action").GetString()!.ToLowerInvariant();
        string? key = args.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()
            : null;
        string? content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        string sessionId = SanitizeSessionId(context.SessionId);
        string path = Path.Combine(_notesRoot, sessionId + ".json");

        Dictionary<string, NoteEntry> notes;
        try
        {
            notes = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Error("notebook cancelled");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to load notes: {ex.Message}");
        }

        switch (action)
        {
            case "get":
            {
                if (key is null) return ToolResult.Error("Internal: key null for get.");
                if (!notes.TryGetValue(key, out var entry))
                    return ToolResult.Error($"No note with key '{key}'.");
                return ToolResult.Success(
                    $"# {key}\n\n{entry.Content}",
                    new { key, content = entry.Content, updatedAt = entry.UpdatedAt });
            }
            case "set":
            {
                if (key is null || content is null) return ToolResult.Error("Internal: key/content null for set.");
                if (notes.Count >= MaxNotesPerSession && !notes.ContainsKey(key))
                    return ToolResult.Error($"Too many notes (max {MaxNotesPerSession}).");
                notes[key] = new NoteEntry(content, DateTimeOffset.UtcNow);
                await SaveAsync(path, notes, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Notebook set {Key} ({Chars} chars) for {Session}", key, content.Length, sessionId);
                return ToolResult.Success(
                    $"Set note '{key}' ({content.Length} chars).",
                    new { key, chars = content.Length, totalNotes = notes.Count });
            }
            case "add":
            {
                if (key is null || content is null) return ToolResult.Error("Internal: key/content null for add.");
                if (notes.TryGetValue(key, out var existing))
                {
                    string combined = existing.Content + "\n\n" + content;
                    if (combined.Length > MaxContentChars)
                        return ToolResult.Error(
                            $"Combined content would exceed {MaxContentChars} chars " +
                            $"(currently {existing.Content.Length}, adding {content.Length}).");
                    notes[key] = existing with { Content = combined, UpdatedAt = DateTimeOffset.UtcNow };
                }
                else
                {
                    notes[key] = new NoteEntry(content, DateTimeOffset.UtcNow);
                }
                await SaveAsync(path, notes, cancellationToken).ConfigureAwait(false);
                return ToolResult.Success(
                    $"Appended to note '{key}' (now {notes[key].Content.Length} chars).",
                    new { key, chars = notes[key].Content.Length, totalNotes = notes.Count });
            }
            case "clear":
            {
                if (key is null)
                {
                    int removed = notes.Count;
                    notes.Clear();
                    await SaveAsync(path, notes, cancellationToken).ConfigureAwait(false);
                    return ToolResult.Success($"Cleared {removed} note(s).", new { removed });
                }
                if (!notes.Remove(key))
                    return ToolResult.Error($"No note with key '{key}'.");
                await SaveAsync(path, notes, cancellationToken).ConfigureAwait(false);
                return ToolResult.Success($"Cleared note '{key}'.", new { key, remaining = notes.Count });
            }
            case "list":
            {
                if (notes.Count == 0)
                    return ToolResult.Success("(no notes in this session)");
                using var sb = StringBuilderPool.Rent(notes.Count * 64);
                var b = sb.Builder;
                b.Append(notes.Count).Append(" note(s):");
                foreach (var kv in notes)
                {
                    string preview = kv.Value.Content;
                    int nl = preview.IndexOf('\n');
                    if (nl >= 0) preview = preview[..nl];
                    if (preview.Length > 80) preview = preview[..80] + "…";
                    b.Append("\n  • ").Append(kv.Key).Append(" — ").Append(preview);
                }
                return ToolResult.Success(
                    b.ToString(),
                    new { count = notes.Count, keys = notes.Keys.ToArray() });
            }
            default:
                return ToolResult.Error($"Unknown action '{action}'.");
        }
    }

    private static async Task<Dictionary<string, NoteEntry>> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new Dictionary<string, NoteEntry>(StringComparer.Ordinal);

        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous);
        var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
        var dict = new Dictionary<string, NoteEntry>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            string c = prop.Value.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString() ?? string.Empty
                : string.Empty;
            var updated = prop.Value.TryGetProperty("updatedAt", out var uEl)
                          && uEl.ValueKind == JsonValueKind.String
                          && DateTimeOffset.TryParse(uEl.GetString(), out var dto)
                ? dto
                : DateTimeOffset.UtcNow;
            dict[prop.Name] = new NoteEntry(c, updated);
        }
        return dict;
    }

    private static async Task SaveAsync(string path, Dictionary<string, NoteEntry> notes, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Atomic write — temp file then rename.
        string tempPath = path + ".tmp";

        await using (var fs = new FileStream(
                         tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous))
        {
            await using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false });
            w.WriteStartObject();
            foreach (var kv in notes)
            {
                w.WritePropertyName(kv.Key);
                w.WriteStartObject();
                w.WriteString("content", kv.Value.Content);
                w.WriteString("updatedAt", kv.Value.UpdatedAt);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            await w.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, path, true);
    }

    private static string SanitizeSessionId(string sessionId)
    {
        // Allow only safe chars; replace anything else with '_'.
        if (string.IsNullOrEmpty(sessionId)) return "default";
        var sb = new StringBuilder(sessionId.Length);
        foreach (char c in sessionId)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }

    private static string GetDefaultNotesRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".harbor", "notes");
    }

    private sealed record NoteEntry(string Content, DateTimeOffset UpdatedAt);
}
