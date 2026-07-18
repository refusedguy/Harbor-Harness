// JsonAppConfigStore.cs — JSON-backed implementation of IAppConfigStore<T>.
//
// Persists per-app config to ~/.harbor/<ConfigFileName>.json using
// System.Text.Json. Writes are atomic (temp file + File.Move) and serialized
// via a SemaphoreSlim so concurrent callers don't truncate each other's writes.
// Reads fall back to the supplied default config when the file is missing or
// corrupt — never throws for expected IO failures.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.Configuration;

/// <summary>
///     JSON-backed <see cref="IAppConfigStore{T}"/>. Reads and writes the
///     per-app config file at <see cref="AppConfigBase.ConfigFilePath"/>.
/// </summary>
/// <typeparam name="T">The app-specific config record type.</typeparam>
/// <remarks>
///     <para>
///         <b>Atomic writes:</b> every <see cref="SaveAsync"/> writes to a
///         sibling <c>&lt;file&gt;.tmp</c> file then <see cref="File.Move"/>
///         (atomic on POSIX, atomic-replace on Windows) into place. A crash
///         mid-write leaves the previous file intact.
///     </para>
///     <para>
///         <b>Thread safety:</b> a single <see cref="SemaphoreSlim"/> guards
///         every Load/Save/Update, so concurrent calls are serialized.
///     </para>
///     <para>
///         <b>Missing file:</b> <see cref="LoadAsync"/> returns the default
///         instance passed at construction — apps boot with sane defaults
///         before the user has ever saved anything.
///     </para>
///     <para>
///         <b>Corrupt file:</b> if JSON deserialization fails, LoadAsync
///         returns <see cref="Result.IsFailure"/> with the parser error. The
///         caller (typically the composition root) decides whether to log +
///         fall back to defaults or surface the error to the user.
///     </para>
/// </remarks>
public sealed class JsonAppConfigStore<T> : IAppConfigStore<T> where T : AppConfigBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            // ImmutableList<T> has no built-in converter; this one round-trips
            // via List<T>. Without it, System.Text.Json throws
            // NotSupportedException on the RecentSessions property.
            ImmutableListConverter<string>.Instance
        }
    };

    private readonly T _default;
    private readonly ILogger<JsonAppConfigStore<T>> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    ///     Construct a JSON-backed store.
    /// </summary>
    /// <param name="defaultConfig">
    ///     The default config returned by <see cref="LoadAsync"/> when the file
    ///     is missing. Typically <c>new CliConfig()</c> / <c>new AvaloniaConfig()</c> / etc.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public JsonAppConfigStore(T defaultConfig, ILogger<JsonAppConfigStore<T>> logger)
    {
        _default = defaultConfig ?? throw new ArgumentNullException(nameof(defaultConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Result<T>> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = _default.ConfigFilePath;
            if (!File.Exists(path))
            {
                _logger.LogInformation("App config file not found at {Path}, using defaults (appId={AppId})",
                    path, _default.AppId);
                return Result.Success(_default);
            }

            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            T? config = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (config is null)
            {
                _logger.LogWarning("App config at {Path} deserialized to null, using defaults", path);
                return Result.Success(_default);
            }
            return Result.Success(config);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse app config JSON at {Path}", _default.ConfigFilePath);
            return Result.Failure<T>($"App config at {_default.ConfigFilePath} is corrupt: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load app config from {Path}", _default.ConfigFilePath);
            return Result.Failure<T>(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> SaveAsync(T config, CancellationToken ct = default)
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

            string json = JsonSerializer.Serialize(config, JsonOptions);
            string tempPath = path + ".tmp";

            // Write to temp file first, then atomically move into place. This
            // ensures a crash mid-write leaves the previous file intact.
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);

            _logger.LogDebug("App config saved to {Path} (appId={AppId})", path, config.AppId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save app config to {Path}", config.ConfigFilePath);
            return Result.Failure(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(Func<T, T> updater, CancellationToken ct = default)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));

        var loadResult = await LoadAsync(ct).ConfigureAwait(false);
        if (loadResult.IsFailure)
        {
            return loadResult;
        }

        T updated = updater(loadResult.Value);
        return await SaveAsync(updated, ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Minimal System.Text.Json converter for <see cref="ImmutableList{T}"/>.
///     System.Text.Json has no built-in immutable-collection support; this
///     converter round-trips via a mutable <see cref="List{T}"/> and calls
///     <see cref="ImmutableList.ToImmutableList{T}"/> / <see cref="ImmutableList{T}.Builder"/>.
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
            T? value = JsonSerializer.Deserialize<T>(ref reader, options);
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
        foreach (T item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }
        writer.WriteEndArray();
    }
}
