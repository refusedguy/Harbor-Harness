using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Harbor.App.Avalonia.Hosting;
using Harbor.App.Avalonia.Services;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Application.Filesystem;
using Harbor.Ui.Framework.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Harbor.App.Avalonia.Views.Shell;

public partial class ActivityRailView : UserControl
{
    private IShellChrome? _shellChrome;
    private IShellChrome ShellChrome => _shellChrome ??= ShellLocator.Of(this).Get<IShellChrome>();

    private CodeEditorViewModel? _codeEditor;
    private CodeEditorViewModel CodeEditor => _codeEditor ??= ShellLocator.Of(this).Get<CodeEditorViewModel>();

    private ILogger<ActivityRailView>? _logger;
    private ILogger<ActivityRailView> Logger => _logger ??= ShellLocator.Of(this).Get<ILogger<ActivityRailView>>();

    private IFloatingTerminals? _terminals;
    private IFloatingTerminals Terminals => _terminals ??= ShellLocator.Of(this).Get<IFloatingTerminals>();

    public ActivityRailView()
    {
        InitializeComponent();
        MinWidth = 56;

        DataContextChanged += (_, __) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible))
                    {
                        UpdateToggleButtonState();
                    }
                };
            }
        };
    }

    private void UpdateToggleButtonState()
    {
        if (Rail_ToggleButton is not { } button) return;

        if (button.RenderTransform is not RotateTransform icon)
        {
            icon = new RotateTransform();
            button.RenderTransform = icon;
        }

        var isExpanded = DataContext is MainViewModel vm && vm.IsSidebarVisible;
        icon.Angle = isExpanded ? 180 : 0;
        icon.CenterX = 8;
        icon.CenterY = 8;
    }

    private void MarkActive(object? sender)
    {
        if (sender is not Button clicked) return;

        foreach (var button in new[] { Rail_BoardButton, Rail_SearchButton, Rail_DiffButton })
        {
            if (button is null) continue;

            if (ReferenceEquals(button, clicked))
            {
                button.Classes.Add("RailButtonActive");
            }
            else
            {
                button.Classes.Remove("RailButtonActive");
            }
        }
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ToggleSidebar();
            UpdateToggleButtonState();
        }
    }

    private void Board_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.OpenOverlay(OverlayIds.SessionsFlyout);
    }

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.OpenOverlay(OverlayIds.Palette);
    }

    private void Diff_Click(object? sender, RoutedEventArgs e)
    {
        MarkActive(sender);
        ShellChrome.ToggleSidebar();
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.ToggleTheme();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        ShellChrome.OpenOverlay(OverlayIds.Settings);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        await vm.RefreshFileTreeAsync();
    }

    // #2682 Z15: the commands only flip flags on FileTreeNode; the two-way
    // style binding in the XAML is what moves the expander glyphs.
    private void ExpandFileTree_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ExpandFileTree();
        }
    }

    private void CollapseFileTree_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CollapseFileTree();
        }
    }

    private async void FileTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TreeView treeView) return;

        if (treeView.SelectedItem is not FileTreeNode node || node.IsDirectory) return;

        try
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SwitchView("code");
            }

            await CodeEditor.LoadFileAsync(node.FullPath);
        }
        catch (Exception ex)
        {
            ActivityRailViewLog.OpenFileFailed(Logger, ex, node.FullPath);
        }
    }

    // #2682 Z20: a right-click records the row the menu was opened on. The
    // MenuItems live in the realized row template, so their Click handlers
    // cannot bind to the view model; this field is the hand-off instead.
    private FileTreeNode? _contextNode;

    private void FileTreeRow_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not Control control) return;

        Control? current = control;
        while (current is not null)
        {
            if (current.DataContext is FileTreeNode node)
            {
                _contextNode = node;
                return;
            }

            current = current.Parent as Control;
        }
    }

    private async void CopyAbsolutePath_Click(object? sender, RoutedEventArgs e)
    {
        await CopyPathAsync(node => FileTreePathLinks.AbsolutePath(node));
    }

    private async void CopyRelativePath_Click(object? sender, RoutedEventArgs e)
    {
        string root = DataContext is MainViewModel vm ? vm.ProjectRootPath : string.Empty;
        await CopyPathAsync(node => FileTreePathLinks.RelativePath(node, root));
    }

    private async void CopyFilePermalink_Click(object? sender, RoutedEventArgs e)
    {
        await CopyPathAsync(node => FileTreePathLinks.Permalink(node));
    }

    private async Task CopyPathAsync(Func<FileTreeNode, string> format)
    {
        FileTreeNode? node = _contextNode;
        _contextNode = null;
        if (node is null) return;

        string text;
        try
        {
            text = format(node);
        }
        catch (Exception ex)
        {
            ActivityRailViewLog.CopyPathFailed(Logger, ex, node.FullPath);
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        try
        {
            // Avalonia 12.1 clipboard API (same shape as CodeBlock's copy
            // button): a DataTransfer with one text item. Headless test runs
            // have no clipboard, so the miss is deliberately not fatal.
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            var transfer = new DataTransfer();
            var item = new DataTransferItem();
            item.SetText(text);
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer).ConfigureAwait(true);
        }
        catch
        {
            // No clipboard in headless CI — the copy is a no-op, not a crash.
        }
    }

    // #2682 Z22: open a terminal pane rooted at the right-clicked row. A
    // directory row targets itself; a file row targets its parent. The service
    // owns the window and marshals to the UI thread, so this handler only has
    // to resolve the directory and hand it over.
    private void OpenInTerminal_Click(object? sender, RoutedEventArgs e)
    {
        FileTreeNode? node = _contextNode;
        _contextNode = null;
        if (node is null) return;

        string root = DataContext is MainViewModel vm ? vm.ProjectRootPath : string.Empty;

        string directory;
        try
        {
            directory = FileTreeTerminalTarget.Directory(node, root);
        }
        catch (Exception ex)
        {
            ActivityRailViewLog.TerminalTargetFailed(Logger, ex, node.FullPath);
            return;
        }

        if (string.IsNullOrEmpty(directory)) return;

        try
        {
            Terminals.ShowPane(directory);
        }
        catch (Exception ex)
        {
            ActivityRailViewLog.TerminalOpenFailed(Logger, ex, directory);
        }
    }
}

