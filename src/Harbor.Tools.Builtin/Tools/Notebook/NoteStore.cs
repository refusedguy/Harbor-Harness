using System.Text;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Notebook persistence: resolves the per-session JSON file under the
///     notes root and guards load/save symmetrically — an I/O failure
///     surfaces as a <see cref="Result" /> error instead of escaping the
///     tool contract. Shared by all <see cref="INoteCommand" /> handlers.
/// </summary>
internal sealed class NoteStore
{
    private readonly string _notesRoot;

    /// <summary>
    ///     Construct a <see cref="NoteStore" /> rooted at
    ///     <paramref name="notesRoot" />.
    /// </summary>
    /// <param name="notesRoot">Directory where per-session note JSON files live.</param>
    internal NoteStore(string notesRoot)
    {
        _notesRoot = notesRoot;
    }

    /// <summary>
    ///     Resolve the JSON file path for <paramref name="sessionId" />.
    /// </summary>
    internal string ResolvePath(string sessionId)
        => Path.Combine(_notesRoot, SanitizeSessionId(sessionId) + ".json");

    /// <summary>
    ///     Load the notes for <paramref name="notesPath" />; a missing file
    ///     yields an empty dictionary.
    /// </summary>
    internal async Task<Result<Dictionary<string, NoteEntry>>> LoadAsync(
        string notesPath, CancellationToken ct)
        => await Result.Try(
                () => LoadCoreAsync(notesPath, ct),
                ToolErrors.Handler("notebook", ct, failurePrefix: "Failed to load notes: "))
            .ConfigureAwait(false);

    /// <summary>
    ///     Save <paramref name="notes" /> to <paramref name="notesPath" />
    ///     atomically (temp file then rename); the caller's success payload
    ///     is built only after a verified save.
    /// </summary>
    internal async Task<Result> SaveAsync(
        string notesPath, Dictionary<string, NoteEntry> notes, CancellationToken ct)
        => await Result.Try(() => SaveCoreAsync(notesPath, notes, ct),
                ToolErrors.Handler("notebook", ct, failurePrefix: "Failed to save notes: "))
            .ConfigureAwait(false);

    /// <summary>
    ///     Allow only safe chars in a session id; replace anything else with '_'.
    /// </summary>
    internal static string SanitizeSessionId(string sessionId)
    {
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

    /// <summary>
    ///     Default notes root: <c>~/.harbor/notes</c>.
    /// </summary>
    internal static string GetDefaultNotesRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".harbor", "notes");
    }

    private static async Task<Dictionary<string, NoteEntry>> LoadCoreAsync(string path, CancellationToken ct)
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

    private static async Task SaveCoreAsync(string path, Dictionary<string, NoteEntry> notes, CancellationToken ct)
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
}
