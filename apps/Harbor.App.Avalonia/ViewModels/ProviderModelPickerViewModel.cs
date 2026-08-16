using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Messaging;
using Harbor.Abstractions.Providers;

namespace Harbor.App.Avalonia.ViewModels;

public sealed partial class ProviderModelPickerViewModel : Harbor.Desktop.Abstractions.ViewModels.ProviderModelPickerViewModel
{
    public ProviderModelPickerViewModel(
        IProviderRegistry providers,
        Harbor.Desktop.Abstractions.Configuration.ICommonConfigStore configStore,
        ISessionManager sessions,
        IToastService toasts,
        ILogger<ProviderModelPickerViewModel> logger,
        IMessenger messenger)
        : base(providers, configStore, sessions, toasts, logger, messenger)
    {
    }
}