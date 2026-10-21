using System.Collections.Frozen;
using System.Text.Json.Serialization.Metadata;
using Harbor.Providers.OpenAiCompatible.Compat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Providers.OpenAiCompatible;
/// <summary>
///     Provider configuration loaded from JSON.
/// </summary>
/// <remarks>
///     <para>
///         #195 (immutability batch): immutable after load. Every property is
///         init-only, so object initializers and <see cref="System.Text.Json" />
///         deserialization keep compiling unchanged, but no holder can mutate a
///         registered config. Collection properties expose read-only views over
///         snapshots taken in <see cref="Create" /> —
///         aliasing a caller-owned <see cref="List{T}" /> or
///         <see cref="Dictionary{TKey, TValue}" /> can no longer mutate the
///         config behind a singleton client's back.
///     </para>
/// </remarks>
public sealed class ProviderConfig
{

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = OpenAiCompatibleJsonContext.Default
    };

    /// <summary>
    ///     Pre-resolved, AOT-safe metadata for the provider config. Pass to the
    ///     <c>JsonTypeInfo&lt;T&gt;</c> overloads — never to the reflection-based
    ///     generic-options overloads. Same options behind the info (Web +
    ///     comments/trailing-commas), so the wire shape and failure contract
    ///     are unchanged.
    /// </summary>
    private static readonly JsonTypeInfo<ProviderConfig> ProviderConfigInfo =
        (JsonTypeInfo<ProviderConfig>)JsonOptions.GetTypeInfo(typeof(ProviderConfig));
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string ApiType { get; init; } = "openai-compatible";
    public string? ApiVersion { get; init; }
    public string AuthType { get; init; } = "bearer";
    public string? AuthHeader { get; init; }
    public string? AuthEnvVar { get; init; }
    public string? ModelsUrl { get; init; }
    public int ModelsRefreshHours { get; init; } = 24;
    public string? ModelsPath { get; init; }
    public ModelMapping? ModelMapping { get; init; }
    public IReadOnlyList<ModelInfo>? Models { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, string>? Capabilities { get; init; }
    public int Timeout { get; init; } = 60;

    /// <summary>
    ///     §OOP-002 (RESOLVED): provider-specific request quirks (Strategy pattern).
    ///     Attached after load via <see cref="WithQuirks" /> (see
    ///     <see cref="ProviderCompatFlags.For" />); not deserialized from JSON.
    ///     May be <see langword="null" /> when the provider has no quirks —
    ///     the client treats null and empty identically.
    /// </summary>
    public IReadOnlyList<IProviderCompatFlag>? Quirks { get; init; }

    public ProviderId GetProviderId() => ProviderId.Create(Id);

    /// <summary>
    ///     Validating factory (#195): the Result-returning counterpart of the
    ///     object initializer for programmatic construction. Collections are
    ///     snapshotted, so later mutations of the caller's enumerables cannot
    ///     leak into the config.
    /// </summary>
    public static Result<ProviderConfig> Create(
        string id,
        string baseUrl,
        string displayName = "",
        string description = "",
        string apiType = "openai-compatible",
        string? apiVersion = null,
        string authType = "bearer",
        string? authHeader = null,
        string? authEnvVar = null,
        string? modelsUrl = null,
        int modelsRefreshHours = 24,
        string? modelsPath = null,
        ModelMapping? modelMapping = null,
        IEnumerable<ModelInfo>? models = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        IEnumerable<KeyValuePair<string, string>>? capabilities = null,
        int timeout = 60,
        IReadOnlyList<IProviderCompatFlag>? quirks = null)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Result.Failure<ProviderConfig>("Provider config is missing 'id'.");
        }

        if (string.IsNullOrEmpty(baseUrl))
        {
            return Result.Failure<ProviderConfig>("Provider config is missing 'baseUrl'.");
        }

        return Result.Success(new ProviderConfig
        {
            Id = id,
            BaseUrl = baseUrl,
            DisplayName = displayName,
            Description = description,
            ApiType = apiType,
            ApiVersion = apiVersion,
            AuthType = authType,
            AuthHeader = authHeader,
            AuthEnvVar = authEnvVar,
            ModelsUrl = modelsUrl,
            ModelsRefreshHours = modelsRefreshHours,
            ModelsPath = modelsPath,
            ModelMapping = modelMapping is null ? null : SealMapping(modelMapping),
            Models = models?.ToArray(),
            Headers = headers?.ToFrozenDictionary(StringComparer.Ordinal),
            Capabilities = capabilities?.ToFrozenDictionary(StringComparer.Ordinal),
            Timeout = timeout,
            Quirks = quirks,
        });
    }

    /// <summary>
    ///     Return a sealed copy of this config with <paramref name="quirks" />
    ///     attached. The registration code calls this once at provider-load
    ///     time instead of mutating <see cref="Quirks" /> post-registration.
    /// </summary>
    public ProviderConfig WithQuirks(IReadOnlyList<IProviderCompatFlag>? quirks) =>
        SealedWith(quirks);

    public static Result<ProviderConfig> LoadFromFile(string path) =>
        Result.Try(() => JsonSerializer.Deserialize(File.ReadAllText(path), ProviderConfigInfo))
            .MapError(ex => $"Failed to load provider config '{path}': {ex}")
            .Bind(Loaded);

    /// <summary>
    ///     Parse a config from an in-memory JSON payload (embedded resources,
    ///     test harnesses). Same options and validation contract as
    ///     <see cref="LoadFromFile" /> — keeps
    ///     <see cref="OpenAiCompatibleJsonContext" /> internal while still
    ///     AOT-safe (pre-resolved TypeInfo).
    /// </summary>
    public static Result<ProviderConfig> LoadFromJson(string json) =>
        Result.Try(() => JsonSerializer.Deserialize(json, ProviderConfigInfo))
            .MapError(ex => $"Failed to load provider config from JSON: {ex}")
            .Bind(Loaded);

    private static Result<ProviderConfig> Loaded(ProviderConfig? c)
    {
        if (c is null)
        {
            return Result.Failure<ProviderConfig>("Provider config deserialized to null.");
        }

        if (string.IsNullOrEmpty(c.Id))
        {
            return Result.Failure<ProviderConfig>("Provider config is missing 'id'.");
        }

        if (string.IsNullOrEmpty(c.BaseUrl))
        {
            return Result.Failure<ProviderConfig>("Provider config is missing 'baseUrl'.");
        }

        // #195 follow-up: STJ source-gen with init-only setters overwrites
        // property defaults for JSON-absent members (0/null instead of the
        // declared 60/24/"openai-compatible"/...). Rebuild through the Create
        // factory so absent stays default; explicit values (even 0/"") pass
        // through exactly as before.
        return Create(
            id: c.Id,
            baseUrl: c.BaseUrl,
            displayName: c.DisplayName ?? "",
            description: c.Description ?? "",
            apiType: c.ApiType ?? "openai-compatible",
            apiVersion: c.ApiVersion,
            authType: c.AuthType ?? "bearer",
            authHeader: c.AuthHeader,
            authEnvVar: c.AuthEnvVar,
            modelsUrl: c.ModelsUrl,
            modelsRefreshHours: c.ModelsRefreshHours == 0 ? 24 : c.ModelsRefreshHours,
            modelsPath: c.ModelsPath,
            modelMapping: c.ModelMapping,
            models: c.Models,
            headers: c.Headers,
            capabilities: c.Capabilities,
            timeout: c.Timeout == 0 ? 60 : c.Timeout,
            quirks: c.Quirks);
    }

    /// <summary>
    ///     Defensive snapshot: copy every collection so the returned config
    ///     owns its state. Deserializer- or caller-owned lists/dictionaries
    ///     aliased by the raw instance can no longer mutate the config.
    /// </summary>
    private ProviderConfig SealedWith(IReadOnlyList<IProviderCompatFlag>? quirks) => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = Description,
        BaseUrl = BaseUrl,
        ApiType = ApiType,
        ApiVersion = ApiVersion,
        AuthType = AuthType,
        AuthHeader = AuthHeader,
        AuthEnvVar = AuthEnvVar,
        ModelsUrl = ModelsUrl,
        ModelsRefreshHours = ModelsRefreshHours,
        ModelsPath = ModelsPath,
        ModelMapping = ModelMapping is null ? null : SealMapping(ModelMapping),
        Models = Models?.ToArray(),
        Headers = Freeze(Headers),
        Capabilities = Freeze(Capabilities),
        Timeout = Timeout,
        Quirks = quirks,
    };

    private static ModelMapping SealMapping(ModelMapping mapping) => new()
    {
        Id = mapping.Id,
        DisplayName = mapping.DisplayName,
        ContextWindow = mapping.ContextWindow,
        MaxOutputTokens = mapping.MaxOutputTokens,
        SupportsVision = mapping.SupportsVision,
        SupportsToolUse = mapping.SupportsToolUse,
        SupportsReasoning = mapping.SupportsReasoning,
        Pricing = Freeze(mapping.Pricing),
    };

    private static IReadOnlyDictionary<string, string>? Freeze(IReadOnlyDictionary<string, string>? source) =>
        source is null ? null : source.ToFrozenDictionary(StringComparer.Ordinal);
}

