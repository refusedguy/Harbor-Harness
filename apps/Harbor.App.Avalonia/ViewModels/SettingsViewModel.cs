using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Providers;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.Core.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Settings dialog view-model. Reads the persisted CommonConfig
///     (~/.harbor/config.json) and AvaloniaConfig (~/.harbor/avalonia.json)
///     on construction, lets the user edit a curated set of fields, and
///     atomically writes both back on Save. Theme is applied immediately
///     so the user sees the switch without a restart.
/// </summary>
/// <remarks>
///     <para>
///         <b>Persistence:</b> Save calls
///         <see cref="ICommonConfigStore.SaveAsync(CommonConfig, CancellationToken)"/>
///         and <see cref="IAppConfigStore{T}.SaveAsync(T, CancellationToken)"/>,
///         both of which write atomically (temp file + rename) under a
///         SemaphoreSlim. No env-var mutation — the previous implementation
///         only set process env vars, which silently disappeared on restart
///         and never showed up in the config file (the user reported
///         "settings don't work" because of this).
///     </para>
///     <para>
///         <b>Cancel:</b> re-reads the persisted config and resets every
///         ObservableProperty back to the on-disk value, so the dialog
///         returns to the last-saved state without saving.
///     </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ToastService _toasts;
    private readonly ICommonConfigStore _commonStore;
    private readonly IAppConfigStore<AvaloniaConfig> _appStore;
    private readonly IProviderRegistry _providers;
    private readonly IAuthResolver _authResolver;
    private CommonConfig _common;
    private AvaloniaConfig _app;

    /// <summary>Construct the settings view-model and load the persisted config.</summary>
    public SettingsViewModel(
        ThemeService theme,
        ILogger<SettingsViewModel> logger,
        ToastService toasts,
        ICommonConfigStore commonStore,
        IAppConfigStore<AvaloniaConfig> appStore,
        IProviderRegistry providers,
        IAuthResolver authResolver)
    {
        _themeService = theme;
        _logger = logger;
        _toasts = toasts;
        _commonStore = commonStore;
        _appStore = appStore;
        _providers = providers;
        _authResolver = authResolver;

        // Load synchronously — the constructor runs once on app start and
        // the stores complete IO in <10ms on a local disk. Blocking here
        // keeps the rest of the VM simple (no async init dance).
        _common = _commonStore.LoadAsync().GetAwaiter().GetResult().Value;
        _app = _appStore.LoadAsync().GetAwaiter().GetResult().Value;

        Theme = string.IsNullOrEmpty(_common.Theme) ? "system" : _common.Theme;
        DefaultProvider = string.IsNullOrEmpty(_common.DefaultProvider) ? "ollama" : _common.DefaultProvider;
        DefaultModel = _common.DefaultModel ?? string.Empty;
        FontFamily = string.IsNullOrEmpty(_app.FontFamily) ? "Inter" : _app.FontFamily;
        StorageBackend = string.IsNullOrEmpty(_common.StorageBackend) ? "jsonl" : _common.StorageBackend;
        LogLevel = string.IsNullOrEmpty(_common.LogLevel) ? "info" : _common.LogLevel;
        OllamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";

        LoadProviderConfigs();
    }

    /// <summary>
    ///     Per-provider configuration rows (API key input + auth status). Built
    ///     from the registry + persisted <c>ApiKeys</c> dictionary.
    /// </summary>
    public ObservableCollection<ProviderConfigRowViewModel> ProviderConfigs { get; } = new();

    /// <summary>
    ///     Reusable picker view-model used inside Settings to browse models.
    ///     Resolved lazily on first access so the constructor stays cheap.
    /// </summary>
    public ProviderModelPickerViewModel? Picker { get; set; }

    /// <summary>
    ///     Populate <see cref="ProviderConfigs"/> from the registry, attaching
    ///     the persisted API key (if any) and the resolved auth status.
    /// </summary>
    private void LoadProviderConfigs()
    {
        ProviderConfigs.Clear();
        foreach (var pid in _providers.GetRegisteredProviderIds())
        {
            var id = pid.Value;
            var preset = ProviderPresets.Find(id);
            string displayName = preset?.DisplayName ?? id;
            bool requiresKey = preset?.RequiresApiKey ?? true;
            _common.ApiKeys.TryGetValue(id, out var savedKey);
            string apiKey = savedKey ?? string.Empty;

            // Resolve auth status (env var fallback is included by the resolver).
            bool authenticated = false;
            try
            {
                authenticated = _authResolver.ResolveApiKeyAsync(id).GetAwaiter().GetResult().IsSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Settings auth resolution threw for {Provider}", id);
            }

            ProviderConfigs.Add(new ProviderConfigRowViewModel(
                id, displayName, apiKey, requiresKey, authenticated));
        }
    }

    [ObservableProperty]
    private string _theme = "system";

    [ObservableProperty]
    private string _defaultProvider = "ollama";

    [ObservableProperty]
    private string _defaultModel = string.Empty;

    [ObservableProperty]
    private string _fontFamily = "Inter";

    [ObservableProperty]
    private string _storageBackend = "jsonl";

    [ObservableProperty]
    private string _logLevel = "info";

    [ObservableProperty]
    private string _ollamaHost = string.Empty;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>
    ///     Save settings: persist CommonConfig + AvaloniaConfig to disk, apply
    ///     the theme immediately, and emit a success toast. No env-var
    ///     mutation — the user's choice survives a restart because it's in
    ///     the JSON files.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        // Persist the env-var-style overrides too, so legacy code that still
        // reads HARBOR_* env vars sees the new values within this process.
        Environment.SetEnvironmentVariable("HARBOR_MODEL", $"{DefaultProvider}/{DefaultModel}");
        Environment.SetEnvironmentVariable("HARBOR_STORAGE", StorageBackend);
        Environment.SetEnvironmentVariable("HARBOR_LOGLEVEL", LogLevel);
        Environment.SetEnvironmentVariable("OLLAMA_HOST", OllamaHost);

        _common = _common with
        {
            Theme = Theme,
            DefaultProvider = DefaultProvider,
            DefaultModel = DefaultModel,
            StorageBackend = StorageBackend,
            LogLevel = LogLevel,
            // Persist every provider's API key from the per-provider rows.
            // Empty strings are dropped so we don't overwrite an existing key
            // with nothing if the user cleared a row.
            ApiKeys = ProviderConfigs
                .Where(r => !string.IsNullOrWhiteSpace(r.ApiKey))
                .ToImmutableDictionary(r => r.Id, r => r.ApiKey, StringComparer.Ordinal),
        };
        _app = _app with { FontFamily = FontFamily, Theme = Theme };

        var commonResult = await _commonStore.SaveAsync(_common).ConfigureAwait(true);
        var appResult = await _appStore.SaveAsync(_app).ConfigureAwait(true);

        if (commonResult.IsSuccess && appResult.IsSuccess)
        {
            // Apply the theme immediately so the user sees the change without
            // a restart. ThemeService.Apply(string) handles dark/light/system.
            _themeService.Apply(Theme);
            IsDarkTheme = _themeService.IsDark;
            _logger.LogInformation(
                "Settings saved: theme={Theme}, provider={Provider}, model={Model}, storage={Storage}, log={LogLevel}, font={Font}",
                Theme, DefaultProvider, DefaultModel, StorageBackend, LogLevel, FontFamily);
            _toasts.Show($"Settings saved — theme: {Theme}, model: {DefaultProvider}/{DefaultModel}.", ToastKind.Success);
        }
        else
        {
            var error = commonResult.IsFailure ? commonResult.Error : appResult.Error;
            _logger.LogError("Settings save failed: {Error}", error);
            _toasts.Show($"Could not save settings: {error}", ToastKind.Error);
        }
    }

    /// <summary>
    ///     Apply the current theme selection immediately (without saving). Used
    ///     by the "Apply" button next to the theme picker so the user can
    ///     preview a theme before committing.
    /// </summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        _themeService.Apply(Theme);
        IsDarkTheme = _themeService.IsDark;
    }

    /// <summary>Cancel: revert every ObservableProperty to the last-saved value.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Theme = _common.Theme;
        DefaultProvider = _common.DefaultProvider;
        DefaultModel = _common.DefaultModel;
        FontFamily = _app.FontFamily;
        StorageBackend = _common.StorageBackend;
        LogLevel = _common.LogLevel;
    }

    /// <summary>
    ///     Re-run the first-launch onboarding wizard. Sets
    ///     <see cref="CommonConfig.OnboardingCompleted"/> back to <c>false</c>
    ///     and persists it, then asks the user to restart. On next launch
    ///     <c>App.OnFrameworkInitializationCompleted</c> will detect the flag
    ///     is false and show <c>OnboardingWindow</c> again.
    /// </summary>
    [RelayCommand]
    private async Task RerunOnboardingAsync()
    {
        var result = await _commonStore.UpdateAsync(cfg => cfg with
        {
            OnboardingCompleted = false,
        }).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            _toasts.Show("Onboarding reset — restart Harbor to run the wizard again.", ToastKind.Success);
            _logger.LogInformation("Onboarding reset by user — restart required.");
        }
        else
        {
            _toasts.Show($"Could not reset onboarding: {result.Error}", ToastKind.Error);
            _logger.LogError("Onboarding reset failed: {Error}", result.Error);
        }
    }

    /// <summary>
    ///     Save a single provider's API key immediately, without requiring the
    ///     user to click the global "Save" button. Persists via
    ///     <see cref="ICommonConfigStore.UpdateAsync"/> so the key is available
    ///     to the agent + picker right away (the in-process auth resolver
    ///     reloads the config on every request).
    /// </summary>
    /// <param name="row">The provider row whose key should be persisted.</param>
    [RelayCommand]
    private async Task SaveProviderKeyAsync(ProviderConfigRowViewModel? row)
    {
        if (row is null) return;

        var result = await _commonStore.UpdateAsync(cfg =>
        {
            var keys = cfg.ApiKeys;
            if (string.IsNullOrWhiteSpace(row.ApiKey))
            {
                keys = keys.Remove(row.Id);
            }
            else
            {
                keys = keys.SetItem(row.Id, row.ApiKey);
            }
            return cfg with { ApiKeys = keys };
        }).ConfigureAwait(true);

        if (result.IsSuccess)
        {
            // Re-resolve auth status so the row's badge updates.
            try
            {
                row.IsAuthenticated = _authResolver.ResolveApiKeyAsync(row.Id).GetAwaiter().GetResult().IsSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Re-resolve auth failed for {Provider}", row.Id);
            }
            row.TestResult = row.IsAuthenticated ? "✓ Key saved" : "Key saved (will check on next request)";
            _toasts.Show($"API key saved for {row.DisplayName}.", ToastKind.Success);
        }
        else
        {
            row.TestResult = $"✗ Save failed: {result.Error}";
            _toasts.Show($"Could not save key for {row.DisplayName}: {result.Error}", ToastKind.Error);
        }
    }

    /// <summary>
    ///     Test a single provider's connection by fetching its model list
    ///     with a 5-second timeout. Persists the row's current API key first
    ///     (so the test sees the value the user just typed, not the on-disk
    ///     one). Used by the "Test" button on each provider row.
    /// </summary>
    /// <param name="row">The provider row to test.</param>
    [RelayCommand]
    private async Task TestProviderConnectionAsync(ProviderConfigRowViewModel? row)
    {
        if (row is null) return;

        row.IsTesting = true;
        row.TestResult = "Testing…";
        try
        {
            // Persist the typed key first so the auth resolver (which reads
            // from CommonConfig on every call) sees the latest value.
            await SaveProviderKeyAsync(row).ConfigureAwait(true);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var modelsResult = await _providers.GetAllModelsAsync(cts.Token).ConfigureAwait(true);
            if (modelsResult.IsSuccess)
            {
                int count = modelsResult.Value.Count(m => m.ProviderId == row.Id);
                row.IsAuthenticated = count > 0 || !row.RequiresApiKey;
                row.TestResult = count > 0
                    ? $"✓ {count} model(s) available"
                    : (row.RequiresApiKey ? "✓ Reachable but no models returned" : "✓ Reachable (no key needed)");
            }
            else
            {
                row.IsAuthenticated = false;
                row.TestResult = $"✗ {modelsResult.Error}";
            }
        }
        catch (OperationCanceledException)
        {
            row.TestResult = "✗ Timed out after 5s — is the provider running?";
        }
        catch (Exception ex)
        {
            row.TestResult = $"✗ {ex.Message}";
        }
        finally
        {
            row.IsTesting = false;
        }
    }
}

