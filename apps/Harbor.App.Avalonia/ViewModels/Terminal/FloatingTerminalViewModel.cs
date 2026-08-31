using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harbor.Terminal.Pty;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels.Terminal;

/// <summary>Layout mode of the floating terminal window.</summary>
public enum TerminalLayout
{
    /// <summary>One pane fills the window.</summary>
    Single,

    /// <summary>Two panes side by side.</summary>
    SplitHorizontal,

    /// <summary>Two panes stacked (top/bottom).</summary>
    SplitVertical,
}

/// <summary>
///     Window-level view-model for the floating terminal: owns the pane list
///     (1–2 PTY panes), the split/stack layout mode, and pane lifecycle.
/// </summary>
public sealed partial class FloatingTerminalViewModel : ObservableObject
{
    private readonly IDispatcherAdapter _dispatcher;
    private readonly ILogger<FloatingTerminalViewModel> _logger;
    private readonly Func<string?, TerminalPaneViewModel> _paneFactory;

    [ObservableProperty]
    private TerminalLayout _layout = TerminalLayout.SplitHorizontal;

    /// <summary>Live PTY panes, in creation order (max 2).</summary>
    public ObservableCollection<TerminalPaneViewModel> Panes { get; } = [];

    public FloatingTerminalViewModel(
        IDispatcherAdapter dispatcher,
        ILogger<FloatingTerminalViewModel> logger,
        Func<string?, TerminalPaneViewModel> paneFactory)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _paneFactory = paneFactory;
        OpenPane(null); // first pane: the project directory
    }

    /// <summary>Open a pane rooted at <paramref name="workingDirectory" /> (null → alternate between project dir and /tmp for the second pane).</summary>
    [RelayCommand]
    public void OpenPane(string? workingDirectory = null)
    {
        if (Panes.Count >= 2) return;
        workingDirectory ??= PickAlternateDirectory();
        var pane = _paneFactory(workingDirectory);
        Panes.Add(pane);
        if (Panes.Count == 2 && Layout == TerminalLayout.Single)
        {
            Layout = TerminalLayout.SplitHorizontal;
        }

        _logger.LogInformation("Terminal pane added: {Count} pane(s), cwd={Cwd}", Panes.Count, workingDirectory);
    }

    /// <summary>Split horizontally (side-by-side) — opens a second pane if needed.</summary>
    [RelayCommand]
    private void SplitHorizontal()
    {
        if (Panes.Count < 2) OpenPane(null);
        Layout = TerminalLayout.SplitHorizontal;
    }

    /// <summary>Stack vertically (top/bottom) — opens a second pane if needed.</summary>
    [RelayCommand]
    private void SplitVertical()
    {
        if (Panes.Count < 2) OpenPane(null);
        Layout = TerminalLayout.SplitVertical;
    }

    /// <summary>Back to a single pane (hides the second without killing it).</summary>
    [RelayCommand]
    private void Single() => Layout = TerminalLayout.Single;

    /// <summary>Close the newest pane and dispose its PTY.</summary>
    [RelayCommand]
    private void CloseLastPane()
    {
        if (Panes.Count == 0) return;
        TerminalPaneViewModel pane = Panes[^1];
        Panes.Remove(pane);
        pane.Dispose();
        if (Panes.Count == 1)
        {
            Layout = TerminalLayout.Single;
        }
    }

    /// <summary>Second pane default: a temp directory when the first pane is the project dir, otherwise the project dir.</summary>
    private string PickAlternateDirectory()
    {
        string current = Environment.CurrentDirectory;
        string first = Panes.Count > 0 ? Panes[0].WorkingDirectory : current;
        return Path.TrimEndingDirectorySeparator(first) == Path.TrimEndingDirectorySeparator(current)
            ? Path.TrimEndingDirectorySeparator(Path.GetTempPath())
            : current;
    }
}