public sealed class ModelMapping
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? ContextWindow { get; init; }
    public string? MaxOutputTokens { get; init; }
    public string? SupportsVision { get; init; }
    public string? SupportsToolUse { get; init; }
    public string? SupportsReasoning { get; init; }
    public IReadOnlyDictionary<string, string>? Pricing { get; init; }
}

/// <summary>
///     Default auth resolver — CLI override → conventional env var
///     (<c>PROVIDER_ID</c> upper-cased with dashes replaced) → failure hint.
///     The env twin of the single <see cref = "Harbor.Abstractions.Providers.IAuthResolver" />
///     abstraction (ROP-A ПР.6): the former IOpenAIAuthResolver /
///     IAnthropicAuthResolver interfaces and their per-provider resolvers
///     collapsed into this pair.
/// </summary>
/// <summary>
///     Default auth resolver — CLI override → conventional env var
///     (<c>PROVIDER_ID</c> upper-cased with dashes replaced) → failure hint.
/// </summary>
public sealed class EnvVarAuthResolver : IAuthResolver
{
    private readonly ILogger<EnvVarAuthResolver> _logger;
    private readonly Dictionary<string, string> _overrides;

    public EnvVarAuthResolver(Dictionary<string, string>? overrides = null, ILogger<EnvVarAuthResolver>? logger = null)
    {
        _overrides = overrides ?? new Dictionary<string, string>();
        _logger = logger ?? NullLogger<EnvVarAuthResolver>.Instance;
    }

    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        // 1. Override (from CLI flag)
        if (_overrides.TryGetValue(providerId, out string? key) && !string.IsNullOrEmpty(key))
            return Task.FromResult(Result.Success(key));

