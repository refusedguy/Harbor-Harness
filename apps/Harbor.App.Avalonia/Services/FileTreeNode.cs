using System.Collections.ObjectModel;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     One row of the desktop shell's file-tree sidebar, as the Avalonia
///     <c>TreeView</c> needs it.
/// </summary>
/// <remarks>
/// <para>
///     Moved out of <c>ViewModels/MainViewModel.cs</c> by #492 for the same
///     reason the scan left it: a model that only the file tree uses does not
///     belong to the type that happens to own the window. Its own file is what
///     lets <see cref="ProjectFileTreeScanner" /> build a tree without naming
///     the view-model it used to be a nested class of.
/// </para>
/// <para>
///     Mutable by design — the sidebar expands and collapses rows, and
///     <see cref="IsExpanded" /> is bound two-way to the expander glyph. Nothing
///     else on a node is written after the scan returns.
/// </para>
/// <para>
///     <see cref="IconPath" /> is a CATEGORY name, not a glyph path: the shape
///     of the icon is a Presentation decision made by
///     <c>FileTypeToGeometryConverter</c>, and what counts as a code file is
///     policy (<see cref="Harbor.Abstractions.Filesystem.IFileTreePolicy" />).
/// </para>
/// </remarks>
public class FileTreeNode
{
    /// <summary>Bare display name. For a directory the view appends its own separator.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path, used when the user opens the row.</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>Whether this row is a directory (and therefore expandable).</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Bound two-way to the expander glyph; written by the view, not the scan.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>Child rows, populated by the scan up to its depth budget.</summary>
    public ObservableCollection<FileTreeNode> Children { get; } = new();

    /// <summary>Icon category from the file-tree policy — never a glyph path.</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>Optional VCS marker for the row. Unused by the default policy.</summary>
    public string? GitStatus { get; set; }
}
