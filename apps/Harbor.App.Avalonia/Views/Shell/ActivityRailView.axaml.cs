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
            Logger.LogError(ex, "Failed to open file: {Path}", node.FullPath);
        }
    }
}

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
public sealed class FileTypeToGeometryConverter : IValueConverter
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
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
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

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
