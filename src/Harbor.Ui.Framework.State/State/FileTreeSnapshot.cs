using System.Collections.Immutable;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     One row of a file-tree view, as the UI needs it. The Presentation-side
///     mirror of the Domain <c>DirectoryEntry</c>: the loader maps one to the
///     other, so the port can change shape without a state migration and the
///     state never has to know what an <c>IDirectoryLister</c> is.
/// </summary>
/// <param name="Name">Bare display name — a view adds its own separator for directories.</param>
/// <param name="FullPath">Absolute path, used when descending into a directory.</param>
/// <param name="IsDirectory">Directories sort first and are the only entries <c>Enter</c> expands in place (#1487).</param>
/// <param name="IsHidden">Hidden/dot-prefixed; drives the row marker only.</param>
/// <param name="Children">Loaded subtree of a directory node (#1487); empty for files and unexpanded nodes.</param>
public sealed record FileTreeEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsHidden,
    ImmutableArray<FileTreeEntry> Children = default)
{
    /// <summary>
    ///     The loaded subtree of a directory node (#1487). Empty for files and for
    ///     a directory whose children were not loaded. The optional parameter uses
    ///     <c>default</c> because <see cref="ImmutableArray{T}.Empty" /> is not a
    ///     compile-time constant; readers go through <see cref="ChildEntries" />.
    /// </summary>
    public ImmutableArray<FileTreeEntry> ChildEntries
        => Children.IsDefault ? ImmutableArray<FileTreeEntry>.Empty : Children;

    /// <summary>Project a Domain listing entry into the view's vocabulary.</summary>
    /// <param name="entry">The entry the loader produced.</param>
    public static FileTreeEntry From(Abstractions.Filesystem.DirectoryEntry entry) =>
        new(entry.Name, entry.FullPath, entry.IsDirectory, entry.IsHidden);

    /// <summary>Return a copy with <paramref name="children" /> attached as the loaded subtree.</summary>
    /// <param name="children">The already-loaded children, in display order.</param>
    public FileTreeEntry WithChildren(ImmutableArray<FileTreeEntry> children) => this with { Children = children };
}

/// <summary>
///     The file-tree listing for exactly one (panel id, directory) pair, plus
///     the status of the load that produced it (#667).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why the listing is state and not a provider-local cache.</b> The
///     panel used to hold the listing in a private field, guarded by a lock,
///     because there was nowhere else for it to live. That is not a harmless
///     implementation detail: a cache on the render thread can only be filled by
///     the render thread, which is precisely why the walk had to happen there.
///     Moving the entries into <see cref="TerminalUiState.FileTrees" /> is what
///     makes it possible for the walk to happen anywhere at all.
/// </para>
/// <para>
///     <b>Why the directory is part of the snapshot.</b> A result that arrives
///     after the user has already navigated describes a directory nobody is
///     looking at. Carrying the directory makes that decidable — the reducer
///     drops a result whose directory is no longer the panel's, and the stale
///     frame never paints.
/// </para>
/// </remarks>
/// <param name="Directory">The directory this snapshot describes. Never empty — always resolved.</param>
/// <param name="Status">Where the load is. Reuses <see cref="AsyncStatus" /> rather than a near-duplicate enum.</param>
/// <param name="Entries">The entries in display order. Empty unless a load has succeeded.</param>
/// <param name="Error">Why the load failed, when <paramref name="Status" /> is <see cref="AsyncStatus.Error" />.</param>
/// <param name="Truncated">The walk hit its entry cap; the view should say so.</param>
/// <param name="TotalCount">Entries before the cap, or -1 when the walk stopped early and never finished counting.</param>
public sealed record FileTreeSnapshot(
    string Directory,
    AsyncStatus Status,
    ImmutableArray<FileTreeEntry> Entries,
    string? Error = null,
    bool Truncated = false,
    int TotalCount = 0)
{
    /// <summary>
    ///     Nothing loaded yet. Carries no directory on purpose: a snapshot that
    ///     claimed one would satisfy the reducer's staleness check for a panel
    ///     that has not asked for anything yet.
    /// </summary>
    public static FileTreeSnapshot None { get; } = new(
        string.Empty,
        AsyncStatus.Idle,
        ImmutableArray<FileTreeEntry>.Empty);

    /// <summary>A load has been asked for; nothing to show but the fact of the ask.</summary>
    /// <param name="directory">The directory being loaded.</param>
    public static FileTreeSnapshot Pending(string directory) => new(
        directory,
        AsyncStatus.Loading,
        ImmutableArray<FileTreeEntry>.Empty);

    /// <summary>
    ///     A settled, successful load. Used by the reducer, which receives entries
    ///     as message payload, and by the loader, which maps the port's
    ///     <c>DirectoryListing</c> through <see cref="FileTreeEntry.From" />.
    /// </summary>
    /// <param name="directory">The directory the entries were read from.</param>
    /// <param name="entries">The entries in display order.</param>
    /// <param name="truncated">Whether the walk hit its entry cap.</param>
    /// <param name="totalCount">Entries before the cap, or -1 when counting stopped early.</param>
    public static FileTreeSnapshot Completed(
        string directory,
        ImmutableArray<FileTreeEntry> entries,
        bool truncated = false,
        int totalCount = 0) => new(
        directory,
        AsyncStatus.Success,
        entries,
        Error: null,
        truncated,
        totalCount);

    /// <summary>A settled, failed load — a missing directory, a permission denial, a cap.</summary>
    /// <param name="directory">The directory that could not be listed.</param>
    /// <param name="error">The reason, already phrased for display.</param>
    public static FileTreeSnapshot Failed(string directory, string error) => new(
        directory,
        AsyncStatus.Error,
        ImmutableArray<FileTreeEntry>.Empty,
        error);

    /// <summary>Whether the view is waiting on a load and has nothing to show for it yet.</summary>
    public bool IsPending => Status is AsyncStatus.Loading or AsyncStatus.Refreshing && Entries.Length == 0;

    /// <summary>Whether this snapshot can answer a request for <paramref name="directory" />.</summary>
    /// <param name="directory">The directory the view is currently pointed at.</param>
    public bool Covers(string directory) => Status is not AsyncStatus.Idle && string.Equals(Directory, directory, StringComparison.Ordinal);
}
