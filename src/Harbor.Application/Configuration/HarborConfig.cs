using System.Collections;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Harbor.Core.Configuration;

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
        Compaction = Compaction
    };

    /// <summary>
    ///     Validate every section. Aggregates all errors into a single failure
    ///     message so the config store can surface them instead of silently
    ///     falling back to defaults.
    /// </summary>
    public Result<HarborConfig> Validate()
    {
        var results = new Result[]
            {
                Identity.EffectiveModel(),
                Tooling.Validate(),
                Cost.Validate(),
                Compaction.Validate(),
                Ui.Validate(),
                Run.Validate()
            }
            .AsValueEnumerable()
            .Concat(Providers.Values.AsValueEnumerable().Select(static e => (Result)e.Validate()));

        var errors = results
            .Where(static r => r.IsFailure)
            .Select(static r => r.Error)
            .ToArray();

        return errors.Length == 0
            ? Result.Success(this)
            : Result.Failure<HarborConfig>(string.Join("; ", errors));
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
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Agent { get; set; }
    public string? Tui { get; set; }
    public string? Storage { get; set; }
    public bool? Onboarded { get; set; }

    [JsonPropertyName("defaultProvider")] public string? DefaultProvider { get; set; }
    [JsonPropertyName("defaultModel")] public string? DefaultModel { get; set; }
    [JsonPropertyName("onboardingCompleted")] public bool? OnboardingCompleted { get; set; }
    [JsonPropertyName("storageBackend")] public string? StorageBackend { get; set; }
    [JsonPropertyName("logLevel")] public string? LogLevelConfig { get; set; }

    public Dictionary<string, string>? ApiKeys { get; set; }
    public Dictionary<string, ProviderConfigEntry>? Providers { get; set; }
    public List<string>? EnabledPlugins { get; set; }
    public List<string>? DisabledTools { get; set; }
    public int? MaxSteps { get; set; }
    public decimal? CostLimit { get; set; }
    public CompactionConfig? Compaction { get; set; }
}

/// <summary>
///     Normalizes a <see cref="RawConfigDto" /> (which may carry legacy
///     <c>CommonConfig</c> aliases) into a canonical <see cref="HarborConfig" />.
///     Centralises the dual-schema compatibility that used to be spread across
///     computed properties on <see cref="HarborConfig" />.
/// </summary>
public static class ConfigNormalizer
{
    public static Result<HarborConfig> Normalize(RawConfigDto raw)
    {
        var config = new HarborConfig();

        // ── Identity: canonical "provider"/"model"/"agent" win; legacy
        // "defaultProvider"/"defaultModel" are only used as fallback. ──
        var providerStr = !string.IsNullOrEmpty(raw.Provider)
            ? raw.Provider
            : raw.DefaultProvider;
        if (!string.IsNullOrEmpty(providerStr))
        {
            var pr = ProviderId.TryCreate(providerStr);
            if (pr.IsFailure) return Result.Failure<HarborConfig>(pr.Error);
            config.Identity = config.Identity with { Provider = pr.Value };
        }

        var modelStr = !string.IsNullOrEmpty(raw.Model)
            ? raw.Model
            : raw.DefaultModel;
        if (!string.IsNullOrEmpty(modelStr))
        {
            var mr = ModelRef.TryParse(modelStr);
            if (mr.IsFailure) return Result.Failure<HarborConfig>(mr.Error);
            config.Identity = config.Identity with { Model = mr.Value };
        }

        if (!string.IsNullOrEmpty(raw.Agent))
        {
            var ar = AgentName.TryCreate(raw.Agent);
            if (ar.IsFailure) return Result.Failure<HarborConfig>(ar.Error);
            config.Identity = config.Identity with { Agent = ar.Value };
        }

        // ── Presentation ──
        config.Ui = new PresentationConfig(
            raw.Tui ?? PresentationConfig.Default.Tui,
            raw.Storage ?? raw.StorageBackend ?? PresentationConfig.Default.Storage,
            raw.Onboarded ?? raw.OnboardingCompleted ?? PresentationConfig.Default.Onboarded);

        // ── Tooling ──
        config.Tooling = new ToolingConfig(
            (raw.EnabledPlugins ?? new List<string>()).AsReadOnly(),
            (raw.DisabledTools ?? new List<string>()).AsReadOnly());

        // ── Run / Cost / Compaction ──
        config.Run = new RunLimitsConfig(raw.MaxSteps ?? RunLimitsConfig.Default.MaxSteps);
        config.Cost = new CostConfig(raw.CostLimit ?? CostConfig.Default.Limit);
        if (raw.Compaction is not null) config.Compaction = raw.Compaction;

        // ── ApiKeys / Providers ──
        if (raw.ApiKeys is not null) config.ApiKeys = raw.ApiKeys;
        if (raw.Providers is not null) config.Providers = raw.Providers;

        return Result.Success(config);
    }
}
