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
    private readonly IThemeApplier _theme;
    private readonly ILogger<AvaloniaShellChrome> _logger;

    public AvaloniaShellChrome(
        IContentHost contentHost,
        OverlayController overlayController,
        IThemeApplier theme,
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
        AvaloniaShellChromeLog.ToggleSidebarRequested(_logger);
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

/// <summary>
///     SG12: BCL <c>[LoggerMessage]</c> delegates for <see cref="AvaloniaShellChrome" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AvaloniaShellChromeLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ToggleSidebar requested — no port wired yet.")]
    public static partial void ToggleSidebarRequested(ILogger logger);
}
