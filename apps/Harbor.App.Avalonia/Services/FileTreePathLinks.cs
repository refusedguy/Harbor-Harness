namespace Harbor.App.Avalonia.Services;

/// <summary>
///     #2682 Z20: the three file references the explorer's context menu can
///     put on the clipboard — an absolute path, a workspace-relative path, and
///     a stable <c>file://</c> permalink.
/// </summary>
/// <remarks>
/// <para>
///     Pure and UI-free on purpose. Building the three strings from a tree node
///     has nothing to do with Avalonia, so it can be pinned by tests without a
///     headless session; the view only decides which one to copy.
/// </para>
/// <para>
///     The permalink is the local-file URI, not a code-host URL. Harbor's
///     desktop shell has no remote seam yet, so a <c>file://</c> URI is the one
///     permanent, pasteable reference that is correct today. It resolves back
///     to the same file from a chat message, a terminal, or a ticket, and it
///     stays meaningful for files that are not tracked by git at all.
/// </para>
/// </remarks>
public static class FileTreePathLinks
{
    /// <summary>
    ///     Fully qualified path of <paramref name="node" />, with the current
    ///     platform's separators. Empty when the node carries no path.
    /// </summary>
    public static string AbsolutePath(FileTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        string path = node.FullPath;
        return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);
    }

    /// <summary>
    ///     <paramref name="node" /> relative to <paramref name="workspaceRoot" />,
    ///     always with <c>/</c> separators so the result is the same string on
    ///     every platform. An empty root means the current directory.
    /// </summary>
    public static string RelativePath(FileTreeNode node, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(node);

        string path = node.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string root = string.IsNullOrEmpty(workspaceRoot)
            ? Environment.CurrentDirectory
            : workspaceRoot;

        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    /// <summary>
    ///     Stable <c>file://</c> URI for <paramref name="node" />. Empty when
    ///     the node carries no path.
    /// </summary>
    public static string Permalink(FileTreeNode node)
    {
        string absolute = AbsolutePath(node);
        if (absolute.Length == 0)
        {
            return string.Empty;
        }

        // Uri turns a rooted path into a file:// URI on both Windows and Unix,
        // and takes care of escaping spaces and other reserved characters.
        return new Uri(absolute, UriKind.Absolute).AbsoluteUri;
    }
}
