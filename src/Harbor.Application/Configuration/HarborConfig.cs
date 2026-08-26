using System.Text.Json.Serialization;
namespace Harbor.Application.Configuration;
/// <summary>
///     Harbor application configuration.
///     Stored at ~/.harbor/config.json. No env vars required.
/// </summary>
/// <remarks>
///     <para>
///         The config is composed of self-validating sections (see
///         <see cref="IdentityConfig" />, <see cref="ToolingConfig" />, etc.).
///         The flat string properties (<see cref="Provider" />, <see cref="Model" />,
///         <see cref="Agent" />, …) are kept for backward compatibility with the
///         many call sites and are computed/written through the typed sections.
///     </para>
///     <para>
///         Legacy fields written by the desktop <c>CommonConfig</c> writer
///         (<c>defaultProvider</c>, <c>defaultModel</c>, <c>onboardingCompleted</c>, …)
///         are normalised once during load (see <see cref="ConfigNormalizer" />),
///         never stored as duplicate public state.
///     </para>
/// </remarks>
public sealed class HarborConfig
{
    /// <summary>Provider / model / agent selection.</summary>
    public IdentityConfig Identity { get; set; } = IdentityConfig.Default;

    /// <summary>UI + storage presentation preferences.</summary>
    public PresentationConfig Ui { get; set; } = PresentationConfig.Default;

    /// <summary>Plugin + builtin-tool toggles.</summary>
    public ToolingConfig Tooling { get; set; } = ToolingConfig.Default;

    /// <summary>Cost guardrail (USD). 0 = no limit.</summary>
    public CostConfig Cost { get; set; } = CostConfig.Default;

    /// <summary>Compaction tuning.</summary>
    public CompactionConfig Compaction { get; set; } = CompactionConfig.Default;

    /// <summary>
    ///     Ф8/A3: optional cheap model reference (<c>"provider/model"</c>) used
    ///     for background summarization (compaction). Empty/null → the primary
    ///     model summarizes (previous behavior).
    /// </summary>
    public string? SecondaryModel { get; set; }

    /// <summary>Run limits (max steps per agent run).</summary>
    public RunLimitsConfig Run { get; set; } = RunLimitsConfig.Default;

    /// <summary>API keys per provider.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    /// <summary>Custom provider configs (path to JSON or inline).</summary>
    public Dictionary<string, ProviderConfigEntry> Providers { get; set; } = new();

    /// <summary>Effective provider ID.</summary>
    [JsonIgnore]
    public string Provider
    {
        get => Identity.Provider?.Value ?? IdentityConfig.FallbackProvider;
        set
        {
            var r = ProviderId.TryCreate(value);
            Identity = r.IsSuccess
                ? Identity with { Provider = r.Value }
                : Identity with { Provider = null };
        }
    }

    /// <summary>Effective model ID (provider/model form).</summary>
    [JsonIgnore]
    public string Model
    {
        get => Identity.Model?.ToString() ?? IdentityConfig.FallbackModel;
        set
        {
            var r = ModelRef.TryParse(value);
            Identity = r.IsSuccess
                ? Identity with { Model = r.Value }
                : Identity with { Model = null };
        }
    }

    /// <summary>Effective agent (mode): code, plan, explore.</summary>
    [JsonIgnore]
    public string Agent
    {
        get => Identity.Agent?.Value ?? IdentityConfig.FallbackAgent;
        set
        {
            var r = AgentName.TryCreate(value);
            Identity = r.IsSuccess
                ? Identity with { Agent = r.Value }
                : Identity with { Agent = null };
        }
    }

    /// <summary>TUI renderer: ansi, plain, spectre.</summary>
    [JsonIgnore]
    public string Tui
    {
        get => Ui.Tui;
        set => Ui = Ui with { Tui = value };
    }

    /// <summary>Storage backend: jsonl, memory, sqlite.</summary>
    [JsonIgnore]
    public string Storage
    {
        get => Ui.Storage;
        set => Ui = Ui with { Storage = value };
    }

    /// <summary>Whether onboarding has been completed.</summary>
    [JsonIgnore]
    public bool Onboarded
    {
        get => Ui.Onboarded;
        set => Ui = Ui with { Onboarded = value };
    }

