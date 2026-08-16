using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Messaging;

namespace Harbor.App.Avalonia.ViewModels;

public sealed partial class OnboardingViewModel : Harbor.Desktop.Abstractions.ViewModels.OnboardingViewModel
{
    public OnboardingViewModel(
        ICommonConfigStore configStore,
        IThemeService theme,
        IToastService toasts,
        ILogger<OnboardingViewModel> logger,
        IMessenger messenger)
        : base(configStore, theme, toasts, logger, messenger)
    {
    }
}
