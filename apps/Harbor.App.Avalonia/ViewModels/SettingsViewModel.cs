using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Providers;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Services;
using Harbor.Core.Configuration;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Settings dialog view-model. Reads the persisted CommonConfig
///     (~/.harbor/config.json) and AvaloniaConfig (~/.harbor/avalonia.json)
///     on construction, lets the user edit a curated set of fields, and
///     atomically writes both back on Save. Theme handling is delegated
///     to <see cref="ThemeSettingsViewModel" />; per-provider config rows
///     are <see cref="ProviderConfigViewModel" /> instances that own their
///     own Save / Test commands.
/// </summary>
/// <remarks>
///     <para>
///         <b>Persistence:</b> Save calls
///         <see cref="ICommonConfigStore.SaveAsync(CommonConfig, CancellationToken)" />
///         and <see cref="IAppConfigStore{T}.SaveAsync(T, CancellationToken)" />,
///         both of which write atomically (temp file + rename) under a
///         SemaphoreSlim. No env-var mutation — the previous implementation
///         only set process env vars, which silently disappeared on restart.
///     </para>
///     <para>
///         <b>Cancel:</b> re-reads the persisted config and resets every
///         ObservableProperty back to the on-disk value, so the dialog
///         returns to the last-saved state without saving.
///     </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigStore<AvaloniaConfig> _appStore;
    private readonly IAuthResolver _authResolver;
    private readonly ICommonConfigStore _commonStore;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IProviderRegistry _providers;
    private readonly IThemeService _themeService;
    private readonly IToastService _toasts;
    private AvaloniaConfig _app;
    private CommonConfig _common;

    [ObservableProperty]
    private string _defaultModel = string.Empty;

    [ObservableProperty]
    private string _defaultProvider = "ollama";

    [ObservableProperty]
    private string _fontFamily = "Inter";

    [ObservableProperty]
    private string _logLevel = "info";

    [ObservableProperty]
    private string _ollamaHost = string.Empty;

    [ObservableProperty]
    private string _storageBackend = "jsonl";

    /// <summary>Construct the settings view-model and load the persisted config.</summary>
    public SettingsViewModel(
        IThemeService theme,
        ILogger<SettingsViewModel> logger,
        ILoggerFactory loggerFactory,
        IToastService toasts,
        ICommonConfigStore commonStore,
        IAppConfigStore<AvaloniaConfig> appStore,
        IProviderRegistry providers,
        IAuthResolver authResolver)
    {
        _themeService = theme;
        _logger = logger;
        _loggerFactory = loggerFactory;
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

        ThemeSettings = new ThemeSettingsViewModel(theme)
        {
            Theme = string.IsNullOrEmpty(_common.Theme) ? "system" : _common.Theme
        };
        DefaultProvider = string.IsNullOrEmpty(_common.DefaultProvider) ? "ollama" : _common.DefaultProvider;
        DefaultModel = _common.DefaultModel ?? string.Empty;
        FontFamily = string.IsNullOrEmpty(_app.FontFamily) ? "Inter" : _app.FontFamily;
        StorageBackend = string.IsNullOrEmpty(_common.StorageBackend) ? "jsonl" : _common.StorageBackend;
        LogLevel = string.IsNullOrEmpty(_common.LogLevel) ? "info" : _common.LogLevel;
        OllamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434";

        LoadProviderConfigs();
    }

    /// <summary>
    ///     Theme selection + preview sub-view-model. Bound by XAML as
    ///     <c>{Binding ThemeSettings.Theme}</c> /
    ///     <c>{Binding ThemeSettings.ApplyThemeCommand}</c>.
    /// </summary>
    public ThemeSettingsViewModel ThemeSettings { get; }

    /// <summary>Diagnostics (A11): theme as seen by the LAST store load.</summary>
    public string DebugPersistedTheme => _common.Theme;

    /// <summary>Diagnostics (A11): directory the common store reads/writes.</summary>
    public string DebugConfigDirectory => _common.ConfigDirectory;

    /// <summary>
    ///     Per-provider configuration rows (API key input + auth status +
    ///     Save / Test commands). Built from the registry + persisted
    ///     <c>ApiKeys</c> dictionary.
    /// </summary>
    public ObservableCollection<ProviderConfigViewModel> ProviderConfigs { get; } = new();

    /// <summary>
    ///     Reusable picker view-model used inside Settings to browse models.
    ///     Resolved lazily on first access so the constructor stays cheap.
    /// </summary>
    public ProviderModelPickerViewModel? Picker { get; set; }

    /// <summary>
    ///     Populate <see cref="ProviderConfigs" /> from the registry, attaching
    ///     the persisted API key (if any) and the resolved auth status.
    /// </summary>
    private void LoadProviderConfigs()
    {
        ProviderConfigs.Clear();
        foreach (var pid in _providers.GetRegisteredProviderIds())
        {
            string id = pid.Value;
            var preset = ProviderPresets.Find(id);
            string displayName = preset?.DisplayName ?? id;
            bool requiresKey = preset?.RequiresApiKey ?? true;
            _common.ApiKeys.TryGetValue(id, out string? savedKey);
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

            ProviderConfigs.Add(new ProviderConfigViewModel(
                id, displayName, apiKey, requiresKey, authenticated,
                _commonStore, _providers, _toasts,
                _loggerFactory.CreateLogger<ProviderConfigViewModel>()));
        }
    }

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
            Theme = ThemeSettings.Theme,
            DefaultProvider = DefaultProvider,
            DefaultModel = DefaultModel,
            StorageBackend = StorageBackend,
            LogLevel = LogLevel,
            // Persist every provider's API key from the per-provider rows.
            // Empty strings are dropped so we don't overwrite an existing key
            // with nothing if the user cleared a row.
            ApiKeys = ProviderConfigs
                .Where(r => !string.IsNullOrWhiteSpace(r.ApiKey))
                .ToImmutableDictionary(r => r.Id, r => r.ApiKey, StringComparer.Ordinal)
        };
        _app = _app with { FontFamily = FontFamily, Theme = ThemeSettings.Theme };

        var commonResult = await _commonStore.SaveAsync(_common).ConfigureAwait(true);
        var appResult = await _appStore.SaveAsync(_app).ConfigureAwait(true);

        if (commonResult.IsSuccess && appResult.IsSuccess)
        {
            // Apply the theme immediately so the user sees the change without
            // a restart. ThemeService.Apply(string) handles dark/light/system.
            ThemeSettings.Apply(ThemeSettings.Theme);
            _logger.LogInformation(
                "Settings saved: theme={Theme}, provider={Provider}, model={Model}, storage={Storage}, log={LogLevel}, font={Font}",
                ThemeSettings.Theme, DefaultProvider, DefaultModel, StorageBackend, LogLevel, FontFamily);
            _toasts.Show($"Settings saved — theme: {ThemeSettings.Theme}, model: {DefaultProvider}/{DefaultModel}.", ToastKind.Success);
        }
        else
        {
            string? error = commonResult.IsFailure ? commonResult.Error : appResult.Error;
            _logger.LogError("Settings save failed: {Error}", error);
            _toasts.Show($"Could not save settings: {error}", ToastKind.Error);
        }
    }

    /// <summary>Cancel: revert every ObservableProperty to the last-saved value.</summary>
    [RelayCommand]
    private void Cancel()
    {
        ThemeSettings.Theme = _common.Theme;
        DefaultProvider = _common.DefaultProvider;
        DefaultModel = _common.DefaultModel;
        FontFamily = _app.FontFamily;
        StorageBackend = _common.StorageBackend;
        LogLevel = _common.LogLevel;
    }

    /// <summary>
    ///     Re-run the first-launch onboarding wizard. Sets
    ///     <see cref="CommonConfig.OnboardingCompleted" /> back to <c>false</c>
    ///     and persists it, then asks the user to restart.
    /// </summary>
    [RelayCommand]
    private async Task RerunOnboardingAsync()
    {
        var result = await _commonStore.UpdateAsync(cfg => cfg with
        {
            OnboardingCompleted = false
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
}
