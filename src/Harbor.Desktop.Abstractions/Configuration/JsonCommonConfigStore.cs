// JsonCommonConfigStore.cs — JSON-backed implementation of ICommonConfigStore.
//
// Persists the shared CommonConfig to ~/.harbor/config.json using
// System.Text.Json with SOURCE-GENERATED metadata (ConfigJsonContext) so it
// works under NativeAOT — reflection-based serialization is unavailable
// there. Writes are atomic (temp file + File.Move) and serialised via a
// SemaphoreSlim so concurrent callers don't truncate each other's
// writes. Reads fall back to the default CommonConfig when the file is missing
// or corrupt — never throws for expected IO failures.
//
// This mirrors JsonAppConfigStore<T>'s design. The non-generic signature
// (ICommonConfigStore vs IAppConfigStore<T>) keeps the DI surface simple:
// there is exactly one CommonConfig type, so there is exactly one
// JsonCommonConfigStore registration per app.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using System.Text.Json.Serialization.Metadata;
namespace Harbor.Desktop.Abstractions.Configuration;
/// <summary>
///     JSON-backed <see cref="ICommonConfigStore" />. Reads and writes the
///     shared config file at <see cref="CommonConfig.ConfigFilePath" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Atomic writes:</b> every <see cref="SaveAsync" /> writes to a
///         sibling <c>&lt;file&gt;.tmp</c> file then <see cref="File.Move" />
///         (atomic on POSIX, atomic-replace on Windows) into place. A crash
///         mid-write leaves the previous file intact.
///     </para>
///     <para>
///         <b>Thread safety:</b> a single <see cref="SemaphoreSlim" /> guards
///         every Load/Save/Update, so concurrent calls are serialised.
///     </para>
///     <para>
///         <b>Missing file:</b> <see cref="LoadAsync" /> returns the default
///         <see cref="CommonConfig" /> passed at construction — apps boot with
///         sane defaults before the user has ever saved anything.
///     </para>
///     <para>
///         <b>Corrupt file:</b> if JSON deserialisation fails, LoadAsync
///         returns <see cref="Result.IsFailure" /> with the parser error. The
///         caller (typically the composition root) decides whether to log +
///         fall back to defaults or surface the error to the user.
///     </para>
/// </remarks>
public sealed class JsonCommonConfigStore : ICommonConfigStore
{
    // AOT-safe metadata: seeded from the source-generated ConfigJsonContext,
    // with the immutable-collection converters layered on top. Do NOT switch
    // these calls back to the reflection-based JsonSerializer.Deserialize<T>(
    // json, options) form — it breaks NativeAOT-published apps.
    private static readonly JsonTypeInfo<CommonConfig> CommonConfigInfo = ConfigJson.CommonConfigInfo;

    private readonly CommonConfig _default;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonCommonConfigStore> _logger;

    /// <summary>
    ///     Construct a JSON-backed common-config store.
    /// </summary>
    /// <param name="defaultConfig">
    ///     The default config returned by <see cref="LoadAsync" /> when the
    ///     file is missing. Typically <c>new CommonConfig()</c>.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public JsonCommonConfigStore(CommonConfig defaultConfig, ILogger<JsonCommonConfigStore> logger)
    {
        _default = defaultConfig ?? throw new ArgumentNullException(nameof(defaultConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = _default.ConfigFilePath;
            if (!File.Exists(path))
            {
                _logger.LogInformation("Common config file not found at {Path}, using defaults", path);
                return Result.Success(_default);
            }

            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var config = JsonSerializer.Deserialize(json, CommonConfigInfo);
            if (config is null)
            {
                _logger.LogWarning("Common config at {Path} deserialized to null, using defaults", path);
                return Result.Success(_default);
            }
            return Result.Success(config);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse common config JSON at {Path}", _default.ConfigFilePath);
            return Result.Failure<CommonConfig>($"Common config at {_default.ConfigFilePath} is corrupt: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load common config from {Path}", _default.ConfigFilePath);
            return Result.Failure<CommonConfig>(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = config.ConfigFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // MERGE with existing JSON to preserve fields from other config systems
            // (e.g. HarborConfig's "provider", "model", "tui", "onboarded", "providers",
            // "enabledPlugins", "disabledTools", "maxSteps", "costLimit", "compaction").
            // Without this, saving CommonConfig wipes those fields and breaks the CLI.
            string existingJson = "{}";
            if (File.Exists(path))
            {
                existingJson = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            }

            // Parse existing JSON, update CommonConfig fields, preserve everything else.
            using var existingDoc = JsonDocument.Parse(string.IsNullOrEmpty(existingJson) ? "{}" : existingJson);
            using var memStream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(memStream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                // Write CommonConfig fields
                // JsonProperty.WriteTo writes BOTH name AND value — don't call WritePropertyName first!
                byte[] commonJson = JsonSerializer.SerializeToUtf8Bytes(config, CommonConfigInfo);
                using var commonDoc = JsonDocument.Parse(commonJson);
                foreach (var prop in commonDoc.RootElement.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }

                // Write existing fields that CommonConfig doesn't have
                foreach (var prop in existingDoc.RootElement.EnumerateObject())
                {
                    if (commonDoc.RootElement.TryGetProperty(prop.Name, out _))
                        continue;
                    prop.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            string mergedJson = Encoding.UTF8.GetString(memStream.ToArray());
            string tempPath = path + ".tmp";

            // Write to temp file first, then atomically move into place.
            await File.WriteAllTextAsync(tempPath, mergedJson, ct).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);

            _logger.LogDebug("Common config saved to {Path}", path);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save common config to {Path}", config.ConfigFilePath);
            return Result.Failure(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default)
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
}

/// <summary>
///     Minimal System.Text.Json converter for
///     <see cref="ImmutableDictionary{TKey, TValue}" />. Mirrors
///     <c>ImmutableListConverter&lt;T&gt;</c> in <c>JsonAppConfigStore.cs</c>:
///     round-trips via a mutable <see cref="Dictionary{TKey, TValue}" /> and
///     calls <see cref="ImmutableDictionary.ToImmutableDictionary" />.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
internal sealed class ImmutableDictionaryConverter<TKey, TValue> : JsonConverter<ImmutableDictionary<TKey, TValue>>
    where TKey : notnull
{
    /// <summary>Singleton instance — the converter is stateless.</summary>
    public static readonly ImmutableDictionaryConverter<TKey, TValue> Instance = new();

    private ImmutableDictionaryConverter() { }

    /// <inheritdoc />
    public override ImmutableDictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return ImmutableDictionary<TKey, TValue>.Empty;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject token for ImmutableDictionary<{typeof(TKey).Name},{typeof(TValue).Name}>, got {reader.TokenType}.");
        }

        var dict = new Dictionary<TKey, TValue>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName token, got {reader.TokenType}.");
            }
            string? keyStr = reader.GetString();
            reader.Read();
            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
            if (keyStr is null)
            {
                throw new JsonException("Null property name in ImmutableDictionary JSON.");
            }
            // Convert the JSON string key to TKey. Only string keys are
            // supported by CommonConfig today, but the converter stays
            // generic for future reuse.
            var key = (TKey)(object)keyStr;
            dict[key] = value!;
        }
        return dict.ToImmutableDictionary();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ImmutableDictionary<TKey, TValue> value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            string keyStr = kv.Key is string s ? s : kv.Key?.ToString() ?? string.Empty;
            writer.WritePropertyName(keyStr);
            JsonSerializer.Serialize(writer, kv.Value, options);
        }
        writer.WriteEndObject();
    }
}