    /// <summary>Enabled plugins.</summary>
    [JsonIgnore]
    public List<string> EnabledPlugins
    {
        get => Tooling.EnabledPlugins.ToList();
        set => Tooling = Tooling with { EnabledPlugins = (value ?? new List<string>()).AsReadOnly() };
    }

    /// <summary>Disabled builtin tools.</summary>
    [JsonIgnore]
    public List<string> DisabledTools
    {
        get => Tooling.DisabledTools.ToList();
        set => Tooling = Tooling with { DisabledTools = (value ?? new List<string>()).AsReadOnly() };
    }

    /// <summary>Default max steps per agent run.</summary>
    [JsonIgnore]
    public int MaxSteps
    {
        get => Run.MaxSteps;
        set => Run = Run with { MaxSteps = value };
    }

    /// <summary>Cost limit per session (USD). 0 = no limit.</summary>
    [JsonIgnore]
    public decimal CostLimit
    {
        get => Cost.Limit;
        set => Cost = Cost with { Limit = value };
    }

    /// <summary>
    ///     Effective provider — the selected provider or the built-in default.
    /// </summary>
    [JsonIgnore]
    public string EffectiveProvider => Identity.EffectiveProvider.Value;

    /// <summary>
    ///     Effective model — the selected model or the provider default.
    ///     Falls back to the built-in default when neither resolves.
    /// </summary>
    [JsonIgnore]
    public string EffectiveModel => Identity.EffectiveModel().Value.ToString();

    /// <summary>
    ///     Returns a default <see cref="HarborConfig" /> (kilocode provider,
    ///     tencent/hy3:free model, code agent, ansi TUI, jsonl storage).
    /// </summary>
    public static HarborConfig Default => new();

    /// <summary>
    ///     Projects the composed sections back into the canonical persisted
    ///     <see cref="RawConfigDto" /> shape (flat fields + legacy aliases
    ///     for cross-app compatibility). Used by <see cref="JsonConfigStore" />
    ///     so a reload reads the same fields back via <see cref="ConfigNormalizer" />.
    /// </summary>
    public RawConfigDto ToRaw() => new()
    {
        Provider = Identity.Provider?.Value,
        Model = Identity.Model?.ToString(),
        Agent = Identity.Agent?.Value,
        Tui = Ui.Tui,
        Storage = Ui.Storage,
        Onboarded = Ui.Onboarded,
        // Persist the ConsoleEx section (nested under `ui`) only when it
        // diverges from defaults — keeps config.json free of knob noise for
        // users who never touched it.
        Ui = Ui.ConsoleEx == ConsoleExUiConfig.Default ? null : new UiRawDto { ConsoleEx = Ui.ConsoleEx },
        DefaultProvider = Identity.Provider?.Value,
        DefaultModel = Identity.Model?.ToString(),
        OnboardingCompleted = Ui.Onboarded,
        StorageBackend = Ui.Storage,
        LogLevelConfig = null,
        ApiKeys = ApiKeys,
        Providers = Providers,
        EnabledPlugins = Tooling.EnabledPlugins.ToList(),
        DisabledTools = Tooling.DisabledTools.ToList(),
        MaxSteps = Run.MaxSteps,
        CostLimit = Cost.Limit,
        Compaction = Compaction,
        SecondaryModel = SecondaryModel
    };

    /// <summary>
    ///     Validate every section. Aggregates all errors into a single failure
    ///     message so the config store can surface them instead of silently
    ///     falling back to defaults.
    /// </summary>
    /// <remarks>
    ///     ROP-B П.14: the manual errors[] + join aggregation is exactly
    ///     <see cref="Result.Combine(IEnumerable{Result})" /> — every failure
    ///     joined with "; " by the standard combinator, so adding a section
    ///     cannot let the join format drift.
    /// </remarks>
    public Result<HarborConfig> Validate()
    {
        var sections = new List<Result>(capacity: 6 + Providers.Count)
        {
            Identity.EffectiveModel(),
            Tooling.Validate(),
            Cost.Validate(),
            Compaction.Validate(),
            Ui.Validate(),
            Run.Validate()
        };

        // Cold path (config load) — plain enumeration is fine here.
        foreach (var entry in Providers.Values)
        {
            sections.Add(entry.Validate());
        }

        return Result.Combine(sections).Map(() => this);
    }
}

