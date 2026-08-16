using Harbor.App.Avalonia.Navigation;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Services;

internal sealed class AvaloniaShellChrome : IShellChrome
{
    private readonly IContentHost _contentHost;
    private readonly OverlayController _overlayController;
    private readonly IThemeService _theme;
    private readonly ILogger<AvaloniaShellChrome> _logger;

    public AvaloniaShellChrome(
        IContentHost contentHost,
        OverlayController overlayController,
        IThemeService theme,
        ILogger<AvaloniaShellChrome> logger)
    {
        _contentHost = contentHost;
        _overlayController = overlayController;
        _theme = theme;
        _logger = logger;
    }

    public void Navigate(string route)
    {
        _contentHost.TryNavigate(route);
    }

    public void ToggleSidebar()
    {
        _logger.LogDebug("ToggleSidebar requested — no port wired yet.");
    }

    public void OpenOverlay(string id)
    {
        _overlayController.Open(id);
    }

    public void CloseOverlay(string id)
    {
        _overlayController.Close(id);
    }

    public bool CloseTopOverlay()
    {
        return _overlayController.CloseTop();
    }

    public void ToggleTheme()
    {
        _theme.Toggle();
    }
}
