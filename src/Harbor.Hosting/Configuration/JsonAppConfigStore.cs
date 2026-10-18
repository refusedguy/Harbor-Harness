// JsonAppConfigStore.cs — JSON-backed implementation of IAppConfigStore<T>.
//
// #534: this type MOVED here from Harbor.Desktop.Abstractions, which is
// IsPackable / `PackageId: Harbor.Desktop.Abstractions`.
// The layer matrix calls it Presentation (§2), and has said so since the matrix
// row was created (5d2df19f). A published Presentation package that writes the
// user's config is a storage engine that also ships a schema, and
// PresentationCapabilityRules forbids `System.IO.File*` in every Presentation
// assembly anyway. The generic port, AppConfigBase and CompositeConfig<T> stayed
// in the leaf; only the persistence moved. See JsonCommonConfigStore.cs for the
// full note and DesktopAbstractionsLeafTakesNoIoRules for the guard.
//
// Persists per-app config to ~/.harbor/<ConfigFileName>.json. Writes are
// atomic (temp file + File.Move) and serialized via a SemaphoreSlim so
// concurrent callers don't truncate each other's writes. Reads fall back to
// the supplied default config when the file is missing or corrupt — never
// throws for expected IO failures.
//
// AOT note: because the concrete T (CliConfig, AvaloniaConfig, …) is defined
// in each app's own assembly, this store cannot source-generate metadata for
// it. Pass a source-generated JsonTypeInfo<T> — from a JsonSerializerContext
// declared next to the app's config record, as CliJsonContext and
// AvaloniaJsonContext now do — via the constructor.
//
// #414: there used to be a third option. Omit the argument and the store called
// JsonSerializer.Deserialize<T>(json, JsonOptions), resolving the contract by
// reflection, warning once at startup, and carrying on. That is the exact shape
// this audit is about: a path that allocates nothing, measures clean, and throws
// InvalidOperationException the moment the publish is trimmed, because
// JsonSerializer.IsReflectionEnabledByDefault is off whenever the trimmer runs.
// Both product call sites omitted the argument, so it was the live path, not a
// fallback nobody reached. It is gone; omitting the argument is now an immediate
// named exception instead of a deferred one.
//
// #1593 (Bridge): the file pipeline (exists-probe, ReadAllTextAsync,
// corrupt→failure mapping, ensure-directory, serialized atomic write — the
// skeleton this file used to duplicate from JsonCommonConfigStore) lives in
// JsonFileConfigMechanism, shared by both stores. What is left here is the
// FORM — the parts that belong to the per-app config shape:
// - the caller-supplied source-generated JsonTypeInfo<T> (#414: no reflection
//   fallback exists; omitting it is an immediate, named construction failure),
// - payload-derived save path (config.ConfigFilePath — deliberately UNlike
//   the common store's #913 store-owned path, and unchanged),
// - this store's SG4 log wording.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpFunctionalExtensions;
using Harbor.Desktop.Abstractions.Configuration;
namespace Harbor.Hosting.Configuration;
/// <summary>
///     JSON-backed <see cref="IAppConfigStore{T}" />. Reads and writes the
///     per-app config file at <see cref="AppConfigBase.ConfigFilePath" />.
/// </summary>
/// <typeparam name="T">The app-specific config record type.</typeparam>
/// <remarks>
///     <para>
///         <b>Atomic writes:</b> every <see cref="SaveAsync" /> writes to a
///         sibling <c>&lt;file&gt;.tmp</c> file then <see cref="File.Move" />
///         (atomic on POSIX, atomic-replace on Windows) into place. A crash
///         mid-write leaves the previous file intact.
///     </para>
///     <para>
///         <b>Thread safety:</b> a single <see cref="SemaphoreSlim" /> guards
///         every Load/Save/Update, so concurrent calls are serialized.
///     </para>
///     <para>
///         <b>Missing file:</b> <see cref="LoadAsync" /> returns the default
///         instance passed at construction — apps boot with sane defaults
///         before the user has ever saved anything.
///     </para>
///     <para>
///         <b>Corrupt file:</b> if JSON deserialization fails, LoadAsync
///         returns <see cref="Result.IsFailure" /> with the parser error. The
///         caller (typically the composition root) decides whether to log +
///         fall back to defaults or surface the error to the user.
///     </para>
/// </remarks>
public sealed class JsonAppConfigStore<T> : IAppConfigStore<T> where T : AppConfigBase
{
    // #414: JsonOptions is gone. It existed only to feed the reflection
    // overloads, and the whole point of the JsonTypeInfo constructor argument is
    // that the options object is not how the contract is chosen. Its
    // ImmutableList<string> converter was redundant anyway — System.Text.Json has
    // supported IImmutableList<T> for both directions since .NET 8, and the
    // comment claiming otherwise predates that.

