namespace Harbor.Application.Configuration;
/// <summary>
///     Provider / model / agent identity selection.
///     Replaces the flat <c>Provider</c>/<c>Model</c>/<c>Agent</c> string fields
///     of the legacy <see cref="HarborConfig" /> with strongly-typed value objects.
/// </summary>
public sealed record IdentityConfig(
    ProviderId? Provider,
    ModelRef? Model,
    AgentName? Agent)
{
    public const string FallbackProvider = "kilocode";
    public const string FallbackModel = "kilocode/tencent/hy3:free";
    public const string FallbackAgent = "code";

    public static readonly IdentityConfig Default = new(
        ProviderId.Create(FallbackProvider),
        ModelRef.Create(ProviderId.Create(FallbackProvider), "tencent/hy3:free".Split('/')[1]),
        AgentName.Create(FallbackAgent));

    public ProviderId EffectiveProvider => Provider ?? ProviderId.Create(FallbackProvider);

    public AgentName EffectiveAgent => Agent ?? AgentName.Create(FallbackAgent);

    /// <summary>
    ///     Effective model — uses the explicit <see cref="Model" /> when set,
    ///     otherwise falls back to the default model for the effective provider.
    /// </summary>
    public Result<ModelRef> EffectiveModel()
    {
        if (Model is not null) return Result.Success(Model);
        var preset = ProviderPresets.Find(EffectiveProvider.Value);
        if (preset is not null)
            return ModelRef.TryParse($"{EffectiveProvider}/{preset.DefaultModel}");
        return ModelRef.TryParse($"{EffectiveProvider}/default");
    }
}

/// <summary>
///     Plugin + builtin-tool toggles.
/// </summary>
/// <summary>
///     Plugin + builtin-tool toggles.
/// </summary>
public sealed record ToolingConfig(
    IReadOnlyList<string> EnabledPlugins,
    IReadOnlyList<string> DisabledTools,
    [property: JsonPropertyName("autoReloadPlugins")] bool AutoReloadPlugins = true)
{
    public static readonly ToolingConfig Default = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        AutoReloadPlugins: true);

    public Result<ToolingConfig> Validate()
    {
        if (EnabledPlugins is null) return Result.Failure<ToolingConfig>("tooling.enabledPlugins must not be null");
        if (DisabledTools is null) return Result.Failure<ToolingConfig>("tooling.disabledTools must not be null");
        return Result.Success(this);
    }
}

/// <summary>
///     Cost guardrail for a session run.
/// </summary>
public sealed record CostConfig(decimal Limit)
{
    public static readonly CostConfig Default = new(10m);

    public Result<CostConfig> Validate()
    {
        if (Limit < 0) return Result.Failure<CostConfig>("cost.limit must be >= 0");
        return Result.Success(this);
    }
}

/// <summary>
///     Compaction tuning.
/// </summary>
public sealed record CompactionConfig(
    [property: JsonPropertyName("reserveTokens")] int ReserveTokens,
    [property: JsonPropertyName("keepRecentTokens")] int KeepRecentTokens,
    [property: JsonPropertyName("tailTurns")] int TailTurns)
{
    public static readonly CompactionConfig Default = new(16384, 20000, 2);

    public Result<CompactionConfig> Validate()
    {
        var errors = new List<string>(3);
        if (ReserveTokens <= 0) errors.Add("compaction.reserveTokens must be > 0");
        if (KeepRecentTokens <= 0) errors.Add("compaction.keepRecentTokens must be > 0");
        if (TailTurns <= 0) errors.Add("compaction.tailTurns must be > 0");
        return errors.Count == 0
            ? Result.Success(this)
            : Result.Failure<CompactionConfig>(string.Join("; ", errors));
    }
}

/// <summary>
///     CellForge renderer tuning (<c>ui.consoleEx</c> in config.json). The
///     CellForge path itself is selected by <c>tui: "consoleex"</c> or the
///     <c>HARBOR_TUI=consoleex</c> env var; this section is its kill-switch
///     and frame-wrapper knob.
/// </summary>
/// <param name="Enabled">
///     Master switch for the CellForge interactive renderer. When explicitly
///     <c>false</c>, a <c>tui: "consoleex"</c> selection falls back to the
///     legacy ANSI renderer with a log line instead of failing.
/// </param>
/// <param name="SyncUpdates">
///     Wrap frames in the synchronized-update pair <c>CSI ?2026 h/l</c>
///     (DECSYNC). Terminals without support simply ignore the sequence; the
///     probe refines this later, the config is the safe default.
/// </param>
public sealed record CellForgeUiConfig(
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("syncUpdates")] bool SyncUpdates = true)
{
    public static readonly CellForgeUiConfig Default = new();
}

/// <summary>
///     UI / storage presentation preferences.
/// </summary>
public sealed record PresentationConfig(
    string Tui,
    string Storage,
    bool Onboarded)
{
    /// <summary>CellForge renderer section — defaults apply when absent.</summary>
    [JsonPropertyName("consoleEx")]
    public CellForgeUiConfig CellForge { get; init; } = CellForgeUiConfig.Default;

    public static readonly PresentationConfig Default = new("ansi", "jsonl", false);

    public Result<PresentationConfig> Validate()
    {
        if (string.IsNullOrWhiteSpace(Tui))
            return Result.Failure<PresentationConfig>("ui.tui must not be empty");
        if (string.IsNullOrWhiteSpace(Storage))
            return Result.Failure<PresentationConfig>("ui.storage must not be empty");
        return Result.Success(this);
    }
}

/// <summary>
///     Per-agent run limits.
/// </summary>
public sealed record RunLimitsConfig(
    int MaxSteps,
    int DefaultMaxSteps = 50)
{
    public static readonly RunLimitsConfig Default = new(50);

    public Result<RunLimitsConfig> Validate()
    {
        if (MaxSteps is < 1 or > 1000)
            return Result.Failure<RunLimitsConfig>("run.maxSteps must be in [1, 1000]");
        return Result.Success(this);
    }
}

/// <summary>
///     User-supplied provider config entry (overrides the bundled JSON presets).
/// </summary>
public sealed record ProviderConfigEntry(
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("apiType")] string ApiType,
    [property: JsonPropertyName("modelsUrl")] string? ModelsUrl)
{
    public static readonly ProviderConfigEntry Default = new(string.Empty, "openai-compatible", null);

    public Result<ProviderConfigEntry> Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiType))
            return Result.Failure<ProviderConfigEntry>("provider.apiType must not be empty");
        return Result.Success(this);
    }
}
