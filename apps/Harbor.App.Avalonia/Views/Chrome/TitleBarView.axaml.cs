using Avalonia.Controls;
using Avalonia.Input;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;

namespace Harbor.App.Avalonia.Views.Chrome;

public partial class TitleBarView : UserControl
{
    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private WindowChromeService Chrome => (HostWindow as MainWindow)?.ChromeService
        ?? throw new InvalidOperationException("WindowChromeService is not available.");
}
