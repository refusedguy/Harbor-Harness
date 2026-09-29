namespace Harbor.Abstractions.Filesystem;

/// <summary>
///     One immediate child of a directory, as a file-tree view needs it: the
///     display name, the path the view will descend into, and the two facts that
///     only a filesystem read can supply.
/// </summary>
/// <param name="Name">
///     Display name. For a directory the UI is expected to append its own
///     separator — the contract reports a bare name, not a decorated one.
/// </param>
/// <param name="FullPath">Absolute path, suitable for descending into.</param>
/// <param name="IsDirectory">Directories sort before files and are the only entries the view descends into.</param>
/// <param name="IsHidden">Whether the entry is hidden/dot-prefixed, used only for the row marker.</param>
public sealed record DirectoryEntry(string Name, string FullPath, bool IsDirectory, bool IsHidden);

/// <summary>
///     The immediate children of one directory, already ordered for display:
///     directories first, then case-insensitive by name.
/// </summary>
/// <param name="Directory">The directory that was listed — echoed so the caller can match a result to its request.</param>
/// <param name="Entries">The entries, display order. Never null; possibly capped — see <paramref name="Truncated" />.</param>
/// <param name="Truncated">
///     True when the walk hit its cap and stopped early. A truncated listing is
///     still a valid answer: the alternative to a cap is unbounded work, and the
///     view can say so.
/// </param>
/// <param name="TotalCount">How many entries exist before the cap, when the implementation can know cheaply.</param>
public sealed record DirectoryListing(
    string Directory,
    IReadOnlyList<DirectoryEntry> Entries,
    bool Truncated = false,
    int TotalCount = 0)
{
    /// <summary>An empty, complete listing of <paramref name="directory" />.</summary>
    public static DirectoryListing Empty(string directory) => new(directory, []);
}

/// <summary>
///     Read-only directory listings for UI file trees (issue #667).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why this contract exists.</b> <c>CellForgeFileTreePanel</c> called
///     <c>Directory.EnumerateDirectories</c> / <c>EnumerateFiles</c> and read
///     <c>FileAttributes</c> from inside <c>Build</c> — that is, from inside a
///     painted frame. Three things were missing at once: the listing had no place
///     in <c>UiState</c>, so the panel had to keep a private cache; the walk had
///     no seam, so the only way to make it fakeable was to fake the filesystem;
///     and it had no cancellation, so a slow mount could stall the render loop
///     with nothing able to stop it. This contract is the seam. It is the same
///     move as <c>IGitQuery</c> / <c>ProcessGitQuery</c> (#537),
///     <c>INotificationProcessRunner</c> (#665) and
///     <c>ITerminalPaneLauncher</c> (#672).
/// </para>
/// <para>
///     <b>Async, and the thread is part of the contract.</b> This is
///     deliberately not a synchronous method. A directory walk over a network
///     mount is unbounded blocking work, and the caller of this contract is a
///     render thread that has already learned what happens when it blocks: it
///     drops frames with no diagnostic and no way out. Implementations MUST NOT
///     run the enumeration on the calling thread. Returning a synchronously
///     completed task is a contract violation, not an optimisation — it puts the
///     work back where it started.
/// </para>
/// <para>
///     <b>Bounded and cancellable, both non-negotiable.</b> Every implementation
///     must stop after some entry cap (reporting <see cref="DirectoryListing.Truncated" />)
///     and must observe <c>cancellationToken</c> between entries. A walk that
///     cannot be stopped is the defect this contract exists to remove; moving it
///     behind an interface without bounding it would relocate the hazard rather
///     than fix it.
/// </para>
/// <para>
///     <b>Permission gating is a deliberate decision.</b> Like the git badge
///     (#537), this is UI chrome: a read-only listing of a directory the user
///     opened, carrying no model intent. Routing it through
///     <c>PermissionRuleset</c> would mean asking the user to approve looking at
///     their own files. The agent's filesystem access is the <c>bash</c> /
///     <c>read</c> / <c>glob</c> tools, which <i>are</c> gated.
/// </para>
/// </remarks>
public interface IDirectoryLister
{
    /// <summary>
    ///     List the immediate children of <paramref name="directory" />.
    /// </summary>
    /// <remarks>
    ///     Never throws for an expected failure — a missing directory, a
    ///     permission denial or a malformed path is a
    ///     <see cref="Result.Failure{TValue}" /> carrying the reason, because the
    ///     only caller is a panel that has to render something either way.
    ///     <see cref="OperationCanceledException" /> is the one exception that
    ///     may propagate, and a caller is expected to treat it as "superseded",
    ///     not as a failure.
    /// </remarks>
    /// <param name="directory">Directory to list. An empty value means the process working directory.</param>
    /// <param name="cancellationToken">Cancels the walk; observed between entries.</param>
    Task<Result<DirectoryListing>> ListAsync(string directory, CancellationToken cancellationToken = default);
}