    private readonly T _default;
    private readonly JsonFileConfigMechanism<T> _mechanism;

    /// <summary>
    ///     Non-null by construction. The parameter is nullable so the "no contract
    ///     supplied" case has something to receive, but the constructor throws
    ///     before it gets here, so the field is declared non-nullable and every
    ///     call site gets CS8604 if that ever stops being true. (CI caught exactly
    ///     that: leaving the field nullable made the two call sites below fail to
    ///     compile with CS8604, because a constructor's null check does not narrow
    ///     a field in a different method.)
    /// </summary>
    private readonly JsonTypeInfo<T> _jsonTypeInfo;

    /// <summary>
    ///     Construct a JSON-backed store.
    /// </summary>
    /// <param name="defaultConfig">
    ///     The default config returned by <see cref="LoadAsync" /> when the file
    ///     is missing. Typically <c>new CliConfig()</c> / <c>new AvaloniaConfig()</c> / etc.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="jsonTypeInfo">
    ///     Source-generated metadata for <typeparamref name="T" />, from a
    ///     <see cref="JsonSerializerContext" /> declared next to the app's config
    ///     record (<c>MyAppJsonContext.Default.CliConfig</c>). This parameter is
    ///     optional in the signature only so that callers outside CI keep
    ///     compiling; omitting it THROWS, because there is no reflection fallback
    ///     left to take (#414). If <typeparamref name="T" /> has immutable-collection
    ///     properties, build the type info from options that register converters
    ///     (see <c>ConfigJson.Options</c>) or rely on the built-in
    ///     <c>IImmutableList&lt;T&gt;</c> support, which has covered both
    ///     directions since .NET 8.
    /// </param>
    public JsonAppConfigStore(
        T defaultConfig,
        ILogger<JsonAppConfigStore<T>> logger,
        JsonTypeInfo<T>? jsonTypeInfo = null)
    {
        _default = defaultConfig ?? throw new ArgumentNullException(nameof(defaultConfig));
        if (logger is null) throw new ArgumentNullException(nameof(logger));

        // #414: there is no reflection fallback any more. It used to be
        // `_jsonTypeInfo is not null ? <generated> : JsonSerializer.Deserialize<T>(
        // json, JsonOptions)` — a branch that allocates nothing, works on JIT, and
        // throws InvalidOperationException under PublishTrimmed because
        // JsonSerializer.IsReflectionEnabledByDefault is off whenever the trimmer
        // runs. BOTH product call sites (HostBuilder.AddCliConfiguration and
        // ConfigRegistration, twice) omitted the argument, so that was the path
        // every launch took and the warning below fired every launch.
        //
        // The parameter stays optional so the signature is unchanged for callers
        // that are not compiled by CI (contrib/apps/*), but omitting it is now an
        // immediate, named failure here rather than a crash discovered at the first
        // trimmed publish. Declaring a JsonSerializerContext next to the config
        // record is the fix, and the message says so.
        if (jsonTypeInfo is null)
        {
            throw new InvalidOperationException(
                $"JsonAppConfigStore<{typeof(T).Name}> was constructed without a source-generated "
                + "JsonTypeInfo, and this store no longer falls back to reflection-based "
                + "System.Text.Json. That fallback works on an untrimmed JIT build and throws "
                + "InvalidOperationException under PublishTrimmed, where "
                + "JsonSerializer.IsReflectionEnabledByDefault is false — so a working config "
                + "path today is a broken one at publish. Declare a JsonSerializerContext next to "
                + $"{typeof(T).Name} ([JsonSerializable(typeof({typeof(T).Name}))], with "
                + "JsonSourceGenerationOptions replicating JsonSerializerDefaults.Web: camelCase "
                + "and case-insensitive, so existing config files keep round-tripping) and pass "
                + "YourContext.Default." + typeof(T).Name + " as the third constructor argument.");
        }

        _jsonTypeInfo = jsonTypeInfo;

        // #1593: declare the FORM once; JsonFileConfigMechanism runs it.
        // Unlike JsonCommonConfigStore there is no #913 story here: the save
        // target stays payload-derived (config.ConfigFilePath), exactly as it
        // always was.
        _mechanism = new(new JsonFileConfigHooks<T>
        {
            Default = _default,
            LoadPath = () => _default.ConfigFilePath,
            SavePath = config => config.ConfigFilePath,
            // #414: no reflection branch. The JsonTypeInfo overload is the only call, so the
            // contract comes from generated metadata or not at all — see the constructor.
            Deserialize = json => JsonSerializer.Deserialize(json, _jsonTypeInfo),
            AfterLoad = config => config,
            // #414: as in LoadAsync, the only (de)serialization call is the
            // JsonTypeInfo one.
            Serialize = (config, _) => Task.FromResult(JsonSerializer.Serialize(config, _jsonTypeInfo)),
            CorruptError = ex => $"App config at {_default.ConfigFilePath} is corrupt: {ex.Message}",
            LogFileNotFound = path => JsonAppConfigStoreLog.ConfigFileNotFound(logger, path, _default.AppId),
            LogNullConfig = path => JsonAppConfigStoreLog.ConfigNullUsingDefaults(logger, path),
            LogParseFailed = (ex, path) => JsonAppConfigStoreLog.ParseFailed(logger, ex, path),
            LogLoadFailed = (ex, path) => JsonAppConfigStoreLog.LoadFailed(logger, ex, path),
            LogSaved = (path, config) => JsonAppConfigStoreLog.ConfigSaved(logger, path, config.AppId),
            LogSaveFailed = (ex, path) => JsonAppConfigStoreLog.SaveFailed(logger, ex, path),
        });
    }

