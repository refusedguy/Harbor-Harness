using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.Messages;
using Harbor.Application.Configuration;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     First-launch onboarding wizard view-model. Walks the user through
///     provider selection → API key entry → default model → theme → done,
///     then persists the result to <see cref="ICommonConfigStore" /> and
///     raises <see cref="Completed" /> so <c>App.axaml.cs</c> can swap to the
///     main window. Non-blocking: every network call is async with a 5-second
///     timeout and is cancellable when the user closes the wizard.
/// </summary>
public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    /// <summary>Number of steps in the wizard (1-based index, used by the view).</summary>
    public const int TotalSteps = 5;

    /// <summary>
    ///     Model used when nothing is selected/typed — always a keyless local
    ///     provider default from <see cref="ProviderPresets" /> so the wizard
    ///     can complete offline.
    /// </summary>
    public static string OfflineFallbackModel => ProviderPresets.Find("ollama")?.DefaultModel ?? "llama3.2";

    private readonly ICommonConfigStore _configStore;
    private readonly ILogger<OnboardingViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly Harbor.Abstractions.Providers.IProviderHealthCheck? _healthCheck;
    private readonly IThemeService _theme;
    private readonly IToastService _toasts;
    private readonly CancellationTokenSource _wizardCts = new();

    /// <summary>API key currently being entered for the provider on step 3.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    private string _apiKey = string.Empty;

    /// <summary>The current step (1..TotalSteps).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    private int _currentStep = 1;

    /// <summary>Default model id typed/selected on step 4.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    private string _defaultModel = string.Empty;

    /// <summary>True while a background operation (save/test) is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True when the wizard has finished and the view should close.</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>Selected provider for the "default model" dropdown on step 4.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    private OnboardingProviderOption? _selectedProvider;

    /// <summary>Status text shown while testing/saving (e.g. "Saving…").</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    ///     Result of the last "test connection" probe for the selected
    ///     provider (empty until run; never blocks advancing — the check is
    ///     informational, a transient outage must not trap the user).
    /// </summary>
    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    /// <summary>True while the connection probe is in flight.</summary>
    [ObservableProperty]
    private bool _isTestingConnection;

    /// <summary>
    ///     Whether the "Test connection" affordance is available — depends on
    ///     the host supplying an <see cref="Harbor.Abstractions.Providers.IProviderHealthCheck" />.
    ///     Fixed for the VM's lifetime, so a plain one-way binding is enough.
    /// </summary>
    public bool HasConnectionTest => _healthCheck is not null;

    /// <summary>Theme choice on step 5: "dark" / "light" / "system".</summary>
    [ObservableProperty]
    private string _themeChoice = "dark";

    /// <summary>Construct the onboarding wizard view-model.</summary>
    /// <param name="configStore">Common config store the result is persisted to.</param>
    /// <param name="theme">Theme service applied on finish.</param>
    /// <param name="toasts">Toast notifications.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="messenger">Messenger for wizard-completion broadcast.</param>
    /// <param name="healthCheck">
    ///     Optional "test connection" probe (PROD-UI-0 З.2). When present, a
    ///     Test connection button is live on step 3; when absent the UI hides it.
    /// </param>
    public OnboardingViewModel(
        ICommonConfigStore configStore,
        IThemeService theme,
        IToastService toasts,
        ILogger<OnboardingViewModel> logger,
        IMessenger messenger,
        Harbor.Abstractions.Providers.IProviderHealthCheck? healthCheck = null)
    {
        _configStore = configStore;
        _theme = theme;
        _toasts = toasts;
        _logger = logger;
        _messenger = messenger;
        _healthCheck = healthCheck;

        // PROD-UI-0 З.1: single source of truth — the wizard catalogue is
        // derived from <see cref="ProviderPresets" /> (the same presets the
        // CLI wizard and /auth use). No per-VM hardcoded provider list: a new
        // preset automatically appears here. Icons are pure presentation and
        // stay in a small id→glyph map with a generic fallback.
        Providers = new ObservableCollection<OnboardingProviderOption>(
            ProviderPresets.All.Select(p => new OnboardingProviderOption(
                p.Id,
                p.DisplayName,
                p.EnvVarName,
                p.RequiresApiKey,
                p.DefaultModel,
                IconFor(p.Id))));

        // Default-select Ollama (works offline, no key needed) so the user
        // can finish onboarding without typing anything.
        Providers.First(p => p.Id == "ollama").IsSelected = true;
        RefreshSelectedProvider();
    }

    /// <summary>Glyph shown next to a provider row; presentation-only mapping.</summary>
    private static string IconFor(string id) => id switch
    {
        "anthropic" => "🤖",
        "openai" => "🌐",
        "openrouter" => "🛰️",
        "deepseek" => "🐋",
        "groq" => "⚡",
        "mistral" => "🌬️",
        "xai" => "✖️",
        "together" => "🤝",
        "fireworks" => "🎆",
        "cerebras" => "🧠",
        "kilocode" => "⌨️",
        "ollama" => "🦙",
        "vllm" => "🚀",
        _ => "🔧"
    };

    /// <summary>Provider catalogue shown on step 2.</summary>
    public ObservableCollection<OnboardingProviderOption> Providers { get; }

    /// <summary>Human-readable title for the current step.</summary>
    public string StepTitle => CurrentStep switch
    {
        1 => "Welcome to Harbor",
        2 => "Choose your providers",
        3 => "Enter API key",
        4 => "Default model",
        5 => "Theme",
        _ => "Onboarding"
    };

    /// <summary>True when the user can advance to the next step.</summary>
    /// <remarks>
    ///     Validation is intentionally soft — the user can always finish the
    ///     wizard and edit any incomplete field later in Settings. The only
    ///     hard block is step 2 (must pick at least one provider) and step 4
    ///     (must have a default model id — otherwise new sessions have nothing
    ///     to call). Step 3 (API key) is always allowed because Ollama needs
    ///     no key and other providers' keys can be entered later.
    /// </remarks>
    public bool CanAdvance => CurrentStep switch
    {
        2 => Providers.Any(p => p.IsSelected),
        3 => true, // API key is optional — Ollama needs none, others can be added later in Settings.
        4 => !string.IsNullOrWhiteSpace(DefaultModel),
        _ => true
    };

    /// <summary>
    ///     Dispose the wizard's cancellation token source. Called by the
    ///     <see cref="OnboardingWindow" /> when it closes — prevents the CTS
    ///     from leaking across re-runs of the wizard.
    /// </summary>
    public void Dispose()
    {
        _wizardCts.Dispose();
    }

    /// <summary>Recompute <see cref="SelectedProvider" /> + CanAdvance after step-2 checkbox toggles.</summary>
    /// <summary>Advance to the next step (or complete on step 5).</summary>
    [RelayCommand]
    private void Next()
    {
        if (!CanAdvance) return;
        if (CurrentStep >= TotalSteps)
        {
            _ = FinishAsync();
            return;
        }

        // Pre-fill the API key + default model fields when entering those steps
        // so the user only has to confirm/edit.
        if (CurrentStep == 2 && SelectedProvider is not null)
        {
            DefaultModel = SelectedProvider.DefaultModel;
        }

        CurrentStep++;
    }

    /// <summary>Go back to the previous step (no-op on step 1).</summary>
    [RelayCommand]
    private void Back()
    {
        if (CurrentStep <= 1) return;
        CurrentStep--;
    }

    /// <summary>Skip onboarding entirely — closes the wizard with defaults saved.</summary>
    [RelayCommand]
    private async Task Skip()
    {
        try
        {
            string provider = SelectedProvider?.Id ?? "ollama";
            string model = string.IsNullOrWhiteSpace(DefaultModel)
                ? SelectedProvider?.DefaultModel ?? OfflineFallbackModel
                : DefaultModel.Trim();
            string? newKey = SelectedProvider is not null
                             && SelectedProvider.RequiresKey
                             && !string.IsNullOrWhiteSpace(ApiKey)
                ? ApiKey.Trim()
                : null;

            var updateResult = await _configStore.UpdateAsync(cfg =>
            {
                var mergedKeys = cfg.ApiKeys.ToBuilder();
                if (newKey is not null)
                {
                    mergedKeys[provider] = newKey;
                }
                return cfg with
                {
                    OnboardingCompleted = true,
                    ApiKeys = mergedKeys.ToImmutable(),
                    DefaultProvider = string.IsNullOrEmpty(cfg.DefaultProvider) ? provider : cfg.DefaultProvider,
                    DefaultModel = string.IsNullOrEmpty(cfg.DefaultModel) ? model : cfg.DefaultModel,
                    StorageBackend = string.IsNullOrEmpty(cfg.StorageBackend) ? "jsonl" : cfg.StorageBackend
                };
            }, _wizardCts.Token).ConfigureAwait(true);

            if (updateResult.IsFailure)
            {
                _logger.LogWarning("Onboarding skip save failed: {Error}", updateResult.Error);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Onboarding skip cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Onboarding skip failed");
        }

        IsCompleted = true;
        _messenger.Send(new OnboardingCompletedMessage(IsCompleted));
    }

    /// <summary>Recompute <see cref="SelectedProvider" /> + CanAdvance after step-2 checkbox toggles.</summary>
    [RelayCommand]
    private void RefreshSelectedProvider()
    {
        SelectedProvider = Providers.FirstOrDefault(p => p.IsSelected)
                           ?? Providers.FirstOrDefault();
        if (SelectedProvider is not null && string.IsNullOrEmpty(DefaultModel))
        {
            DefaultModel = SelectedProvider.DefaultModel;
        }
    }

    /// <summary>
    ///     PROD-UI-0 З.2: probe the selected provider with a cheap models-list
    ///     request and surface a classified, human-readable verdict. Never
    ///     blocks advancing — failures are informational (may be transient).
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken ct)
    {
        if (_healthCheck is null || IsTestingConnection) return;
        string? providerId = SelectedProvider?.Id;
        if (providerId is null) return;

        var pid = Harbor.Abstractions.Models.Identifiers.ProviderId.TryCreate(providerId);
        if (pid.IsFailure)
        {
            ConnectionStatus = "Invalid provider id.";
            return;
        }

        IsTestingConnection = true;
        ConnectionStatus = $"Testing connection to {providerId}…";
        try
        {
            var result = await _healthCheck.CheckAsync(pid.Value, ct).ConfigureAwait(true);
            ConnectionStatus = result.IsSuccess
                ? $"✓ Connection OK — {result.Value.ModelsCount} model(s), {result.Value.LatencyMs} ms."
                : $"⚠ {result.Error}";
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "Connection test cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for {Provider}", providerId);
            ConnectionStatus = $"⚠ Connection test failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    /// <summary>
    ///     Persist the onboarding result to <c>~/.harbor/config.json</c> and
    ///     raise <see cref="Completed" />. Non-blocking: returns immediately
    ///     on the UI thread; the await chain is fire-and-forget.
    /// </summary>
    private async Task FinishAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Saving configuration…";
        try
        {
            // The wizard collects an API key for the currently-selected provider
            // only (step 3 binds the TextBox to ApiKey + SelectedProvider). Any
            // other selected providers keep their existing key in config — the
            // user can fill them in later via Settings. We MERGE with the
            // existing ApiKeys dictionary (not replace) so re-running the
            // wizard for a second provider doesn't wipe the first one's key.
            string provider = SelectedProvider?.Id ?? "ollama";
            string model = string.IsNullOrWhiteSpace(DefaultModel)
                ? SelectedProvider?.DefaultModel ?? OfflineFallbackModel
                : DefaultModel.Trim();
            string? newKey = SelectedProvider is not null
                             && SelectedProvider.RequiresKey
                             && !string.IsNullOrWhiteSpace(ApiKey)
                ? ApiKey.Trim()
                : null;

            var updateResult = await _configStore.UpdateAsync(cfg =>
            {
                var mergedKeys = cfg.ApiKeys.ToBuilder();
                if (newKey is not null)
                {
                    mergedKeys[provider] = newKey;
                }
                return cfg with
                {
                    OnboardingCompleted = true,
                    ApiKeys = mergedKeys.ToImmutable(),
                    DefaultProvider = provider,
                    DefaultModel = model,
                    StorageBackend = string.IsNullOrEmpty(cfg.StorageBackend) ? "jsonl" : cfg.StorageBackend
                };
            }, _wizardCts.Token).ConfigureAwait(true);

            if (updateResult.IsFailure)
            {
                _logger.LogError("Onboarding save failed: {Error}", updateResult.Error);
                _toasts.Show($"Could not save configuration: {updateResult.Error}", ToastKind.Error);
                StatusText = string.Empty;
                return;
            }

            // Apply the chosen theme immediately so the main window opens
            // with it (and the user sees their choice reflected).
            switch ((ThemeChoice ?? "dark").ToLowerInvariant())
            {
                case "light":
                    _theme.ApplyLight();
                    break;
                case "system":
                    _logger.LogInformation("Onboarding theme 'system' — leaving default (dark) active.");
                    break;
                default:
                    _theme.ApplyDark();
                    break;
            }

            _toasts.Show("Onboarding complete — welcome to Harbor!", ToastKind.Success);
            IsCompleted = true;
            _messenger.Send(new OnboardingCompletedMessage(IsCompleted));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Onboarding cancelled by user.");
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding finish failed");
            _toasts.Show($"Onboarding failed: {ex.Message}", ToastKind.Error);
            StatusText = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
