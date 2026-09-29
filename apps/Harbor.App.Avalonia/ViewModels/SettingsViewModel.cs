using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Abstractions.Providers;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Services;
using Harbor.Application.Configuration;
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
///         SemaphoreSlim.
///     </para>
///     <para>
///         <b>No env-var mutation (#677).</b> An earlier revision called
///         <c>Environment.SetEnvironmentVariable</c> for <c>HARBOR_MODEL</c> /
///         <c>HARBOR_STORAGE</c> / <c>HARBOR_LOGLEVEL</c> / <c>OLLAMA_HOST</c>.
///         That is process state, not session state, and it was dead: every one
///         of those names is read ONCE during <c>AddHarbor</c> —
///         <c>StorageModule</c> resolves the backend,
///         <c>ConfigurationModule</c> resolves the model,
///         <c>ProviderFactories</c> snapshots <c>OLLAMA_HOST</c> into an
///         <c>HttpClient</c> — so nothing re-reads them after startup and the
///         write could not have changed the running app. It only leaked the
///         user's choice into every other component in the process, and into
///         every test that ran after it. Persistence is the config file, which
///         is what the next launch reads.
///     </para>
///     <para>
///         <b>No chosen defaults (#677).</b> Every field below is shown exactly
///         as the config record holds it, empty included. <c>CommonConfig</c>
///         owns the defaults (<c>DefaultProvider = "anthropic"</c>,
///         <c>LogLevel = "info"</c>, <c>AvaloniaConfig.FontFamily = "Inter"</c>),
///         and <c>StorageBackend = ""</c> means "not chosen" so the composition
///         preset resolves it (ADR-008 — CLI <c>jsonl</c>, desktop
///         <c>memory</c>). This view-model used to substitute <c>"ollama"</c>
///         and <c>"jsonl"</c> for those, which both disagreed with the core
///         and made the preset unreachable — the user could not unset the field,
///         because reopening the wizard wrote <c>"jsonl"</c> straight back.
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
    private readonly IToastService _toasts;
    private AvaloniaConfig _app;
    private CommonConfig _common;

    /// <summary>
    ///     Full model id exactly as the config record holds it. Empty is a
    ///     legitimate stored value and is shown as such — see the class remarks.
    /// </summary>
    [ObservableProperty]
    private string _defaultModel = string.Empty;

    /// <summary>
    ///     Provider id exactly as the config record holds it. The record's own
    ///     default is <c>"anthropic"</c>
    ///     (<see cref="CommonConfig.DefaultProvider" />); this field never
    ///     supplies one.
    /// </summary>
    [ObservableProperty]
    private string _defaultProvider = string.Empty;

    /// <summary>Font family exactly as <c>AvaloniaConfig.FontFamily</c> holds it.</summary>
    [ObservableProperty]
    private string _fontFamily = string.Empty;

    /// <summary>Log level exactly as <c>CommonConfig.LogLevel</c> holds it.</summary>
    [ObservableProperty]
    private string _logLevel = string.Empty;

    /// <summary>
    ///     Ollama endpoint as it was read from the process environment at
    ///     startup. Display-only: <c>ProviderFactories</c> snapshots it into an
    ///     <c>HttpClient</c> while <c>AddHarbor</c> composes, so nothing this
    ///     screen does can change a running app. A running app's endpoint is a
    ///     launch-time input, not a setting (#677).
    /// </summary>
    [ObservableProperty]
    private string _ollamaHost = string.Empty;

    /// <summary>
    ///     Storage backend exactly as the config record holds it. <c>""</c> means
    ///     "not chosen" and is shown as an empty selection — the composition
    ///     preset decides (ADR-008).
    /// </summary>
    [ObservableProperty]
    private string _storageBackend = string.Empty;

    /// <summary>Construct the settings view-model and load the persisted config.</summary>
    /// <param name="themeReader">Read-only view of the active theme (for <c>ThemeSettings</c>).</param>
    /// <param name="themeApplier">Theme applier forwarded to <c>ThemeSettings</c>.</param>
    /// <param name="logger">Sink for view-model diagnostics.</param>
    /// <param name="loggerFactory">Factory for the per-session loggers this view-model creates.</param>
    /// <param name="toasts">Transient notifications surface for the user.</param>
    /// <param name="commonStore">Cross-desktop configuration, the one both apps read.</param>
    /// <param name="appStore">Avalonia-only configuration layered over the common store.</param>
    /// <param name="providers">Provider registry, for the model picker.</param>
    /// <param name="authResolver">
    ///     The single source of truth on whether a provider is authorized (#671).
    ///     The view-model does not inspect key stores itself.
    /// </param>
    public SettingsViewModel(
        IThemeReader themeReader,
        IThemeApplier themeApplier,
        ILogger<SettingsViewModel> logger,
        ILoggerFactory loggerFactory,
        IToastService toasts,
        ICommonConfigStore commonStore,
        IAppConfigStore<AvaloniaConfig> appStore,
        IProviderRegistry providers,
        IAuthResolver authResolver)
    {
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
#pragma warning disable RS0030 // One-shot ctor load of local-disk config (<10ms); async-init dance not worth it. Catalogued in BannedSymbols.txt.
        #pragma warning disable CFE0001
        // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
        // REAL DEFECT, NOT a false positive -- baselined pending a product decision, see
        // docs/ROP-API-INVENTORY.md 5.3. There is NO guard: LoadAsync() returns a Result and
        // .Value is read in the constructor, so a failed config load throws
        // ResultFailureException out of a ViewModel constructor on app start. Fixing it
        // needs a decision on what the settings screen shows when the store fails, which is
        // not a mechanical change -- CommonConfig has no public default instance.
        _common = _commonStore.LoadAsync().GetAwaiter().GetResult().Value;
        #pragma warning restore CFE0001
        #pragma warning disable CFE0001
        // CFE0001 baseline: docs/ROP-API-INVENTORY.md 5.
        // REAL DEFECT, NOT a false positive -- baselined pending a product decision, see
        // docs/ROP-API-INVENTORY.md 5.3. Same unguarded read as the line above.
        _app = _appStore.LoadAsync().GetAwaiter().GetResult().Value;
        #pragma warning restore CFE0001
#pragma warning restore RS0030

        ThemeSettings = new ThemeSettingsViewModel(themeReader, themeApplier)
        {
            Theme = _common.Theme ?? string.Empty
        };
        // #677: every field is shown EXACTLY as the record holds it. An absent
        // key already yields the record's own default (CommonConfig.DefaultProvider
        // = "anthropic", LogLevel = "info", AvaloniaConfig.FontFamily = "Inter"),
        // and an empty StorageBackend means "not chosen" so the composition
        // preset resolves it. Substituting a literal here is what made the UI say
        // "ollama" where the core says "anthropic" and "jsonl" where the desktop
        // preset says "memory" — and it put the preset permanently out of reach.
        DefaultProvider = _common.DefaultProvider ?? string.Empty;
        DefaultModel = _common.DefaultModel ?? string.Empty;
        FontFamily = _app.FontFamily ?? string.Empty;
        StorageBackend = _common.StorageBackend ?? string.Empty;
        LogLevel = _common.LogLevel ?? string.Empty;
        // Read-only display of a launch-time input — see the _ollamaHost doc.
        OllamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? string.Empty;

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
#pragma warning disable RS0030 // Sync settings build from ctor path; auth probe is a fast local check and failure is caught below. Catalogued in BannedSymbols.txt.
                authenticated = _authResolver.ResolveApiKeyAsync(id).GetAwaiter().GetResult().IsSuccess;
#pragma warning restore RS0030
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Settings auth resolution threw for {Provider}", id);
            }

            ProviderConfigs.Add(new ProviderConfigViewModel(
                id, displayName, apiKey, requiresKey, authenticated,
                _commonStore, _providers, _toasts,
                _loggerFactory.CreateLogger<ProviderConfigViewModel>(), _authResolver));
        }
    }

    /// <summary>
    ///     Save settings: persist CommonConfig + AvaloniaConfig to disk, apply
    ///     the theme immediately, and emit a success toast.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The user's choice survives a restart because it is in the JSON
    ///         files, which is what the next launch reads. This method writes
    ///         nothing else — in particular it does NOT touch
    ///         <c>Environment</c> (#677). An earlier revision mirrored the
    ///         values into <c>HARBOR_MODEL</c> / <c>HARBOR_STORAGE</c> /
    ///         <c>HARBOR_LOGLEVEL</c> / <c>OLLAMA_HOST</c>, which is process
    ///         state: every one of those names is read once while
    ///         <c>AddHarbor</c> composes, so the write reached nothing and
    ///         outlived the session in a way the user cannot see or undo.
    ///     </para>
    ///     <para>
    ///         An unset <see cref="StorageBackend" /> is written back as unset.
    ///         That is the point: the next launch falls through to the
    ///         composition preset, and this screen is not the thing that picks a
    ///         backend.
    ///     </para>
    /// </remarks>
    [RelayCommand]
    private async Task SaveAsync()
    {
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
            // ollamaHost is logged for traceability only: it is a launch-time
            // input, not something this save can change.
            _logger.LogInformation(
                "Settings saved: theme={Theme}, provider={Provider}, model={Model}, storage={Storage}, log={LogLevel}, font={Font}, ollamaHost={OllamaHost}",
                ThemeSettings.Theme, DefaultProvider, DefaultModel, StorageBackend, LogLevel, FontFamily, OllamaHost);
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
        ThemeSettings.Theme = _common.Theme ?? string.Empty;
        DefaultProvider = _common.DefaultProvider ?? string.Empty;
        DefaultModel = _common.DefaultModel ?? string.Empty;
        FontFamily = _app.FontFamily ?? string.Empty;
        StorageBackend = _common.StorageBackend ?? string.Empty;
        LogLevel = _common.LogLevel ?? string.Empty;
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
