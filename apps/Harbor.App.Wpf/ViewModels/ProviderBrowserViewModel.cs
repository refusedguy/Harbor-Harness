using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Harbor.App.Wpf.ViewModels;

/// <summary>
///     Provider + model browser view model. Lists known providers, their
///     models, and lets the user pick the active one.
/// </summary>
public sealed partial class ProviderBrowserViewModel : ObservableObject
{
    /// <summary>Construct a <see cref="ProviderBrowserViewModel" />.</summary>
    public ProviderBrowserViewModel()
    {
        Providers = new ObservableCollection<ProviderEntryViewModel>
        {
            new("anthropic", "Anthropic", "Claude family", new[]
            {
                new ModelEntryViewModel("claude-3-5-sonnet-20241022", "Claude 3.5 Sonnet", 200_000, 8_192, true, true),
                new ModelEntryViewModel("claude-3-5-haiku-20241022", "Claude 3.5 Haiku", 200_000, 8_192, true, true),
                new ModelEntryViewModel("claude-3-opus-20240229", "Claude 3 Opus", 200_000, 4_096, true, true)
            }),
            new("openai", "OpenAI", "GPT-4 / GPT-4o family", new[]
            {
                new ModelEntryViewModel("gpt-4o", "GPT-4o", 128_000, 16_384, true, true),
                new ModelEntryViewModel("gpt-4o-mini", "GPT-4o mini", 128_000, 16_384, true, true),
                new ModelEntryViewModel("o1", "o1", 200_000, 100_000, false, false)
            }),
            new("openrouter", "OpenRouter", "Aggregator", Array.Empty<ModelEntryViewModel>()),
            new("ollama", "Ollama", "Local models", Array.Empty<ModelEntryViewModel>()),
            new("groq", "Groq", "Fast inference", Array.Empty<ModelEntryViewModel>())
        };

        SelectedProvider = Providers[0];
        SelectedModel = SelectedProvider.Models.Count > 0 ? SelectedProvider.Models[0] : null;
    }

    /// <summary>Providers list.</summary>
    public ObservableCollection<ProviderEntryViewModel> Providers { get; }

    /// <summary>Selected provider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProviderModels))]
    private ProviderEntryViewModel? _selectedProvider;

    /// <summary>Models for the selected provider.</summary>
    public IReadOnlyList<ModelEntryViewModel> SelectedProviderModels
        => SelectedProvider?.Models ?? Array.Empty<ModelEntryViewModel>();

    /// <summary>Selected model.</summary>
    [ObservableProperty] private ModelEntryViewModel? _selectedModel;

    /// <summary>Chosen provider id (set when the user confirms).</summary>
    [ObservableProperty] private string? _chosenProviderId;

    /// <summary>Chosen model id (set when the user confirms).</summary>
    [ObservableProperty] private string? _chosenModelId;

    partial void OnSelectedProviderChanged(ProviderEntryViewModel? value)
    {
        SelectedModel = value is not null && value.Models.Count > 0 ? value.Models[0] : null;
    }

    /// <summary>Confirm the selection and close the dialog.</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (SelectedProvider is null || SelectedModel is null) return;
        ChosenProviderId = SelectedProvider.Id;
        ChosenModelId = SelectedModel.Id;
    }
}

/// <summary>
///     One provider in the browser.
/// </summary>
/// <param name="Id">Provider id.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Models">Models offered by this provider.</param>
public sealed record ProviderEntryViewModel(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<ModelEntryViewModel> Models);

/// <summary>
///     One model offered by a provider.
/// </summary>
/// <param name="Id">Model id.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="ContextWindow">Max input tokens.</param>
/// <param name="MaxOutputTokens">Max output tokens.</param>
/// <param name="SupportsVision">Whether the model accepts image inputs.</param>
/// <param name="SupportsTools">Whether the model supports tool calls.</param>
public sealed record ModelEntryViewModel(
    string Id,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsVision,
    bool SupportsTools)
{
    /// <summary>Summary text for display.</summary>
    public string Summary =>
        $"{ContextWindow / 1000}K ctx · {MaxOutputTokens / 1000}K out" +
        (SupportsVision ? " · vision" : "") +
        (SupportsTools ? " · tools" : "");
}
