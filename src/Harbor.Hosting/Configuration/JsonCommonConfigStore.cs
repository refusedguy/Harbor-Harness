// JsonCommonConfigStore.cs — JSON-backed implementation of ICommonConfigStore.
//
// #534: this type MOVED here from Harbor.Desktop.Abstractions, which is
// IsPackable / `PackageId: Harbor.Desktop.Abstractions`.
// The layer matrix calls it Presentation (§2), and has said so since the matrix
// row was created (5d2df19f). A published Presentation package that stats the
// file, creates ~/.harbor and writes through a sibling `.tmp` is not a UI
// contract; it
// is a storage engine that also ships a schema, and PresentationCapabilityRules
// forbids `System.IO.File*` in every Presentation assembly anyway. Persistence
// belongs to the composition root, which already constructed both of these
// stores.
//
// The PORT (ICommonConfigStore), the DTOs (CommonConfig / AppConfigBase) and
// `CompositeConfig<T>` stayed in the leaf. The contract is the schema's; where the
// bytes come from is an outer layer's — the same split #742 made for IThemeStore
// / ThemeStore.
//
// Note the direction the matrix forces: Infrastructure may never reference
// Presentation, and Harbor.Desktop.Abstractions sits in the Presentation band, so
// "put this in Infrastructure" was never available. See
// DesktopAbstractionsLeafTakesNoIoRules for the guard that keeps the leaf clean.
//
// Persists the shared CommonConfig to ~/.harbor/config.json. Writes are
// atomic (temp file + File.Move) and serialized via a SemaphoreSlim so
// concurrent callers don't truncate each other's writes. Reads fall back to
// the default CommonConfig when the file is missing or corrupt — never throws
// for expected IO failures.
//
// #1593 (Bridge): the FILE PIPELINE itself (exists-probe, ReadAllTextAsync,
// corrupt→failure mapping, ensure-directory, serialized atomic write, and the
// common defaults-merge / foreign-field-preserve merges) lives in
// JsonFileConfigMechanism, shared with JsonAppConfigStore<T>. What is left
// here is the FORM — the parts that genuinely belong to CommonConfig:
// - its AOT-safe serializer (ConfigJson.CommonConfigInfo),
// - #913 path ownership (the store reads and writes its OWN file regardless
//   of what the payload's ConfigDirectory says, and pins/shapes the record
//   both ways),
// - defaults-merge on load and foreign-field merge on save,
// - this store's SG4 log wording.
// The non-generic signature (ICommonConfigStore vs IAppConfigStore<T>) keeps
// the DI surface simple: there is exactly one CommonConfig type, so there is
// exactly one JsonCommonConfigStore registration per app.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpFunctionalExtensions;
using Harbor.Desktop.Abstractions.Configuration;
namespace Harbor.Hosting.Configuration;
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
    private static readonly JsonTypeInfo<CommonConfig> CommonConfigInfo = ConfigJson.CommonConfigInfo;

    private readonly CommonConfig _default;
    private readonly JsonFileConfigMechanism<CommonConfig> _mechanism;

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
        if (logger is null) throw new ArgumentNullException(nameof(logger));

        // #1593: declare the FORM once; JsonFileConfigMechanism runs it.
        _mechanism = new(new JsonFileConfigHooks<CommonConfig>
        {
            Default = _default,
            LoadPath = () => _default.ConfigFilePath,
            // #913: the target is the store's OWN file — the same
            // _default.ConfigFilePath LoadAsync reads from — NOT one
            // re-derived from the payload's ConfigDirectory. That property is
            // init-only, round-trips through config.json, and is caller-
            // controlled, so trusting it made the write destination a function
            // of unvalidated input: a payload carrying a foreign
            // configDirectory wrote the whole config to a different tree
            // (creating it via Directory.CreateDirectory) and then re-persisted
            // that foreign path, so the split reproduced itself on every
            // subsequent save. The invariant belongs to the store, so the
            // store decides where it writes.
            SavePath = _ => _default.ConfigFilePath,
            Deserialize = json => JsonFileConfigMechanism<CommonConfig>.DeserializeMergedWithDefaults(json, _default, CommonConfigInfo),
            AfterLoad = config =>
            {
                // #913: pin the directory to the one this store was constructed
                // with, so the record handed back and the path SaveAsync writes to
                // can never disagree. `ConfigDirectory` round-trips through the
                // file, and the merge above lets the FILE's copy win — so before
                // this, a config.json carrying a stale absolute path (relocated
                // ~/.harbor, a config copied off another machine, a pinned E2E
                // sandbox) made LoadAsync return a record that pointed somewhere
                // other than the file it had just read.
                return config with { ConfigDirectory = _default.ConfigDirectory };
            },
            Serialize = async (config, ct) =>
            {
                // #913: the payload may carry a foreign or stale
                // ConfigDirectory; the file always records the directory this
                // store was constructed with, so the next Load has nothing
                // stale left to inherit.
                var pinned = config with { ConfigDirectory = _default.ConfigDirectory };

                // MERGE with existing JSON to preserve fields from other config systems
                // (e.g. HarborConfig's "provider", "model", "tui", "onboarded", "providers",
                // "enabledPlugins", "disabledTools", "maxSteps", "costLimit", "compaction").
                // Without this, saving CommonConfig wipes those fields and breaks the CLI.
                string existingJson = "{}";
                if (File.Exists(_default.ConfigFilePath))
                {
                    existingJson = await File.ReadAllTextAsync(_default.ConfigFilePath, ct).ConfigureAwait(false);
                }

                return JsonFileConfigMechanism<CommonConfig>.SerializeMergedWithExisting(existingJson, pinned, CommonConfigInfo);
            },
            CorruptError = ex => $"Common config at {_default.ConfigFilePath} is corrupt: {ex.Message}",
            LogFileNotFound = path => JsonCommonConfigStoreLog.ConfigFileNotFound(logger, path),
            LogNullConfig = path => JsonCommonConfigStoreLog.ConfigNullUsingDefaults(logger, path),
            LogParseFailed = (ex, path) => JsonCommonConfigStoreLog.ParseFailed(logger, ex, path),
            LogLoadFailed = (ex, path) => JsonCommonConfigStoreLog.LoadFailed(logger, ex, path),
            LogSaved = (path, _) => JsonCommonConfigStoreLog.ConfigSaved(logger, path),
            LogSaveFailed = (ex, path) => JsonCommonConfigStoreLog.SaveFailed(logger, ex, path),
        });
    }

    /// <inheritdoc />
    public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default) => _mechanism.LoadAsync(ct);

    /// <inheritdoc />
    public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default) => _mechanism.SaveAsync(config, ct);

    /// <inheritdoc />
    public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default) => _mechanism.UpdateAsync(updater, ct);
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

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="JsonCommonConfigStore" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class JsonCommonConfigStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Common config file not found at {Path}, using defaults")]
    public static partial void ConfigFileNotFound(ILogger logger, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Common config at {Path} deserialized to null, using defaults")]
    public static partial void ConfigNullUsingDefaults(ILogger logger, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to parse common config JSON at {Path}")]
    public static partial void ParseFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to load common config from {Path}")]
    public static partial void LoadFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Common config saved to {Path}")]
    public static partial void ConfigSaved(ILogger logger, string path);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to save common config to {Path}")]
    public static partial void SaveFailed(ILogger logger, Exception ex, string path);
}
