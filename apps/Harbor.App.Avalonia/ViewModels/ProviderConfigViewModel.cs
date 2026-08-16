using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
using Harbor.Abstractions.Providers;
namespace Harbor.App.Avalonia.ViewModels;
/// <summary>
///     Per-provider configuration row view-model. Inherits shared
///     <see cref="ProviderConfigViewModel" /> from Desktop.Abstractions;
///     this subclass exists only to preserve the Avalonia namespace for XAML
///     DataTemplates.
/// </summary>
public sealed partial class ProviderConfigViewModel : Harbor.Desktop.Abstractions.ViewModels.ProviderConfigViewModel
{
    public ProviderConfigViewModel(
        string id,
        string displayName,
        string apiKey,
        bool requiresApiKey,
        bool isAuthenticated,
        ICommonConfigStore commonStore,
        IProviderRegistry providers,
        IToastService toasts,
        ILogger<ProviderConfigViewModel> logger)
        : base(id, displayName, apiKey, requiresApiKey, isAuthenticated, commonStore, providers, toasts, logger)
    {
    }
}
