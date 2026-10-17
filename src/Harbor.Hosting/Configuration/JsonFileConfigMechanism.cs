// JsonFileConfigMechanism.cs — the single file-backed persistence MECHANISM
// behind both JSON config stores (Bridge, #1593).
//
// #1593: JsonCommonConfigStore and JsonAppConfigStore<T> each carried a
// private copy of the same persistence skeleton — the File.Exists probe, the
// ReadAllTextAsync read, the corrupt-file → Result.Failure mapping, the
// ensure-directory step, the serialized write, and the temp file +
// File.Delete + File.Move atomic write. The MECHANISM (how JSON survives to
// disk and back) had fused with the FORM (which config record, serializer and
// log wording the caller uses) — ABSTRACTION.STRATEGY/BRIDGE: abstraction and
// implementation did not vary independently, and a third JSON store would
// have meant a third copy of the skeleton. This file extracts the mechanism;
// the two stores are now thin bridges over it:
//
//     JsonCommonConfigStore ──┐
//                              ├─→ JsonFileConfigMechanism<T> ── File IO
//     JsonAppConfigStore<T> ───┘
//
// A bridge declares its form as a <see cref="JsonFileConfigHooks{T}" /> at
// construction: which paths to read and write, how to deserialize (plain
// metadata, or the defaults-merge below), how to serialize (plain, or the
// foreign-field-preserving indented merge), how to shape a loaded record
// (CommonConfig pins ConfigDirectory to the store's own — #913; app configs
// don't), and its own SG4 [LoggerMessage] log wording. The mechanism owns
// everything else: the SemaphoreSlim-guarded Load/Save/Update pipelines, the
// missing-file fallback to the supplied default, the corrupt/generic failure
// mapping, directory creation, and the serialized atomic write.
//
// Contract notes, unchanged from the copies this merged:
// - <b>Atomic writes:</b> every save writes to a sibling
//   <c>&lt;file&gt;.tmp</c> file then moves it into place with
//   <see cref="File.Move" /> (atomic on POSIX, atomic-replace on Windows). A
//   crash mid-write leaves the previous file intact.
// - <b>Thread safety:</b> a single <see cref="SemaphoreSlim" /> per mechanism
//   instance guards every Load/Save/Update, so concurrent calls are
//   serialized.
// - <b>Missing file:</b> LoadAsync returns the default config the bridge
//   supplied — apps boot with sane defaults before the user has ever saved
//   anything.
// - <b>Corrupt file:</b> if JSON deserialization fails, LoadAsync returns
//   failure text from the bridge (<see cref="Result.IsFailure" /> with the
//   parser error wording). The caller (typically the composition root)
//   decides whether to log + fall back to defaults or surface the error to
//   the user.
// - <b>AOT:</b> deserialization callers pass a source-generated
//   <see cref="JsonTypeInfo{T}" />; this file never touches the
//   reflection-based <c>JsonSerializer.Deserialize&lt;T&gt;(json, options)</c>
//   overloads (see #414 in JsonAppConfigStore for why).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CSharpFunctionalExtensions;

namespace Harbor.Hosting.Configuration;

/// <summary>
///     The "form" half of the Bridge (#1593): everything that varies per JSON
///     config store — paths, (de)serializer choice, load shaping and log
///     wording — while <see cref="JsonFileConfigMechanism{T}" /> owns the
///     persistence pipeline. Supplied by each bridge at construction and
///     immutable afterwards.
/// </summary>
/// <typeparam name="T">The config record this bridge persists.</typeparam>
internal sealed class JsonFileConfigHooks<T> where T : class
{
    /// <summary>
    ///     The default config returned by the Load pipeline when the file is
    ///     missing or deserialized to <c>null</c>.
    /// </summary>
    public required T Default { get; init; }

    /// <summary>
    ///     Path the Load pipeline reads from — the store's own file. Never
    ///     payload-derived (#913: <c>ConfigDirectory</c> round-trips through
    ///     the file, so trusting it would make the read target caller-
    ///     controlled).
    /// </summary>
    public required Func<string> LoadPath { get; init; }

    /// <summary>
    ///     Path the Save pipeline writes to, resolved from the saved payload.
    ///     <see cref="JsonAppConfigStore{T}" /> derives it from
    ///     <c>config.ConfigFilePath</c>; <see cref="JsonCommonConfigStore" />
    ///     ignores the payload and writes its own file (#913).
    /// </summary>
    public required Func<T, string> SavePath { get; init; }

    /// <summary>
    ///     File text → config record, or <c>null</c> to fall back to
    ///     <see cref="Default" />. Implementations must use source-generated
    ///     metadata — see <see cref="JsonFileConfigMechanism{T}" />'s AOT note.
    /// </summary>
    public required Func<string, T?> Deserialize { get; init; }