/// <summary>
///     Raw DTO for the persisted config.json shape. Allows the normalizer to
///     read both the canonical fields and the legacy <c>CommonConfig</c> aliases
///     (defaultProvider / defaultModel / onboardingCompleted / storageBackend / …)
///     without polluting the public <see cref="HarborConfig" /> API.
/// </summary>
public sealed class RawConfigDto
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("agent")] public string? Agent { get; set; }
    [JsonPropertyName("tui")] public string? Tui { get; set; }
    [JsonPropertyName("storage")] public string? Storage { get; set; }
    [JsonPropertyName("onboarded")] public bool? Onboarded { get; set; }

    /// <summary>Nested UI section (<c>ui.consoleEx</c>) — the canonical shape.</summary>
    [JsonPropertyName("ui")] public UiRawDto? Ui { get; set; }

    /// <summary>Legacy root-level alias for <c>ui.consoleEx</c> — still read, no longer written.</summary>
    [JsonPropertyName("consoleEx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConsoleExUiConfig? ConsoleEx { get; set; }

    [JsonPropertyName("defaultProvider")] public string? DefaultProvider { get; set; }
    [JsonPropertyName("defaultModel")] public string? DefaultModel { get; set; }
    [JsonPropertyName("onboardingCompleted")] public bool? OnboardingCompleted { get; set; }
    [JsonPropertyName("storageBackend")] public string? StorageBackend { get; set; }
    [JsonPropertyName("logLevel")] public string? LogLevelConfig { get; set; }

    [JsonPropertyName("apiKeys")] public Dictionary<string, string>? ApiKeys { get; set; }
    [JsonPropertyName("providers")] public Dictionary<string, ProviderConfigEntry>? Providers { get; set; }
    [JsonPropertyName("enabledPlugins")] public List<string>? EnabledPlugins { get; set; }
    [JsonPropertyName("disabledTools")] public List<string>? DisabledTools { get; set; }
    [JsonPropertyName("maxSteps")] public int? MaxSteps { get; set; }
    [JsonPropertyName("costLimit")] public decimal? CostLimit { get; set; }
    [JsonPropertyName("compaction")] public CompactionConfig? Compaction { get; set; }
    [JsonPropertyName("secondaryModel")] public string? SecondaryModel { get; set; }
}

/// <summary>
///     Nested <c>ui</c> section of config.json. Currently carries only the
///     ConsoleEx renderer knobs; future UI preferences land here instead of
///     growing new root-level keys.
/// </summary>
public sealed class UiRawDto
{
    [JsonPropertyName("consoleEx")] public ConsoleExUiConfig? ConsoleEx { get; set; }
}

/// <summary>
///     Normalizes a <see cref="RawConfigDto" /> (which may carry legacy
///     <c>CommonConfig</c> aliases) into a canonical <see cref="HarborConfig" />.
///     Centralises the dual-schema compatibility that used to be spread across
///     computed properties on <see cref="HarborConfig" />.
/// </summary>
public static class ConfigNormalizer
{
    /// <summary>
    ///     Normalize a raw config into a canonical <see cref="HarborConfig" />.
    /// </summary>
    /// <remarks>
    ///     ROP-B П.13: nullable default ladders (canonical field → legacy alias)
    ///     are expressed as Maybe chains (<c>From → Where → Or → AsNullable</c>);
    ///     mandatory parses are fail-fast preconditions over
    ///     <see cref="Result.FirstFailureOrSuccess" />, so an invalid section
    ///     reports its own error without a ladder of manual
    ///     <c>if (IsFailure) return Failure</c> passthroughs.
    /// </remarks>
    public static Result<HarborConfig> Normalize(RawConfigDto raw)
    {
        var config = new HarborConfig();

        // ── Identity: canonical "provider"/"model"/"agent" win; legacy
        // "defaultProvider"/"defaultModel" are only used as fallback. ──
        string? providerStr = FirstNonEmpty(raw.Provider, raw.DefaultProvider);
        string? modelStr = FirstNonEmpty(raw.Model, raw.DefaultModel);

        return Result.FirstFailureOrSuccess(
                SetProvider(config, providerStr),
                SetModel(config, modelStr),
                SetAgent(config, raw.Agent),
                SetSecondary(config, raw.SecondaryModel))
            .Map(() =>
            {
                ApplyPresentation(config, raw);
                ApplyTooling(config, raw);
                ApplyLimits(config, raw);
                return config;
            });
    }

    /// <summary>
    ///     The "first non-empty wins" nullable ladder as a Maybe chain
    ///     (ROP-B П.13 reference pattern): <c>From → Where → Or(lazy)</c>,
    ///     unfolded back into the nullable world at the boundary.
    /// </summary>
    private static string? FirstNonEmpty(string? primary, string? fallback)
    {
        var candidate = Maybe.From(primary)
            .Where(static s => !string.IsNullOrEmpty(s))
            .Or(() => Maybe.From(fallback).Where(static s => !string.IsNullOrEmpty(s)));

        return candidate.TryGetValue(out var value) ? value : null;
    }

    private static Result SetProvider(HarborConfig config, string? providerStr)
    {
        if (providerStr is null)
        {
            return Result.Success();
        }

        return ProviderId.TryCreate(providerStr)
            .Tap(id => config.Identity = config.Identity with { Provider = id });
    }

    private static Result SetModel(HarborConfig config, string? modelStr)
    {
        if (modelStr is null)
        {
            return Result.Success();
        }

        return ModelRef.TryParse(modelStr)
            .Tap(model => config.Identity = config.Identity with { Model = model });
    }

    private static Result SetAgent(HarborConfig config, string? agentStr)
    {
        if (string.IsNullOrEmpty(agentStr))
        {
            return Result.Success();
        }

        return AgentName.TryCreate(agentStr)
            .Tap(agent => config.Identity = config.Identity with { Agent = agent });
    }

    private static Result SetSecondary(HarborConfig config, string? secondaryStr)
    {
        if (string.IsNullOrEmpty(secondaryStr))
        {
            return Result.Success();
        }

        return ModelRef.TryParse(secondaryStr)
            .Tap(_ => config.SecondaryModel = secondaryStr);
    }

    private static void ApplyPresentation(HarborConfig config, RawConfigDto raw)
    {
        // ── Presentation ──
        // ConsoleEx knobs: canonical `ui.consoleEx` wins; the legacy root-level
        // `consoleEx` key is still honored so pre-CE-4-final configs keep working.
        config.Ui = new PresentationConfig(
            raw.Tui ?? PresentationConfig.Default.Tui,
            raw.Storage ?? raw.StorageBackend ?? PresentationConfig.Default.Storage,
            raw.Onboarded ?? raw.OnboardingCompleted ?? PresentationConfig.Default.Onboarded)
        {
            ConsoleEx = raw.Ui?.ConsoleEx ?? raw.ConsoleEx ?? ConsoleExUiConfig.Default,
        };
    }

    private static void ApplyTooling(HarborConfig config, RawConfigDto raw)
    {
        // ── Tooling ──
        config.Tooling = new ToolingConfig(
            (raw.EnabledPlugins ?? new List<string>()).AsReadOnly(),
            (raw.DisabledTools ?? new List<string>()).AsReadOnly());
    }

    private static void ApplyLimits(HarborConfig config, RawConfigDto raw)
    {
        // ── Run / Cost / Compaction ──
        config.Run = new RunLimitsConfig(raw.MaxSteps ?? RunLimitsConfig.Default.MaxSteps);
        config.Cost = new CostConfig(raw.CostLimit ?? CostConfig.Default.Limit);
        if (raw.Compaction is not null) config.Compaction = raw.Compaction;

        // ── ApiKeys / Providers ──
        if (raw.ApiKeys is not null) config.ApiKeys = raw.ApiKeys;
        if (raw.Providers is not null) config.Providers = raw.Providers;
    }
}
