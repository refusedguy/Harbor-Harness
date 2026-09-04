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

    /// <summary>Persisted per-agent permission overrides (user decisions from Ask prompts).</summary>
    public Dictionary<string, List<PermissionRule>> Permissions { get; set; } = new();

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
        // Persist the CellForge section (nested under `ui`) only when it
        // diverges from defaults — keeps config.json free of knob noise for
        // users who never touched it.
        Ui = Ui.CellForge == CellForgeUiConfig.Default ? null : new UiRawDto { CellForge = Ui.CellForge },
        DefaultProvider = Identity.Provider?.Value,
        DefaultModel = Identity.Model?.ToString(),
        OnboardingCompleted = Ui.Onboarded,
        StorageBackend = Ui.Storage,
        LogLevelConfig = null,
        ApiKeys = ApiKeys,
        Providers = Providers,
        EnabledPlugins = Tooling.EnabledPlugins.ToList(),
        DisabledTools = Tooling.DisabledTools.ToList(),
        AutoReloadPlugins = !Tooling.AutoReloadPlugins, // null when default — keep config.json free of knob noise
        MaxSteps = Run.MaxSteps,
        CostLimit = Cost.Limit,
        Compaction = Compaction,
        SecondaryModel = SecondaryModel,
        Permissions = Permissions.Count == 0 ? null : Permissions
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
