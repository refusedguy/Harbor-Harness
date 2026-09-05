namespace Harbor.Application.Configuration;
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
                ApplyPermissions(config, raw);
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
        // CellForge knobs: canonical `ui.consoleEx` wins; the legacy root-level
        // `consoleEx` key is still honored so pre-CE-4-final configs keep working.
        config.Ui = new PresentationConfig(
            raw.Tui ?? PresentationConfig.Default.Tui,
            raw.Storage ?? raw.StorageBackend ?? PresentationConfig.Default.Storage,
            raw.Onboarded ?? raw.OnboardingCompleted ?? PresentationConfig.Default.Onboarded)
        {
            CellForge = raw.Ui?.CellForge ?? raw.CellForge ?? CellForgeUiConfig.Default,
        };
    }

    private static void ApplyTooling(HarborConfig config, RawConfigDto raw)
    {
        // ── Tooling ──
        config.Tooling = new ToolingConfig(
            (raw.EnabledPlugins ?? new List<string>()).AsReadOnly(),
            (raw.DisabledTools ?? new List<string>()).AsReadOnly(),
            raw.AutoReloadPlugins ?? true);
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

    private static void ApplyPermissions(HarborConfig config, RawConfigDto raw)
    {
        if (raw.Permissions is null) return;
        config.Permissions = raw.Permissions;
    }
}
