using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Onboarding;

// KILLER_FEATURES §2.7 Feature 9 (issue #383): setup-guide DETECTION.
//
// Detection lives here (Application) and stays free of any UI-framework
// reference — the CLI composition root maps the signals onto the framework
// `SetupChecklistModel` (Harbor.Ui.Framework.Projection), exactly like
// `SkillFreshnessSeeder` maps snapshots onto `SkillFreshnessEntry`. That keeps
// `Harbor.Application`'s Domain-only edge and the FullLayerMatrixTests row
// green.
//
// Existence-tolerant by contract: a missing config, an unreadable store or a
// failing probe yield "not done" signals, never a throw. Setup detection runs
// on the render-critical startup path and must not be able to fail the REPL.

/// <summary>
/// What setup detection observed (issue #383). One bool per detected task; the
/// host maps them onto <c>SetupTaskIds</c>. <see cref="ProviderHealthPassed" />
/// stays false in <see cref="SetupChecklistDetector.DetectAsync" /> — the probe
/// is a network call and runs separately via
/// <see cref="SetupChecklistDetector.CheckProviderHealthAsync" />.
/// </summary>
/// <param name="ConfigFileExists">A config file exists on disk.</param>
/// <param name="ProviderKeyStored">At least one provider key resolves (config or env).</param>
/// <param name="ProviderHealthPassed">The configured provider answered a probe.</param>
/// <param name="WorkspaceResolved">A working directory is resolved (non-empty and existing).</param>
public sealed record SetupChecklistSignals(
    bool ConfigFileExists,
    bool ProviderKeyStored,
    bool ProviderHealthPassed,
    bool WorkspaceResolved)
{
    /// <summary>Nothing detected yet.</summary>
    public static SetupChecklistSignals None { get; } = new(false, false, false, false);
}

/// <summary>
/// Detects setup-guide progress (issue #383): (1) config file exists,
/// (2) a provider key is stored, (3) the provider health-check passed,
/// (4) the workspace is resolved. (5) "first prompt sent" is session state and
/// is owned by the host, not here.
/// </summary>
public sealed class SetupChecklistDetector
{
    private readonly IConfigStore _configStore;
    private readonly AuthStore _authStore;
    private readonly IProviderHealthCheck? _healthCheck;
    private readonly string? _configPath;
    private readonly string? _workspacePath;
    private readonly ILogger<SetupChecklistDetector>? _logger;

    /// <summary>Construct a detector over the supplied stores.</summary>
    /// <param name="configStore">Config store (source for keys + the onboarded flag).</param>
    /// <param name="authStore">Auth store (stored + env keys).</param>
    /// <param name="healthCheck">Optional "test connection" probe; absent skips task 3.</param>
    /// <param name="configPath">Config file path override (defaults to the store's default path).</param>
    /// <param name="workspacePath">Workspace override (defaults to the current directory).</param>
    /// <param name="logger">Optional logger.</param>
    public SetupChecklistDetector(
        IConfigStore configStore,
        AuthStore authStore,
        IProviderHealthCheck? healthCheck = null,
        string? configPath = null,
        string? workspacePath = null,
        ILogger<SetupChecklistDetector>? logger = null)
    {
        _configStore = configStore;
        _authStore = authStore;
        _healthCheck = healthCheck;
        _configPath = configPath;
        _workspacePath = workspacePath;
        _logger = logger;
    }

    /// <summary>
    /// Detect the local (no-network) signals: config file, stored key,
    /// workspace. Never throws — an unreadable store reports "not done".
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SetupChecklistSignals> DetectAsync(CancellationToken ct = default)
    {
        bool configFileExists = ConfigFileExists();
        bool providerKeyStored = await HasStoredKeyAsync(ct).ConfigureAwait(false);
        bool workspaceResolved = ResolveWorkspace() is not null;
        return new SetupChecklistSignals(configFileExists, providerKeyStored, false, workspaceResolved);
    }

    /// <summary>
    /// True when a config file exists. Uses the injected path when supplied,
    /// otherwise the store's default location (<c>~/.harbor/config.json</c>).
    /// </summary>
    public bool ConfigFileExists()
    {
        try
        {
            return File.Exists(_configPath ?? JsonConfigStore.GetDefaultPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger?.LogDebug(ex, "Setup checklist: config file probe failed — treating as absent");
            return false;
        }
    }

    /// <summary>
    /// True when setup has never completed (<c>config.Onboarded == false</c>) —
    /// the first-run gate of the checklist. A config that cannot be loaded
    /// counts as "never completed" (the user is new by definition).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> HasCompletedSetupAsync(CancellationToken ct = default)
    {
        Result<HarborConfig> loaded = await _configStore.LoadAsync(ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            _logger?.LogDebug("Setup checklist: config load failed ({Error}) — treating setup as incomplete", loaded.Error);
            return false;
        }

        return loaded.Value.Onboarded;
    }

    /// <summary>
    /// Probe the configured provider. Best-effort and never throws: no probe,
    /// no provider configured, a bad provider id or a network failure all mean
    /// "not done", never a failed startup.
    /// </summary>
    /// <param name="ct">Cancellation token (cooperative — the wizard and the REPL both cancel on quit).</param>
    public async Task<bool> CheckProviderHealthAsync(CancellationToken ct = default)
    {
        if (_healthCheck is null)
        {
            return false;
        }

        try
        {
            Result<HarborConfig> loaded = await _configStore.LoadAsync(ct).ConfigureAwait(false);
            if (loaded.IsFailure)
            {
                return false;
            }

            // Explicit choice only: HarborConfig.Provider always reports the
            // built-in fallback, so the raw identity is the honest signal for
            // "the user never picked a provider".
            ProviderId? configured = loaded.Value.Identity.Provider;
            if (configured is null)
            {
                return false;
            }

            Result<ProviderHealth> health = await _healthCheck.CheckAsync(configured, ct).ConfigureAwait(false);
            return health.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Setup checklist: provider health probe failed — task stays pending");
            return false;
        }
    }

    private async Task<bool> HasStoredKeyAsync(CancellationToken ct)
    {
        try
        {
            Result<IReadOnlyDictionary<string, bool>> keys = await _authStore.ListApiKeysAsync(ct).ConfigureAwait(false);
            if (keys.IsFailure)
            {
                return false;
            }

            foreach (KeyValuePair<string, bool> pair in keys.Value)
            {
                if (pair.Value)
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Setup checklist: key probe failed — task stays pending");
            return false;
        }
    }

    private string? ResolveWorkspace()
    {
        try
        {
            string path = _workspacePath ?? Directory.GetCurrentDirectory();
            return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