    /// <summary>
    ///     Config record → the final file text, invoked under the mechanism's
    ///     lock. Runs before the temp-file write; may do its own IO (the
    ///     common store reads the pre-save file to preserve foreign fields —
    ///     that read lives here because only stores that merge on save need
    ///     it).
    /// </summary>
    public required Func<T, CancellationToken, Task<string>> Serialize { get; init; }

    /// <summary>
    ///     Fix-up applied to a successfully deserialized record before it is
    ///     returned. The common store pins <c>ConfigDirectory</c> to the
    ///     store's own (#913); app stores pass the record through.
    /// </summary>
    public required Func<T, T> AfterLoad { get; init; }

    /// <summary>
    ///     Failure text for the corrupt-file path (<see cref="JsonException" />
    ///     during load).
    /// </summary>
    public required Func<Exception, string> CorruptError { get; init; }

    // Logging, bound to each bridge's own SG4 [LoggerMessage] class. The
    // mechanism emits only these six events — the exact templates, levels and
    // EventIds stay in the bridges.

    /// <summary>File missing at <c>LoadPath</c>.</summary>
    public required Action<string> LogFileNotFound { get; init; }

    /// <summary>Deserialization returned null at <c>LoadPath</c>.</summary>
    public required Action<string> LogNullConfig { get; init; }

    /// <summary>JSON parse failed at <c>LoadPath</c>.</summary>
    public required Action<Exception, string> LogParseFailed { get; init; }

    /// <summary>Non-JSON load failure at <c>LoadPath</c>.</summary>
    public required Action<Exception, string> LogLoadFailed { get; init; }

    /// <summary>Save succeeded to <c>SavePath(config)</c>.</summary>
    public required Action<string, T> LogSaved { get; init; }

    /// <summary>Save failed targeting <c>SavePath(config)</c>.</summary>
    public required Action<Exception, string> LogSaveFailed { get; init; }
}

/// <summary>
///     The file-backed persistence mechanism shared by
///     <see cref="JsonCommonConfigStore" /> and <see cref="JsonAppConfigStore{T}" />
///     (#1593): lock-guarded load/save/update pipelines over JSON config
///     files with missing-file fallback, corrupt-file failure mapping and
///     serialized atomic writes. Knows nothing about any config type — the
///     bridges supply the form via <see cref="JsonFileConfigHooks{T}" />.
/// </summary>
/// <remarks>
///     One instance per store. The type is deliberately INTERNAL — it is the
///     mechanism of this composition root's two known bridges, not an
///     extension axis: a third JSON store would add a thin bridge over the
///     SAME pipeline, not grow this file.
/// </remarks>
/// <typeparam name="T">The config record the caller persists.</typeparam>
internal sealed class JsonFileConfigMechanism<T> where T : class
{
    private readonly JsonFileConfigHooks<T> _hooks;

    // Serialised writes: one SemaphoreSlim per store instance so concurrent
    // Load/Save/Update callers cannot truncate each other's writes.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    ///     Construct a mechanism binding. The bridge passes the full
    ///     <see cref="JsonFileConfigHooks{T}" />; afterwards the pipelines are
    ///     closed over it.
    /// </summary>
    public JsonFileConfigMechanism(JsonFileConfigHooks<T> hooks)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    /// <summary>
    ///     Load: missing file → the bridge's default; parse-null → the
    ///     default; corrupt JSON → failure text from
    ///     <see cref="JsonFileConfigHooks{T}.CorruptError" />; anything else →
    ///     the shaped record (<see cref="JsonFileConfigHooks{T}.AfterLoad" />).
    /// </summary>
    public async Task<Result<T>> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = _hooks.LoadPath();
            if (!File.Exists(path))
            {
                _hooks.LogFileNotFound(path);
                return Result.Success(_hooks.Default);
            }

            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var config = _hooks.Deserialize(json);
            if (config is null)
            {
                _hooks.LogNullConfig(path);
                return Result.Success(_hooks.Default);
            }

