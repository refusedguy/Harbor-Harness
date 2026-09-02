using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Harbor.App.Avalonia.ViewModels.Terminal;
using Harbor.App.Avalonia.Views.Terminal;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Opens and tracks floating PTY terminal windows. Each window holds one
///     or two panes; panes are session-aware (own shell process, working
///     directory, environment and history) and survive as long as the window.
/// </summary>
public interface IFloatingTerminals
{
    /// <summary>Show a floating terminal pane rooted at <paramref name="workingDirectory" /> (null → project dir, second pane defaults to /tmp).</summary>
    void ShowPane(string? workingDirectory = null);

    /// <summary>Open a pane if none is visible, otherwise close the newest one.</summary>
    void TogglePane();
}

public sealed class FloatingTerminalService : IFloatingTerminals
{
    private readonly IDispatcherAdapter _dispatcher;
    private readonly Func<FloatingTerminalViewModel> _viewModelFactory;
    private readonly ILogger<FloatingTerminalService> _logger;
    private readonly List<FloatingTerminalPaneWindow> _windows = [];

    public FloatingTerminalService(
        IDispatcherAdapter dispatcher,
        Func<FloatingTerminalViewModel> viewModelFactory,
        ILogger<FloatingTerminalService> logger)
    {
        _dispatcher = dispatcher;
        _viewModelFactory = viewModelFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public void ShowPane(string? workingDirectory = null)
        => _dispatcher.Post(() =>
        {
            global::Avalonia.Controls.Window? owner =
                (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            FloatingTerminalViewModel vm = _viewModelFactory();
            if (workingDirectory is not null && vm.Panes.Count > 0)
            {
                // Replace the auto-created first pane's intent: open a pane for the requested directory.
                vm.Panes[0].Dispose();
                vm.Panes.Clear();
                vm.OpenPane(workingDirectory);
            }

            var window = new FloatingTerminalPaneWindow { DataContext = vm };
            _windows.Add(window);
            window.Closed += (_, _) => _windows.Remove(window);
            if (owner is not null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }

            _logger.LogInformation("Floating terminal window opened (cwd={Cwd})", workingDirectory ?? vm.Panes[0].WorkingDirectory);
        });

    /// <inheritdoc />
    public void TogglePane()
    {
        if (_windows.Count > 0)
        {
            _dispatcher.Post(() =>
            {
                FloatingTerminalPaneWindow? last = _windows[^1];
                _windows.Remove(last);
                last.Close();
            });
        }
        else
        {
            ShowPane(null);
        }
    }
}
