namespace Harbor.Tools.Builtin;
/// <summary>
///     Shared part-whole model (Composite) behind the builtin directory walkers:
///     Component = <see cref="FsNode" />, Leaf = <see cref="FsFile" />,
///     Composite = <see cref="FsDirectory" />. Changing the traversal semantics
///     (hidden policy, ordering, error policy) is done here — once — instead of
///     keeping a near-verbatim copy in every walker (#1588).
/// </summary>
/// <remarks>
///     A directory's children are not materialised with the directory itself:
///     neither walker can afford to read a subtree it prunes, so a walker that
///     decides to descend re-enters <see cref="FsModel.Read" /> on the node's
///     <see cref="FsNode.FullName" />. Every level of the walk therefore goes
///     through this one reader.
/// </remarks>
internal abstract class FsNode
{
    private protected FsNode(string name, string fullName)
    {
        Name = name;
        FullName = fullName;
    }

    /// <summary>Display name of the entry, as <c>FileSystemInfo.Name</c> reports it.</summary>
    public string Name { get; }

    /// <summary>Full path of the entry — the anchor a walker needs to descend into a directory.</summary>
    public string FullName { get; }

    /// <summary>
    ///     Whether the node is a composite. Kept as a property so policy filters
    ///     (prune lists, tracked-file rules) stay uniform over the model.
    /// </summary>
    public bool IsDirectory => this is FsDirectory;
}

/// <summary>
///     Leaf of the model: a file. The backing <see cref="FileInfo" /> is exposed so
///     listing-style consumers can read size and mtime without a second stat.
/// </summary>
internal sealed class FsFile : FsNode
{
    internal FsFile(FileInfo file) : base(file.Name, file.FullName) => File = file;

    /// <summary>The <see cref="FileInfo" /> this node was materialised from.</summary>
    public FileInfo File { get; }
}

/// <summary>
///     Composite of the model: a directory that may hold children. Children are
///     per the remarks on <see cref="FsNode" /> read on demand via
///     <see cref="FsModel.Read" />.
/// </summary>
internal sealed class FsDirectory : FsNode
{
    internal FsDirectory(DirectoryInfo directory) : base(directory.Name, directory.FullName) =>
        Directory = directory;

    /// <summary>The <see cref="DirectoryInfo" /> this node was materialised from.</summary>
    public DirectoryInfo Directory { get; }
}

/// <summary>
///     The one traversal the model offers: reads a single directory level into
///     <see cref="FsNode" /> children. Before #1588 the walkers owned copies of
///     this — <c>GetFileSystemInfos</c>, the dirs-first ordinal-ignore-case sort,
///     the hidden filter — differing only in their render step.
/// </summary>
internal static class FsModel
{
    /// <summary>
    ///     Reads one directory level and returns its sorted, visible children as
    ///     model nodes. Dirs come first, then files; within each group the order is
    ///     ordinal ignore-case — the display order both walkers have always used.
    /// </summary>
    /// <param name="dir">The directory to read.</param>
    /// <param name="includeHidden">
    ///     Whether dot-prefixed entries stay in the listing. Both walkers share the
    ///     same rule: a name starting with <c>.</c> is hidden.
    /// </param>
    /// <param name="reportUnreadable">
    ///     Failure sink for the silent-skip contract (ROP-A Z1 п.15): when the
    ///     directory cannot be listed, the walker keeps its output contract and the
    ///     reason is explained at trace level by the caller's own logger.
    /// </param>
    /// <returns>
    ///     The visible children; empty when the directory is unreadable.
    /// </returns>
    internal static List<FsNode> Read(
        string dir,
        bool includeHidden,
        Action<string, Exception> reportUnreadable)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            reportUnreadable(dir, ex);
            return [];
        }

        // Dirs first, then files; ordinal ignore-case within each group.
        Array.Sort(entries, static (a, b) =>
        {
            bool ad = a is DirectoryInfo;
            bool bd = b is DirectoryInfo;
            if (ad != bd) return ad ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        var nodes = new List<FsNode>(entries.Length);
        foreach (var e in entries)
        {
            if (!includeHidden && e.Name.StartsWith('.'))
            {
                continue;
            }

            if (e is DirectoryInfo di)
            {
                nodes.Add(new FsDirectory(di));
            }
            else if (e is FileInfo fi)
            {
                nodes.Add(new FsFile(fi));
            }
        }

        return nodes;
    }
}
