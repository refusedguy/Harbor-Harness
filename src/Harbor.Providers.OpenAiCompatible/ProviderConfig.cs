using System.Collections.Frozen;
using System.Text.Json.Serialization.Metadata;
using Harbor.Providers.OpenAiCompatible.Compat;
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
