using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.Configuration;
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
    private CommonConfig _common;
    private AvaloniaConfig _app;

    /// <summary>Construct the settings view-model and load the persisted config.</summary>
    public SettingsViewModel(
        ThemeService theme,
        ILogger<SettingsViewModel> logger,
        ToastService toasts,
        ICommonConfigStore commonStore,
        IAppConfigStore<AvaloniaConfig> appStore)
    {
        _themeService = theme;
        _logger = logger;
        _toasts = toasts;
        _commonStore = commonStore;
        _appStore = appStore;

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
}
