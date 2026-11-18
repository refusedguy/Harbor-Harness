namespace Harbor.App.Avalonia.Services;

/// <summary>
///     #2682 Z15: expand/collapse operations for the desktop file tree.
/// </summary>
/// <remarks>
/// <para>
///     The shell's file tree is a plain <see cref="FileTreeNode" /> graph whose
///     <see cref="FileTreeNode.IsExpanded" /> flag is the single source of truth
///     for both the expander glyph and the HDS folder icon. Keeping the
///     recursive walk here makes the behavior assertable without an Avalonia
///     <c>Application</c>: the tests build a node graph and check the flags.
/// </para>
/// <para>
///     <see cref="CollapseAll" /> deliberately leaves the supplied roots open.
///     The shell passes the project root as its only root, so collapsing that
///     too would hide every entry behind a single row and leave the user with
///     no visible folder to re-expand. Zed's explorer keeps the worktree root
///     reachable for the same reason.
/// </para>
/// </remarks>
public static class FileTreeExpansion
{
    /// <summary>Expand every directory in the supplied trees, roots included.</summary>
    public static void ExpandAll(IEnumerable<FileTreeNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        foreach (FileTreeNode root in roots)
        {
            SetExpanded(root, true);
        }
    }

    /// <summary>
    ///     Collapse every directory below the supplied roots. The roots
    ///     themselves keep their current state so the tree stays reachable.
    /// </summary>
    public static void CollapseAll(IEnumerable<FileTreeNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        foreach (FileTreeNode root in roots)
        {
            foreach (FileTreeNode child in root.Children)
            {
                SetExpanded(child, false);
            }
        }
    }

    /// <summary>
    ///     Flip one directory's expansion state and return the new value.
    ///     Files are leaves: their state is untouched and reported as-is.
    /// </summary>
    public static bool Toggle(FileTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
        }

        return node.IsExpanded;
    }

    private static void SetExpanded(FileTreeNode node, bool expanded)
    {
        if (node.IsDirectory)
        {
            node.IsExpanded = expanded;
        }

        foreach (FileTreeNode child in node.Children)
        {
            SetExpanded(child, expanded);
        }
    }
}