/// <summary>
///     Turns the rail's expanded flag into the rail's width.
/// </summary>
/// <remarks>
///     Not a <see cref="OneWayConverterBase{T}" />: the two widths are instance
///     state, set from the XAML that declares the converter
///     (<c>ExpandedWidth</c> / <c>CollapsedWidth</c>). A shared singleton
///     would carry one rail's widths into every other rail, so this converter
///     is declared as a resource and instantiated per view.
/// </remarks>
public sealed class BoolToWidthConverter : IValueConverter
{
    public double ExpandedWidth { get; set; } = 240;
    public double CollapsedWidth { get; set; } = 56;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b) return ExpandedWidth;
        return CollapsedWidth;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Resolves a file-tree row to the HDS glyph for the icon CATEGORY the
///     file-tree policy computed for it.
/// </summary>
/// <remarks>
/// <para>
///     #755. This used to decide the shape from <c>node.IsDirectory</c> and
///     <c>node.IsExpanded</c> alone, carrying its own three copies of the
///     <c>IcFolderOpen</c> / <c>IcFolder</c> / <c>IcFileCode</c> path data. The
///     classification <see cref="FileTreeNode.IconPath" /> holds — source file or
///     not — was therefore never read by anything, and every non-directory row
///     was painted with the code glyph whether or not it was code.
/// </para>
/// <para>
///     The category is the policy's, so this class compares against the policy's
///     own constants rather than re-typing the strings: a rename there breaks
///     this build instead of silently mis-rendering every row. Expansion is
///     still read off the node, because <see cref="FileTreeNode.IsExpanded" /> is
///     view state the scan does not know — the policy classifies files, not how
///     a folder is drawn open or closed.
/// </para>
/// <para>
///     Geometry comes from the ResourceDictionary, not from a string here, so the
///     dictionary stays the single source of truth for iconography
///     (<c>docs/ui/HDS.md</c>; <c>ThemeTokenDuplicationGuardTests</c> enforces it).
/// </para>
/// </remarks>
public sealed class FileTypeToGeometryConverter : OneWayConverterBase<FileTypeToGeometryConverter>
{
    /// <summary>Category a classified (source-ish) file row gets.</summary>
    public const string CodeResourceKey = "IcFileCode";

    /// <summary>Category an unclassified file row gets.</summary>
    public const string FileResourceKey = "IcFile";

    /// <summary>Category a collapsed directory row gets.</summary>
    public const string FolderResourceKey = "IcFolder";

    /// <summary>Category an expanded directory row gets.</summary>
    public const string FolderOpenResourceKey = "IcFolderOpen";

    /// <summary>
    ///     The HDS resource key that paints <paramref name="node" />, from the
    ///     category the policy computed plus the expansion state the view owns.
    /// </summary>
    /// <remarks>
    ///     Public and static so the mapping is assertable without standing up an
    ///     Avalonia <c>Application</c> — the point of #755 is that the
    ///     classification became observable, and that is a claim about this
    ///     function, not about the resource plumbing around it.
    ///     <para>
    ///         Total by construction: a category no shipped policy emits still
    ///         resolves, to the generic document, rather than to no icon at all.
    ///     </para>
    /// </remarks>
    public static string IconResourceKeyFor(FileTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.IconPath switch
        {
            DefaultFileTreePolicy.FolderIcon => node.IsExpanded ? FolderOpenResourceKey : FolderResourceKey,
            DefaultFileTreePolicy.CodeIcon => CodeResourceKey,
            _ => FileResourceKey,
        };
    }

    /// <inheritdoc />
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileTreeNode node) return null;
        if (global::Avalonia.Application.Current is null) return null;

        // TryGetResource, not the direct indexer: it walks the merged
        // dictionaries, which is where Icons.axaml lives (App.axaml cascade
        // slot [2]). Same lookup, and same reason, as Views/Converters.cs.
        return global::Avalonia.Application.Current.TryGetResource(
                   IconResourceKeyFor(node), null, out object? resource)
            ? resource
            : null;
    }
}

/// <summary>
///     SG14: BCL <c>[LoggerMessage]</c> delegates for <see cref="ActivityRailView" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ActivityRailViewLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to open file: {Path}")]
    public static partial void OpenFileFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to format a copy-path reference for: {Path}")]
    public static partial void CopyPathFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to resolve terminal directory for: {Path}")]
    public static partial void TerminalTargetFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to open a terminal at: {Directory}")]
    public static partial void TerminalOpenFailed(ILogger logger, Exception ex, string directory);
}
