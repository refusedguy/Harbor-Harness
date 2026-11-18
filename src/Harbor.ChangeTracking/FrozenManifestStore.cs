namespace Harbor.ChangeTracking;

/// <summary>
///     Persistence for the frozen change-set artifact: <c>changeset.json</c>
///     and the <c>change.patch</c> path it records. Extracted from
///     <see cref="ChangeSetFreezer" /> (issue #2593, F17) so the god class keeps
///     git staging and verification only: this type validates a run id, writes
///     both artifacts atomically and reads the manifest back with the exact
///     error texts the pipeline has always returned. No public API changed —
///     <see cref="ChangeSetFreezer.TryLoad" /> delegates here.
/// </summary>
internal static class FrozenManifestStore
{
    private const string PatchFileName = "change.patch";
    private const string ManifestFileName = "changeset.json";

    internal static string PatchPath(string runId) =>
        Path.Combine(WorkspacePaths.Default.RunDir(runId), PatchFileName);

    internal static string ManifestPath(string runId) =>
        Path.Combine(WorkspacePaths.Default.RunDir(runId), ManifestFileName);

    /// <summary>
    ///     Validate <paramref name="runIdValue" /> and load the recorded frozen
    ///     change set. The run-id guard is the shared one (F5): a crafted id
    ///     cannot escape the runs root. A missing patch or inventory is a
    ///     <c>Result.Failure</c> naming the freeze stage, never an empty set.
    /// </summary>
    internal static Result<FrozenChangeSet> TryLoad(string? runIdValue) =>
        RunIdValidation.ValidateRunId(runIdValue).Bind(runId =>
        {
            string patchPath = PatchPath(runId);
            string changesetPath = ManifestPath(runId);
            if (!File.Exists(patchPath) || !File.Exists(changesetPath))
                return Result.Failure<FrozenChangeSet>(
                    $"Run '{runId}' has no frozen change set ('{patchPath}', '{changesetPath}'): " +
                    "stage 'freeze' (S3, #377) has not produced it — refusing to invent a change set.");
            return ReadChangesetJson(changesetPath, patchPath);
        });

    internal static Result<string> WriteBytesAtomic(string path, byte[] bytes, string runId)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Result.Failure<string>($"Cannot create run directory '{dir}': {ex.Message}");
            }
        }
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            return Result.Failure<string>($"Cannot write frozen patch for run '{runId}': {ex.Message}");
        }
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            FileCleanup.QuietDelete(tmp);
            return Result.Failure<string>($"Cannot write frozen patch for run '{runId}': {ex.Message}");
        }
        return Result.Success(path);
    }

    internal static Result<string> WriteChangesetJson(string path, FrozenChangeSet set)
    {
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("runId", set.RunId);
                writer.WriteString("baseRevision", set.BaseRevision);
                writer.WriteString("headRevision", set.HeadRevision);
                writer.WriteBoolean("isEmpty", set.IsEmpty);
                writer.WriteNumber("ignoredPathCount", set.IgnoredPathCount);
                writer.WriteStartArray("entries");
                for (int i = 0; i < set.Entries.Count; i++)
                {
                    FrozenChangeEntry e = set.Entries[i];
                    writer.WriteStartObject();
                    writer.WriteString("path", e.Path);
                    writer.WriteString("status", e.Status);
                    if (e.OldPath is null)
                        writer.WriteNull("oldPath");
                    else
                        writer.WriteString("oldPath", e.OldPath);
                    if (e.Insertions is null)
                        writer.WriteNull("insertions");
                    else
                        writer.WriteNumber("insertions", e.Insertions.Value);
                    if (e.Deletions is null)
                        writer.WriteNull("deletions");
                    else
                        writer.WriteNumber("deletions", e.Deletions.Value);
                    writer.WriteBoolean("binary", e.Binary);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Result.Failure<string>($"Cannot serialize frozen change set for run '{set.RunId}': {ex.Message}");
        }
        return WriteBytesAtomic(path, bytes, set.RunId);
    }

    internal static Result<FrozenChangeSet> ReadChangesetJson(string changesetPath, string patchPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(changesetPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return Result.Failure<FrozenChangeSet>($"Cannot read frozen change set at '{changesetPath}': {ex.Message}");
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' is not valid JSON: {ex.Message}");
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' is not a JSON object.");
            string? runId = GetString(root, "runId");
            string? baseRevision = GetString(root, "baseRevision");
            string? headRevision = GetString(root, "headRevision");
            if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(baseRevision) || string.IsNullOrWhiteSpace(headRevision))
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' is missing required fields.");
            if (!root.TryGetProperty("ignoredPathCount", out JsonElement ignoredEl)
                || ignoredEl.ValueKind != JsonValueKind.Number
                || !ignoredEl.TryGetInt32(out int ignoredPathCount)
                || ignoredPathCount < 0)
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' has an invalid 'ignoredPathCount'.");
            if (!root.TryGetProperty("entries", out JsonElement entriesEl)
                || entriesEl.ValueKind != JsonValueKind.Array)
                return Result.Failure<FrozenChangeSet>($"Frozen change set at '{changesetPath}' has an invalid 'entries' array.");
            var entries = new List<FrozenChangeEntry>(entriesEl.GetArrayLength());
            foreach (JsonElement el in entriesEl.EnumerateArray())
            {
                Result<FrozenChangeEntry> entry = ReadEntry(el);
                if (entry.IsFailure)
                    return entry.ConvertFailure<FrozenChangeSet>()
                        .MapError(e => $"Frozen change set at '{changesetPath}' has an invalid entry: {e}");
                // Hoisted to the guard level (see FreezeIsolatedAsync).
                FrozenChangeEntry parsed = entry.Value;
                entries.Add(parsed);
            }
            return Result.Success(FrozenChangeSet.Create(
                runId, baseRevision, headRevision, patchPath, entries, ignoredPathCount));
        }
    }

    private static Result<FrozenChangeEntry> ReadEntry(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return Result.Failure<FrozenChangeEntry>("entry is not a JSON object.");
        string? path = GetString(el, "path");
        string? status = GetString(el, "status");
        string? oldPath = GetString(el, "oldPath");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(status))
            return Result.Failure<FrozenChangeEntry>("entry is missing 'path' or 'status'.");
        int? insertions = GetNullableInt(el, "insertions");
        int? deletions = GetNullableInt(el, "deletions");
        bool binary = el.TryGetProperty("binary", out JsonElement binEl)
            && binEl.ValueKind == JsonValueKind.True;
        try
        {
            return Result.Success(FrozenChangeEntry.Create(path, status, oldPath, insertions, deletions, binary));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<FrozenChangeEntry>(ex.Message);
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null,
        };
    }

    private static int? GetNullableInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n) && n >= 0)
            return n;
        return null;
    }
}
