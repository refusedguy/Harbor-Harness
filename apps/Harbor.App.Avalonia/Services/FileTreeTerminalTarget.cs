// FileTreeTerminalTarget.cs - issue #2682 Z22.
//
// The explorer's "Open in terminal" action has one decision to make: which
// directory becomes the new pane's working directory. A directory row is the
// directory itself; a file row means the directory that holds the file. This
// helper pins that decision as a pure function so it can be tested without an
// Avalonia session or a running shell.

using System.IO;

namespace Harbor.App.Avalonia.Services;

public static class FileTreeTerminalTarget
{
    /// <summary>
    ///     Resolve the working directory for a terminal opened from a file-tree
    ///     row. Directories target themselves; files target their parent; a row
    ///     without a path falls back to <paramref name="fallbackRoot" />.
    /// </summary>
    /// <param name="node">The row the user invoked the action on.</param>
    /// <param name="fallbackRoot">Workspace root to use when the row has no path.</param>
    /// <returns>An absolute directory path, or an empty string when neither the row nor the fallback has one.</returns>
    public static string Directory(FileTreeNode node, string fallbackRoot)
    {
        if (string.IsNullOrEmpty(node.FullPath))
        {
            return string.IsNullOrEmpty(fallbackRoot) ? string.Empty : Path.GetFullPath(fallbackRoot);
        }

        string absolute = Path.GetFullPath(node.FullPath);

        if (node.IsDirectory) return absolute;

        return Path.GetDirectoryName(absolute) ?? fallbackRoot;
    }
}