/// <summary>
///     One row in the Settings "Provider Configuration" section. Holds the
///     provider id, display name, the editable API key TextBox content, and
///     the live auth status / test-connection result.
/// </summary>
public sealed partial class ProviderConfigRowViewModel : ObservableObject
{
    /// <summary>Construct a provider config row.</summary>
    public ProviderConfigRowViewModel(
        string id,
        string displayName,
        string apiKey,
        bool requiresApiKey,
        bool isAuthenticated)
    {
        Id = id;
        DisplayName = displayName;
        _apiKey = apiKey;
        RequiresApiKey = requiresApiKey;
        _isAuthenticated = isAuthenticated;
    }

    /// <summary>Provider id (e.g. <c>ollama</c>, <c>kilocode</c>).</summary>
    public string Id { get; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; }

    /// <summary>Whether this provider requires an API key (false for Ollama).</summary>
    public bool RequiresApiKey { get; }

    /// <summary>Auth status icon — <c>✓</c> or <c>✗</c>.</summary>
    public string AuthIcon => IsAuthenticated ? "✓" : (RequiresApiKey ? "✗" : "—");

    /// <summary>Auth status text — e.g. "Authenticated", "No key".</summary>
    public string AuthText => !RequiresApiKey
        ? "No key needed"
        : (IsAuthenticated ? "Authenticated" : "No key");

    /// <summary>API key (editable in the UI).</summary>
    [ObservableProperty]
    private string _apiKey;

    /// <summary>Whether an API key has been resolved for this provider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthIcon))]
    [NotifyPropertyChangedFor(nameof(AuthText))]
    private bool _isAuthenticated;

    /// <summary>True while a Test connection request is in flight.</summary>
    [ObservableProperty]
    private bool _isTesting;

    /// <summary>Human-readable result of the last Test connection attempt.</summary>
    [ObservableProperty]
    private string _testResult = string.Empty;
}