        // 2. Conventional env var
        string envName = providerId.ToUpperInvariant().Replace("-", "_") + "_API_KEY";
        string? envValue = Environment.GetEnvironmentVariable(envName);

        return Task.FromResult(
            Result.SuccessIf(!string.IsNullOrEmpty(envValue),
                    $"API key not found. Set ${envName} or pass --{providerId}-api-key.")
                .Map(() => envValue!));
    }
}

/// <summary>
///     Model catalog — fetches and caches models list.
/// </summary>
public interface IModelCatalog
{
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default);
}

public sealed class DynamicModelCatalog : IModelCatalog
{
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly ILogger<DynamicModelCatalog> _logger;

    // #1932: the catalogue's freshness window is read through an injected
    // clock, not through DateTimeOffset.UtcNow. TimeProvider is the BCL clock
    // seam the product already uses (ProviderRegistry #1878, AgentLoop,
    // ToolDispatcher, RetryPolicy, MascotDirector), so the 24-hour TTL can be
    // aged in a test instead of by waiting a day.
    private readonly TimeProvider _time;

    /// <summary>
    ///     Construct the catalogue.
    /// </summary>
    /// <param name="http">Client the model list is fetched over.</param>
    /// <param name="cacheDir">Directory the cached catalogue lives in.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="timeProvider">
    ///     Clock source for the catalogue's freshness window (#1932). Production
    ///     passes nothing and reads <see cref="TimeProvider.System" />; a test
    ///     passes a manual clock so the cache can be aged on demand.
    /// </param>
    public DynamicModelCatalog(
        HttpClient http,
        string cacheDir,
        ILogger<DynamicModelCatalog> logger,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _cacheDir = cacheDir;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        if (!Directory.Exists(_cacheDir))
            Directory.CreateDirectory(_cacheDir);
    }