    /// <inheritdoc />
    public Task<Result<T>> LoadAsync(CancellationToken ct = default) => _mechanism.LoadAsync(ct);

    /// <inheritdoc />
    public Task<Result> SaveAsync(T config, CancellationToken ct = default) => _mechanism.SaveAsync(config, ct);

    /// <inheritdoc />
    public Task<Result> UpdateAsync(Func<T, T> updater, CancellationToken ct = default) => _mechanism.UpdateAsync(updater, ct);
}

/// <summary>
///     Minimal System.Text.Json converter for <see cref="ImmutableList{T}" />.
///     System.Text.Json has no built-in immutable-collection support; this
///     converter round-trips via a mutable <see cref="List{T}" /> and calls
///     <see cref="ImmutableList.ToImmutableList{T}" /> / <see cref="ImmutableList{T}.Builder" />.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
internal sealed class ImmutableListConverter<T> : JsonConverter<ImmutableList<T>>
{
    /// <summary>Singleton instance — the converter is stateless.</summary>
    public static readonly ImmutableListConverter<T> Instance = new();

    private ImmutableListConverter() { }

    /// <inheritdoc />
    public override ImmutableList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return ImmutableList<T>.Empty;
        }
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected StartArray token for ImmutableList<{typeof(T).Name}>, got {reader.TokenType}.");
        }

        var list = new List<T>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }
            var value = JsonSerializer.Deserialize<T>(ref reader, options);
            list.Add(value!);
        }
        return list.ToImmutableList();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ImmutableList<T> value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }
        writer.WriteEndArray();
    }
}

/// <summary>
///     SG4: BCL <c>[LoggerMessage]</c> delegates for <see cref="JsonAppConfigStore{T}" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class JsonAppConfigStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "App config file not found at {Path}, using defaults (appId={AppId})")]
    public static partial void ConfigFileNotFound(ILogger logger, string path, string appId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "App config at {Path} deserialized to null, using defaults")]
    public static partial void ConfigNullUsingDefaults(ILogger logger, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to parse app config JSON at {Path}")]
    public static partial void ParseFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to load app config from {Path}")]
    public static partial void LoadFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "App config saved to {Path} (appId={AppId})")]
    public static partial void ConfigSaved(ILogger logger, string path, string appId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to save app config to {Path}")]
    public static partial void SaveFailed(ILogger logger, Exception ex, string path);
}
