using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Services;
using Harbor.Desktop.Abstractions.Configuration;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     First-launch onboarding wizard view-model. Walks the user through
///     provider selection → API key entry → default model → theme → done,
///     then persists the result to <see cref="ICommonConfigStore"/> and
///     raises <see cref="Completed"/> so <c>App.axaml.cs</c> can swap to the
///     main window. Non-blocking: every network call is async with a 5-second
///     timeout and is cancellable when the user closes the wizard.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    /// <summary>Number of steps in the wizard (1-based index, used by the view).</summary>
    public const int TotalSteps = 5;

    private readonly ICommonConfigStore _configStore;
    private readonly ThemeService _theme;
    private readonly ToastService _toasts;
    private readonly ILogger<OnboardingViewModel> _logger;
    private readonly CancellationTokenSource _wizardCts = new();

    /// <summary>Construct the onboarding wizard view-model.</summary>
    public OnboardingViewModel(
        ICommonConfigStore configStore,
        ThemeService theme,
        ToastService toasts,
        ILogger<OnboardingViewModel> logger)
    {
        _configStore = configStore;
        _theme = theme;
        _toasts = toasts;
        _logger = logger;

        // Static catalogue of providers the wizard knows about. Each entry
        // declares whether an API key is required (Ollama is the only one that
        // doesn't need one). The user picks a subset via checkboxes; the
        // wizard then collects keys + a default model + theme.
        Providers =
        [
            new OnboardingProviderOption("anthropic",  "Anthropic",   "ANTHROPIC_API_KEY",   requiresKey: true,  defaultModel: "claude-sonnet-4-20250514", icon: "🤖"),
            new OnboardingProviderOption("openai",     "OpenAI",      "OPENAI_API_KEY",      requiresKey: true,  defaultModel: "gpt-4o",                   icon: "🌐"),
            new OnboardingProviderOption("openrouter", "OpenRouter",  "OPENROUTER_API_KEY",  requiresKey: true,  defaultModel: "anthropic/claude-sonnet-4", icon: "🛰️"),
            new OnboardingProviderOption("deepseek",   "DeepSeek",    "DEEPSEEK_API_KEY",    requiresKey: true,  defaultModel: "deepseek-chat",            icon: "🐋"),
            new OnboardingProviderOption("groq",       "Groq",        "GROQ_API_KEY",        requiresKey: true,  defaultModel: "llama-3.3-70b-versatile",  icon: "⚡"),
            new OnboardingProviderOption("mistral",    "Mistral",     "MISTRAL_API_KEY",     requiresKey: true,  defaultModel: "mistral-large-latest",     icon: "🌬️"),
            new OnboardingProviderOption("xai",        "xAI",         "XAI_API_KEY",         requiresKey: true,  defaultModel: "grok-3",                   icon: "✖️"),
            new OnboardingProviderOption("together",   "Together AI", "TOGETHER_API_KEY",    requiresKey: true,  defaultModel: "meta-llama/Llama-3.3-70B-Instruct-Turbo", icon: "🤝"),
            new OnboardingProviderOption("fireworks",  "Fireworks",   "FIREWORKS_API_KEY",   requiresKey: true,  defaultModel: "accounts/fireworks/models/llama-v3p1-70b-instruct", icon: "🎆"),
            new OnboardingProviderOption("cerebras",   "Cerebras",    "CEREBRAS_API_KEY",    requiresKey: true,  defaultModel: "llama-3.3-70b",            icon: "🧠"),
            new OnboardingProviderOption("kilocode",   "Kilo Code",   "KILO_API_KEY",        requiresKey: true,  defaultModel: "tencent/hy3:free",         icon: "⌨️"),
            new OnboardingProviderOption("ollama",     "Ollama (local)", null,               requiresKey: false, defaultModel: "qwen2.5-coder:7b",         icon: "🦙"),
        ];

        // Default-select Ollama (works offline, no key needed) so the user
        // can finish onboarding without typing anything.
        Providers.First(p => p.Id == "ollama").IsSelected = true;
        RefreshSelectedProvider();
    }

    /// <summary>
    ///     Dispose the wizard's cancellation token source. Called by the
    ///     <see cref="OnboardingWindow"/> when it closes — prevents the CTS
    ///     from leaking across re-runs of the wizard.
    /// </summary>
    public void Dispose()
    {
        _wizardCts.Cancel();
        _wizardCts.Dispose();
    }

    /// <summary>Provider catalogue shown on step 2.</summary>
    public ObservableCollection<OnboardingProviderOption> Providers { get; }

    /// <summary>Selected provider for the "default model" dropdown on step 4.</summary>
    [ObservableProperty]
    private OnboardingProviderOption? _selectedProvider;

    /// <summary>The current step (1..TotalSteps).</summary>
    [ObservableProperty]
    private int _currentStep = 1;

    /// <summary>API key currently being entered for the provider on step 3.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    private string _apiKey = string.Empty;

    /// <summary>Default model id typed/selected on step 4.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    private string _defaultModel = string.Empty;

    /// <summary>Theme choice on step 5: "dark" / "light" / "system".</summary>
    [ObservableProperty]
    private string _themeChoice = "dark";

    /// <summary>Status text shown while testing/saving (e.g. "Saving…").</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>True while a background operation (save/test) is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True when the wizard has finished and the view should close.</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>Raised when the user completes onboarding. App.axaml.cs swaps to MainWindow.</summary>
    public event EventHandler? Completed;

    /// <summary>Human-readable title for the current step.</summary>
    public string StepTitle => CurrentStep switch
    {
        1 => "Welcome to Harbor",
        2 => "Choose your providers",
        3 => "Enter API key",
        4 => "Default model",
        5 => "Theme",
        _ => "Onboarding",
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
        _ => true,
    };

    /// <summary>
    ///     Re-raise <see cref="CanAdvance"/> when any of the inputs that affect
    ///     it change. Without these, the Next button's <c>IsEnabled</c> binding
    ///     never refreshes after the user types in the API-key / model field,
    ///     so the button appears stuck (the bug: "в wizard не работает next
    ///     если ключ ввести").
    /// </summary>
    partial void OnApiKeyChanged(string value) => OnPropertyChanged(nameof(CanAdvance));
    partial void OnDefaultModelChanged(string value) => OnPropertyChanged(nameof(CanAdvance));
    partial void OnSelectedProviderChanged(OnboardingProviderOption? value) => OnPropertyChanged(nameof(CanAdvance));
    partial void OnCurrentStepChanged(int value) => OnPropertyChanged(nameof(CanAdvance));

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
        OnPropertyChanged(nameof(CanAdvance));
    }

    /// <summary>Go back to the previous step (no-op on step 1).</summary>
    [RelayCommand]
    private void Back()
    {
        if (CurrentStep <= 1) return;
        CurrentStep--;
        OnPropertyChanged(nameof(CanAdvance));
    }

    /// <summary>Skip onboarding entirely — closes the wizard without saving.</summary>
    [RelayCommand]
    private void Skip()
    {
        _wizardCts.Cancel();
        IsCompleted = true;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Recompute <see cref="SelectedProvider"/> + CanAdvance after step-2 checkbox toggles.</summary>
    [RelayCommand]
    private void RefreshSelectedProvider()
    {
        SelectedProvider = Providers.FirstOrDefault(p => p.IsSelected)
            ?? Providers.FirstOrDefault();
        if (SelectedProvider is not null && string.IsNullOrEmpty(DefaultModel))
        {
            DefaultModel = SelectedProvider.DefaultModel;
        }
        OnPropertyChanged(nameof(CanAdvance));
    }

    /// <summary>
    ///     Persist the onboarding result to <c>~/.harbor/config.json</c> and
    ///     raise <see cref="Completed"/>. Non-blocking: returns immediately
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
            var provider = SelectedProvider?.Id ?? "ollama";
            var model = string.IsNullOrWhiteSpace(DefaultModel)
                ? (SelectedProvider?.DefaultModel ?? "qwen2.5-coder:7b")
                : DefaultModel.Trim();
            var newKey = (SelectedProvider is not null
                          && SelectedProvider.RequiresKey
                          && !string.IsNullOrWhiteSpace(ApiKey))
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
                    StorageBackend = string.IsNullOrEmpty(cfg.StorageBackend) ? "jsonl" : cfg.StorageBackend,
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
            Completed?.Invoke(this, EventArgs.Empty);
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

/// <summary>
///     One provider row on step 2 of the onboarding wizard. Bindable:
///     <see cref="IsSelected"/> is a two-way checkbox; the wizard reads the
///     checked set on <c>Next</c>.
/// </summary>
public sealed partial class OnboardingProviderOption : ObservableObject
{
    /// <summary>Provider id (matches <c>CommonConfig.ApiKeys</c> keys + provider JSON files).</summary>
    public string Id { get; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; }

    /// <summary>Name of the env var that holds the API key (shown as a hint), or null when no key is needed.</summary>
    public string? AuthEnvVar { get; }

    /// <summary>True when this provider requires an API key (i.e. not Ollama).</summary>
    public bool RequiresKey { get; }

    /// <summary>Suggested default model id for this provider.</summary>
    public string DefaultModel { get; }

    /// <summary>Emoji icon shown next to the provider name in the wizard.</summary>
    public string Icon { get; }

    /// <summary>Construct a provider option.</summary>
    public OnboardingProviderOption(string id, string displayName, string? authEnvVar, bool requiresKey, string defaultModel, string icon = "🔧")
    {
        Id = id;
        DisplayName = displayName;
        AuthEnvVar = authEnvVar;
        RequiresKey = requiresKey;
        DefaultModel = defaultModel;
        Icon = icon;
    }

    /// <summary>Two-way bound checkbox state.</summary>
    [ObservableProperty]
    private bool _isSelected;
}
