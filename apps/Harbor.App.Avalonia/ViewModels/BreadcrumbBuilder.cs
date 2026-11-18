// BreadcrumbBuilder.cs — K24: addressable path breadcrumbs for the Avalonia
// code editor.
//
// The editor used to show only Path.GetFileName for the active tab, so a file
// several directories deep lost the path that identified it. This class splits
// a path into clickable crumbs, each carrying the cumulative path up to that
// segment. The split is pure string handling over a path the picker or the tree
// already produced: no XAML, no dispatcher, no reflection, so it is safe under
// NativeAOT and testable without standing up an Avalonia application.

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>One crumb of a file path (K24).</summary>
/// <param name="Label">Text shown for this crumb (a segment or the root).</param>
/// <param name="Path">Cumulative path up to and including this crumb.</param>
/// <param name="IsCurrent">True for the last crumb, the file itself.</param>
/// <param name="IsEnabled">
/// False for a crumb that cannot be navigated to (<c>.</c> / <c>..</c>).
/// </param>
public sealed record BreadcrumbCrumb(
    string Label,
    string Path,
    bool IsCurrent,
    bool IsEnabled = true);

/// <summary>
///     Pure projection from a path string to an ordered list of crumbs.
/// </summary>
/// <remarks>
///     <para>
///         Both separator flavours are accepted so a path captured on Windows
///         and read on Linux (or the reverse) still produces a trail. Emitted
///         paths are normalised to <c>/</c>, which is the separator every crumb
///         command receives and the one the editor's port understands on both
///         platforms.
///     </para>
///     <para>
///         An absolute path leads with a root crumb: <c>/</c> on Unix,
///         <c>C:/</c> for a drive, <c>//server/share</c> for UNC. A relative
///         path has no root crumb. Empty, duplicate and trailing separators
///         never create a crumb, so the trail has one entry per real segment.
///     </para>
/// </remarks>
public static class BreadcrumbBuilder
{
    /// <summary>The glyph drawn between two crumbs.</summary>
    public const string Separator = "›";

    /// <summary>
    ///     Splits <paramref name="path"/> into crumbs, root first and file last.
    /// </summary>
    /// <param name="path">A path string. Null, empty or whitespace yields no crumbs.</param>
    public static IReadOnlyList<BreadcrumbCrumb> Build(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<BreadcrumbCrumb>();

        string normalized = path.Replace('\\', '/');
        int start;
        string root;

        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            // UNC: //server/share/rest... The root is server + share.
            start = IndexAfterSegments(normalized, 2);
            root = normalized[..start];
        }
        else if (normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0]))
        {
            // Drive: C:/rest... or the bare C:.
            root = normalized.Length > 2 && normalized[2] == '/'
                ? normalized[..3]
                : normalized[..2] + "/";
            start = normalized.Length > 2 && normalized[2] == '/' ? 3 : 2;
        }
        else if (normalized[0] == '/')
        {
            root = "/";
            start = 1;
        }
        else
        {
            root = string.Empty;
            start = 0;
        }

        var crumbs = new List<BreadcrumbCrumb>();
        if (root.Length > 0)
        {
            crumbs.Add(new BreadcrumbCrumb(root, root, false));
        }

        string prefix = root;
        foreach (string raw in normalized[start..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = raw.Trim();
            if (segment.Length == 0) continue;

            string crumbPath = prefix.Length == 0
                ? segment
                : prefix.EndsWith('/') ? prefix + segment : prefix + "/" + segment;
            bool navigable = segment is not ("." or "..");
            crumbs.Add(new BreadcrumbCrumb(segment, crumbPath, false, navigable));
            prefix = crumbPath;
        }

        if (crumbs.Count == 0) return Array.Empty<BreadcrumbCrumb>();

        BreadcrumbCrumb last = crumbs[^1];
        crumbs[^1] = last with { IsCurrent = true };
        return crumbs;
    }

    /// <summary>Index just past the end of the <paramref name="count"/> first non-empty segments.</summary>
    private static int IndexAfterSegments(string value, int count)
    {
        int index = 0;
        for (int n = 0; n < count && index < value.Length; n++)
        {
            while (index < value.Length && value[index] == '/') index++;
            while (index < value.Length && value[index] != '/') index++;
        }
        return index;
    }
}