            return Result.Success(_hooks.AfterLoad(config));
        }
        catch (JsonException ex)
        {
            _hooks.LogParseFailed(ex, _hooks.LoadPath());
            return Result.Failure<T>(_hooks.CorruptError(ex));
        }
        catch (Exception ex)
        {
            _hooks.LogLoadFailed(ex, _hooks.LoadPath());
            return Result.Failure<T>(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Save: ensure the target directory, serialize via the bridge, write
    ///     to a sibling <c>.tmp</c> file, then atomically move into place.
    ///     Never derives storage decisions from unvalidated payload state the
    ///     bridge's <see cref="JsonFileConfigHooks{T}.SavePath" /> doesn't own.
    /// </summary>
    public async Task<Result> SaveAsync(T config, CancellationToken ct = default)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = _hooks.SavePath(config);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = await _hooks.Serialize(config, ct).ConfigureAwait(false);

            // Write to a temp file first, then atomically move into place. A
            // crash mid-write leaves the previous file intact.
            string tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);

            _hooks.LogSaved(path, config);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _hooks.LogSaveFailed(ex, _hooks.SavePath(config));
            return Result.Failure(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Load → apply <paramref name="updater" /> → save. Two lock
    ///     acquisitions, not one: the same read-modify-write window the stores
    ///     always had (each caller's Save re-reads nothing mid-flight).
    /// </summary>
    public async Task<Result> UpdateAsync(Func<T, T> updater, CancellationToken ct = default)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));

        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure)
        {
            return loadResult;
        }

        var updated = updater(loadResult.Value);
        return await SaveAsync(updated, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Deserialize <paramref name="json" /> with ABSENT keys filled from
    ///     <paramref name="defaultConfig" /> instead of being reset to null.
    ///     Moved here from <see cref="JsonCommonConfigStore" /> (#1593) and
    ///     genericised so the next JSON config store reuses it verbatim.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists (.NET 10 STJ source-gen):</b> metadata-based
    ///         deserialization assigns <c>default(T)</c> (i.e. NULL for string)
    ///         to every init-only property that is ABSENT from the JSON — it
    ///         does NOT leave the parameterless-constructor field-initializer
    ///         value in place (the reflection-based serializer does). A config
    ///         file written before a property existed therefore loaded as
    ///         <c>ConfigDirectory = null</c> and crashed
    ///         <c>Path.Combine(null, …)</c> on next use.
    ///     </para>
    ///     <para>
    ///         Fix: deep-merge the parsed file over the serialized defaults
    ///         node-first, then deserialize the merged node. Explicit nulls in
    ///         the file still win; only ABSENT keys fall back to defaults.
    ///     </para>
    /// </remarks>
    /// <typeparam name="TConfig">The config record type.</typeparam>
    internal static TConfig? DeserializeMergedWithDefaults<TConfig>(
        string json, TConfig defaultConfig, JsonTypeInfo<TConfig> typeInfo)
        where TConfig : class
    {
        var fileNode = JsonNode.Parse(json);
        if (fileNode is not JsonObject fileObject)
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }

        byte[] defaultsBytes = JsonSerializer.SerializeToUtf8Bytes(defaultConfig, typeInfo);
        using var defaultsDoc = JsonDocument.Parse(defaultsBytes);
        var defaultsNode = JsonNode.Parse(defaultsDoc.RootElement.GetRawText());
        if (defaultsNode is not JsonObject defaultsObject)
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }

        // The serialized defaults object carries EVERY property key, so no
        // key is "absent" after the merge and the null-reset cannot trigger.
        // File values win verbatim (including explicit nulls — explicit user
        // choice); keys missing from the file keep their default values.
        foreach (var (key, value) in fileObject)
        {
            defaultsObject[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
        }

        return defaultsNode.Deserialize(typeInfo);
    }

    /// <summary>
    ///     Serialize <paramref name="config" /> into the existing file text,
    ///     PRESERVING keys the config record doesn't own — the other config
    ///     systems sharing the file (e.g. HarborConfig's "provider", "model",
    ///     "tui", "onboarded", "providers", "enabledPlugins", "disabledTools",
    ///     "maxSteps", "costLimit", "compaction"). Without this, saving the
    ///     config record wipes those fields and breaks the CLI.
    /// </summary>
    /// <remarks>
    ///     The output is indented as a whole (the indented
    ///     <see cref="Utf8JsonWriter" /> covers both halves) — the on-disk
    ///     shape of the file that already shared this behaviour.
    /// </remarks>
    /// <typeparam name="TConfig">The config record type.</typeparam>
    internal static string SerializeMergedWithExisting<TConfig>(
        string existingJson, TConfig config, JsonTypeInfo<TConfig> typeInfo)
        where TConfig : class
    {
        // Parse existing JSON, write the config's own fields, preserve
        // everything else.
        using var existingDoc = JsonDocument.Parse(string.IsNullOrEmpty(existingJson) ? "{}" : existingJson);
        using var memStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memStream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            // Write the config's own fields.
            // JsonProperty.WriteTo writes BOTH name AND value — don't call WritePropertyName first!
            byte[] ownJson = JsonSerializer.SerializeToUtf8Bytes(config, typeInfo);
            using var ownDoc = JsonDocument.Parse(ownJson);
            foreach (var prop in ownDoc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            // Write existing fields the config record doesn't own.
            foreach (var prop in existingDoc.RootElement.EnumerateObject())
            {
                if (ownDoc.RootElement.TryGetProperty(prop.Name, out _))
                    continue;
                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(memStream.ToArray());
    }
}