    public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default)
    {
        // Hardcoded models in config.
        //
        // #848: stamped from `config.Id`, NOT returned as the JSON spelled it.
        // This branch used to hand back `config.Models` verbatim, so the
        // `providerId` inside a provider JSON's `models` array was a second,
        // unchecked source of truth for "which provider am I" — while
        // `JsonProviderDiscovery` registered the same file under its `id`. A
        // gateway entry stamped with its upstream (`{ "id": "gateway",
        // "models": [ { ..., "providerId": "anthropic" } ] }`) therefore
        // answered to the registry key `gateway` and told every reader
        // `anthropic`: the prompt rendered `- Model: anthropic/…`,
        // ProviderModelPickerViewModel's `m.ProviderId == group.Id` filter hid
        // the model, and CompactionService resolved the wrong ILlmClient to
        // summarize with.
        //
        // `ParseModel` already stamps `config.Id` for the fetched path; this
        // makes the hardcoded path obey the same invariant. The file's `id` is
        // the one registry key, so the stamp is DERIVED here rather than
        // trusted — and `~/.harbor/providers/*.json` (user-writable, and
        // checked first) can no longer diverge from it.
        if (config.Models is { Count: > 0 })
            return Result.Success(StampWithOwnProvider(config.Models, config.Id));

        if (string.IsNullOrEmpty(config.ModelsUrl))
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Provider '{config.Id}' has no modelsUrl and no hardcoded models.");

        // ROP-A ПР.8 — canonical Compensate chain: fresh cache → network →
        // stale cache. Each fallback fires only when the previous source
        // failed; the headline error stays the FETCH failure, not a cache miss.
        string cachePath = Path.Combine(_cacheDir, $"{config.Id}.json");
        TimeSpan maxAge = TimeSpan.FromHours(config.ModelsRefreshHours);

        return await ReadCacheAsync(cachePath, config, ct, freshOnly: true, maxAge: maxAge)
            .Compensate(_ => FetchAndCacheAsync(config, config.ModelsUrl!, cachePath, ct))
            .Compensate(fetchError => ReadCacheAsync(cachePath, config, ct, freshOnly: false, maxAge: maxAge)
                .MapError(_ => fetchError))
            .ConfigureAwait(false);
    }

    /// <summary>Fetch the live model list and seed the cache.</summary>
    private async Task<Result<IReadOnlyList<ModelInfo>>> FetchAndCacheAsync(
        ProviderConfig config, string modelsUrl, string cachePath, CancellationToken ct)
    {
        // Result.Try (CSharpFunctionalExtensions 3.7.0) is the library form of
        // wrapping a throwing call in a Result. The parse step stays OUTSIDE
        // the Try and is chained with Bind: it has its own catch-all (see
        // ParseModelsResponse), so folding it in would double-report, and the
        // two failure texts differ (fetch vs parse).
        return await Result.Try(
                async () =>
                {
                    string response = await _http.GetStringAsync(modelsUrl, ct).ConfigureAwait(false);
                    Directory.CreateDirectory(_cacheDir);
                    await File.WriteAllTextAsync(cachePath, response, ct).ConfigureAwait(false);

                    // #1932: the file's freshness mark is the PRODUCT's clock,
                    // not the filesystem's. The reader below compares the same
                    // clock against that mark, so the entry this path writes
                    // expires for the reader exactly `maxAge` after it was
                    // written — not "24 hours after whatever the filesystem
                    // felt like when it stamped the file".
                    StampFreshness(cachePath);
                    return response;
                },
                ex =>
                {
                    DynamicModelCatalogLog.FetchModelsFailed(_logger, ex, config.Id);
                    return ex.Message;
                })
            .Bind(response => ParseModelsResponse(response, config))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Read the cached model list. <paramref name="freshOnly" /> enforces
    ///     the <paramref name="maxAge" /> window (a missing or aged-out cache is
    ///     a MISS, reported distinctly from a corrupt cache).
    /// </summary>
    private async Task<Result<IReadOnlyList<ModelInfo>>> ReadCacheAsync(
        string path, ProviderConfig config, CancellationToken ct, bool freshOnly, TimeSpan maxAge)
    {
        if (freshOnly)
        {
            if (!File.Exists(path))
                return Result.Failure<IReadOnlyList<ModelInfo>>("no cached model catalog");

            // #1932: "now" comes from the injected clock, so the window is
            // testable without waiting a day. "Written at" is the file's own
            // freshness mark — stamped by the fetch path from the same clock,
            // so the two ends of the window are one clock's opinion.
            DateTimeOffset writtenAt = new(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (_time.GetUtcNow() - writtenAt >= maxAge)
                return Result.Failure<IReadOnlyList<ModelInfo>>("cached model catalog is stale");
        }

        // Result.Try + Bind, mirroring FetchAndCacheAsync: the read is the
        // fallible I/O step, the parse keeps its own catch-all and its own
        // failure text.
        return await Result.Try(
                () => File.ReadAllTextAsync(path, ct),
                ex => $"Cache read failed: {ex.Message}")
            .Bind(json => ParseModelsResponse(json, config))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Stamp the cache file's freshness mark from the injected clock
    ///     (#1932).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The catalogue's TTL has two ends: the instant the entry was
    ///         written, and the instant it is read. Before #1932 the first end
    ///         was the FILESYSTEM's clock (the file's own last-write time) and
    ///         the second was the wall clock — two clocks that only ever
    ///         agreed by accident, and a seam that could be pulled from one
    ///         side only. Writing the mark from the same clock the reader uses
    ///         makes the window one clock's, on both ends.
    ///     </para>
    ///     <para>
    ///         BEST-EFFORT: a filesystem that refuses the stamp keeps its own
    ///         write time, which in production is the same instant (both are
    ///         the system clock) — so the TTL still means what it meant. A
    ///         failed stamp must never fail the fetch that produced a good
    ///         catalogue.
    ///     </para>
    /// </remarks>
    private void StampFreshness(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or ArgumentOutOfRangeException)
        {
            DynamicModelCatalogLog.FreshnessStampFailed(_logger, ex, path);
        }
    }

    /// <summary>
    ///     Parse a models-list response through <c>JsonElement</c> traversal.
    ///     The <c>ModelsPath</c> (and per-field mapping paths) are per-provider
    ///     dotted JSON keys, so no static record can serve them — the traversal
    ///     stays dynamic by design. JSON keys are not CLR members (arch-gate
    ///     TRIM-MAY-NOT-RESOLVE-A-CLR-MEMBER-BY-A-STRING-NAME ignores them),
    ///     and no <c>JsonSerializer</c> call is involved here. Cold leg by
    ///     design (one parse per fetch/cache-read, never per-turn/per-frame).
    /// </summary>
    private Result<IReadOnlyList<ModelInfo>> ParseModelsResponse(string json, ProviderConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = root;
            if (!string.IsNullOrEmpty(config.ModelsPath))
            {
                foreach (string part in config.ModelsPath.Split('.'))
                {
                    if (!modelsArray.TryGetProperty(part, out var next))
                        return Result.Failure<IReadOnlyList<ModelInfo>>($"Path '{config.ModelsPath}' not found in response.");
                    modelsArray = next;
                }
            }
            else if (root.TryGetProperty("data", out var data))
            {
                modelsArray = data;
            }
            else if (root.TryGetProperty("models", out var models))
            {
                modelsArray = models;
            }

            if (modelsArray.ValueKind != JsonValueKind.Array)
                return Result.Failure<IReadOnlyList<ModelInfo>>("Models response is not an array.");

            var mapping = config.ModelMapping ?? new ModelMapping();
            var result = new List<ModelInfo>();

            // Skip accounting (#1581): a corrupted catalog entry must not
            // silently shorten the returned list. Every entry that fails to
            // parse — either a normal Failure from ParseModel or an unexpected
            // exception — is counted here with its index and reason, and the
            // accounting below decides how the skips surface.
            var skips = new List<string>();
            int total = modelsArray.GetArrayLength();

            for (int i = 0; i < total; i++)
            {
                try
                {
                    var parsed = ParseModel(modelsArray[i], config, mapping);
                    if (parsed.IsSuccess)
                        result.Add(parsed.Value);
                    else
                        skips.Add($"[{i}] {parsed.Error}");
                }
                catch (Exception ex)
                {
                    DynamicModelCatalogLog.ParseModelFailed(_logger, ex, i);
                    skips.Add($"[{i}] {ex.Message}");
                }
            }

            if (skips.Count == 0)
                return Result.Success<IReadOnlyList<ModelInfo>>(result);

            string reasons = string.Join("; ", skips);

            // Every entry was skipped: returning an empty Success would send
            // "Model 'x' not found in provider catalog" downstream and bury the
            // real cause in the log. Keep the true cause on the Result rail.
            if (result.Count == 0)
                return Result.Failure<IReadOnlyList<ModelInfo>>(
                    $"Failed to parse models response: all {total} model entries were skipped — {reasons}");

            DynamicModelCatalogLog.ModelsSkipped(_logger, config.Id, skips.Count, total, reasons);
            return Result.Success<IReadOnlyList<ModelInfo>>(result);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ModelInfo>>($"Failed to parse models response: {ex.Message}");
        }
    }

    /// <summary>
    ///     Re-stamp a hardcoded catalog with the id of the config that carries
    ///     it (#848).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A catalog is fetched under exactly one registry key, so every
    ///         entry it returns must carry that key — see the call site for what
    ///         a divergent stamp costs. The JSON's own <c>providerId</c> is not a
    ///         second source of truth for "which provider am I"; the file's
    ///         <c>id</c> is, because that is what
    ///         <c>JsonProviderDiscovery</c> registers the file under.
    ///     </para>
    ///     <para>
    ///         ALLOCATION: the list is returned as-is when every entry already
    ///         agrees, which is the case for every catalog the bundled provider
    ///         JSONs ship today. Only a diverging entry allocates a replacement
    ///         <see cref="ModelInfo" /> (and the array holding it), so the fix
    ///         costs nothing on the happy path and cannot be defeated by an
    ///         unrelated member being rewritten — <c>with</c> copies the record
    ///         and changes one member.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<ModelInfo> StampWithOwnProvider(IReadOnlyList<ModelInfo> models, string providerId)
    {
        List<ModelInfo>? restamped = null;

        for (int i = 0; i < models.Count; i++)
        {
            ModelInfo model = models[i];

            if (restamped is null)
            {
                // Ordinal, not case-insensitive: a registry lookup is ordinal
                // (`ProviderId.Create`, the `seenIds` HashSet in
                // JsonProviderDiscovery), so "OpenRouter" is NOT the id
                // "openrouter" is registered under, and passing it off as
                // agreeing would bless a provider string nothing resolves.
                if (string.Equals(model.ProviderId, providerId, StringComparison.Ordinal))
                {
                    continue;
                }

                // First divergence: start the replacement, carrying every
                // entry before it verbatim.
                restamped = CopyUpTo(models, i);
            }

            // Past this point every entry is appended, whether it agreed or
            // not — an agreeing entry that FOLLOWS a divergent one still has
            // to reach the caller, or the fix would silently shorten the
            // catalog.
            restamped.Add(
                string.Equals(model.ProviderId, providerId, StringComparison.Ordinal)
                    ? model
                    : model with { ProviderId = providerId });
        }

        return (IReadOnlyList<ModelInfo>?)restamped ?? models;
    }

    /// <summary>
    ///     A fresh list holding entries <c>[0, count)</c> of <paramref name="source" />
    ///     verbatim — used to build the replacement lazily, so a catalog that
    ///     needs no re-stamping allocates nothing.
    /// </summary>
    private static List<ModelInfo> CopyUpTo(IReadOnlyList<ModelInfo> source, int count)
    {
        var copy = new List<ModelInfo>(source.Count);
        for (int i = 0; i < count; i++)
        {
            copy.Add(source[i]);
        }

        return copy;
    }

    /// <summary>
    ///     Parse one models-array entry onto the Result rail (#1581): a record
    ///     without an <c>id</c> is a <see cref="Result" /> failure carrying the
    ///     reason, not a silent <c>null</c>. The caller counts failures and
    ///     reports them, so a corrupted entry surfaces in the catalog
    ///     diagnostics instead of shortening the list by one with no trace.
    /// </summary>
    private static Result<ModelInfo> ParseModel(JsonElement item, ProviderConfig config, ModelMapping mapping)
    {
        string? id = GetStringField(item, mapping.Id ?? "id");
        if (string.IsNullOrEmpty(id))
            return Result.Failure<ModelInfo>("model entry has no 'id' (or it is null)");

        string displayName = GetStringField(item, mapping.DisplayName ?? "name") ?? id;
        int contextWindow = GetIntField(item, mapping.ContextWindow ?? "context_length") ?? 4096;
        int maxOutput = GetIntField(item, mapping.MaxOutputTokens ?? "max_output_tokens") ?? 4096;

        return Result.Success(new ModelInfo(
            id,
            config.Id,
            displayName,
            contextWindow,
            maxOutput,
            false,
            false,
            true,
            Pricing.Unknown,
            "openai"));
    }

    private static Maybe<JsonElement> Navigate(JsonElement item, string path) =>
        path.Split('.').Aggregate(
            Maybe.From(item),
            static (acc, part) => acc.Bind(cur =>
                cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var next)
                    ? Maybe.From(next)
                    : Maybe<JsonElement>.None));

    private static string? GetStringField(JsonElement item, string path) =>
        Navigate(item, path).Match(
            static e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText(),
            static () => (string?)null);

    private static int? GetIntField(JsonElement item, string path) =>
        Navigate(item, path).Bind(static e => e.ValueKind switch
        {
            JsonValueKind.Number => Maybe.From((int)e.GetInt64()),
            JsonValueKind.String when int.TryParse(e.GetString(), out int i) => Maybe.From(i),
            _ => Maybe<int>.None,
        }).Match(static v => (int?)v, static () => (int?)null);
}

/// <summary>
///     SG7: BCL <c>[LoggerMessage]</c> delegates for <see cref="DynamicModelCatalog" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class DynamicModelCatalogLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch models for {Provider}, trying stale cache")]
    public static partial void FetchModelsFailed(ILogger logger, Exception ex, string provider);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to parse model entry {Index} from models response")]
    public static partial void ParseModelFailed(ILogger logger, Exception ex, int index);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Provider {Provider}: skipped {Skipped} of {Total} model entries. Reasons: {Reasons}")]
    public static partial void ModelsSkipped(ILogger logger, string provider, int skipped, int total, string reasons);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Could not stamp the catalogue freshness mark on '{Path}'; the file keeps its own write time")]
    public static partial void FreshnessStampFailed(ILogger logger, Exception ex, string path);
}
