using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Desktop.Abstractions.ViewModels;
namespace Harbor.App.Wpf.ViewModels;
/// <summary>
///     Provider + model browser view model. Lists known providers, their
///     models, and lets the user pick the active one.
/// </summary>
public sealed partial class ProviderBrowserViewModel : ObservableObject
{

    /// <summary>Chosen model id (set when the user confirms).</summary>
    [ObservableProperty] private string? _chosenModelId;

    /// <summary>Chosen provider id (set when the user confirms).</summary>
    [ObservableProperty] private string? _chosenProviderId;

    /// <summary>Selected model.</summary>
    [ObservableProperty] private ModelEntryViewModel? _selectedModel;

    /// <summary>Selected provider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProviderModels))]
    private ProviderEntryViewModel? _selectedProvider;
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

    /// <summary>Models for the selected provider.</summary>
    public IReadOnlyList<ModelEntryViewModel> SelectedProviderModels
        => SelectedProvider?.Models ?? Array.Empty<ModelEntryViewModel>();

    partial void OnSelectedProviderChanged(ProviderEntryViewModel? value) => SelectedModel = value is not null && value.Models.Count > 0 ? value.Models[0] : null;

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
///     One provider in the browser. Platform projection of the shared
///     <see cref="Harbor.Desktop.Abstractions.ViewModels.ProviderEntryViewModel" /> record
///     (kept in this namespace so the WPF XAML <c>vm:</c> mappings resolve to it).
///     <see cref="Models" /> is re-exposed with the WPF <see cref="ModelEntryViewModel" />
///     element type because the base record types it with the shared element.
/// </summary>
public sealed class ProviderEntryViewModel : Harbor.Desktop.Abstractions.ViewModels.ProviderEntryViewModel
{
    /// <summary>Construct a <see cref="ProviderEntryViewModel" />.</summary>
    public ProviderEntryViewModel(string id, string displayName, string description, IReadOnlyList<ModelEntryViewModel> models)
        : base(id, displayName, description, models) { }

    /// <summary>Models offered by this provider, typed with the WPF element.</summary>
    public new IReadOnlyList<ModelEntryViewModel> Models => (IReadOnlyList<ModelEntryViewModel>)(object)base.Models!;
}

/// <summary>
///     One model offered by a provider. Platform projection of the shared
///     <see cref="Harbor.Desktop.Abstractions.ViewModels.ModelEntryViewModel" /> record
///     (kept in this namespace so the WPF XAML <c>vm:</c> mappings resolve to it).
/// </summary>
public sealed class ModelEntryViewModel : Harbor.Desktop.Abstractions.ViewModels.ModelEntryViewModel
{
    /// <summary>Construct a <see cref="ModelEntryViewModel" />.</summary>
    public ModelEntryViewModel(string id, string displayName, int contextWindow, int maxOutputTokens, bool supportsVision, bool supportsTools)
        : base(id, displayName, contextWindow, maxOutputTokens, supportsVision, supportsTools) { }
}
