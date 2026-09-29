using Harbor.Abstractions.Filesystem;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Filesystem;

/// <summary>
///     <see cref="IDirectoryLister" /> over <see cref="Directory" /> — the
///     single place in the harness that walks a directory for a UI file tree
///     (#667).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why Application and not Presentation.</b> The panel that used to do
///     this walked the filesystem from <c>Build</c>, inside a painted frame. The
///     walk itself is not the defect — its <i>location</i> was — but the only way
///     to move it out of the render path is to give it an owner, and Application
///     is where the other blocking I/O already lives (<c>BashTool</c>'s
///     neighbours, <c>ProcessGitQuery</c>, <c>WorkspaceInspector</c>).
/// </para>
/// <para>
///     <b>Threading.</b> The walk runs on a thread-pool thread via
///     <see cref="Task.Run(Action, CancellationToken)" />. That is not incidental:
///     the contract's whole reason for being async is that the caller is a
///     render thread, so a "fast path" that ran the enumeration inline and
///     returned <see cref="Task.FromResult{TResult}" /> would put the stall
///     straight back where it came from.
/// </para>
/// <para>
///     <b>Bounded.</b> The walk stops at <see cref="MaxEntries" /> and says so
///     through <see cref="DirectoryListing.Truncated" />. A directory with a
///     million children is a real thing (a <c>node_modules</c>, a build output
///     tree); without a cap the listing is not merely slow, it is a way to wedge
///     the machine. The cap is reported rather than silent so the view can tell
///     the user it is showing a prefix.
/// </para>
/// <para>
///     <b>Cancellable.</b> <c>cancellationToken</c> is checked before each
///     directory and again before each file, and it is linked to
///     <see cref="Timeout" />. A cancelled token throws
///     <see cref="OperationCanceledException" /> — deliberately NOT converted to
///     a failure, because a superseded or timed-out listing is not an error and
///     must not paint one.
/// </para>
/// </remarks>
public sealed class SystemDirectoryLister : IDirectoryLister
{
    /// <summary>Hard ceiling on entries returned for one directory.</summary>
    public const int DefaultMaxEntries = 4096;

    /// <summary>Hard ceiling on one walk, independent of how many entries it yields.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly int _maxEntries;
    private readonly ILogger<SystemDirectoryLister> _logger;

    /// <summary>Construct a lister with the default cap and timeout.</summary>
    /// <param name="logger">Logger for the debug trail of a failed walk.</param>
    public SystemDirectoryLister(ILogger<SystemDirectoryLister> logger)
        : this(DefaultMaxEntries, logger)
    {
    }

    /// <summary>Construct a lister with an explicit entry cap.</summary>
    /// <param name="maxEntries">Maximum entries to return before truncating. Values below 1 are clamped to 1.</param>
    /// <param name="logger">Logger for the debug trail of a failed walk.</param>
    public SystemDirectoryLister(int maxEntries, ILogger<SystemDirectoryLister> logger)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<DirectoryListing>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        string dir = string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory;

        // The cap is the whole reason this is not `Task.FromResult(Walk(...))`:
        // `Walk` is blocking syscall work, and the caller is a render thread.
        return Task.Run(() => Walk(dir, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     The blocking walk. Never called on a caller-supplied thread: private to
    ///     <see cref="ListAsync" /> for exactly that reason.
    /// </summary>
    private Result<DirectoryListing> Walk(string dir, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = linked.Token;

        var directories = new List<DirectoryEntry>();
        var files = new List<DirectoryEntry>();
        bool truncated = false;

        try
        {
            // EnumerationOptions over the string-returning overloads: the old code
            // paired EnumerateDirectories with a fresh DirectoryInfo per entry,
            // which is a second stat per child. One FileSystemInfo per child
            // carries the attributes the hidden-marker needs, so the walk costs
            // one syscall per entry instead of two.
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
            };

            foreach (FileSystemInfo info in new DirectoryInfo(dir)
                .EnumerateFileSystemInfos("*", options))
            {
                // Checked once per entry, not once per batch: the cap means a
                // long walk is expected, and "expected" is exactly when a
                // cancellation has to be able to land.
                token.ThrowIfCancellationRequested();

                if (directories.Count + files.Count >= _maxEntries)
                {
                    truncated = true;
                    break;
                }

                DirectoryEntry entry = Describe(info);
                (info is DirectoryInfo ? directories : files).Add(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded, timed out, or the host is shutting down. Not a failure:
            // the caller discards stale results anyway, and painting an error for
            // a walk nobody is waiting for any more is a lie.
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A missing directory, a permission denial and a malformed path are
            // all expected outcomes for a view that follows the user around the
            // filesystem. They are results, not exceptions: the panel has to
            // render something either way.
            _logger.LogDebug(ex, "Directory listing failed for {Dir}", dir);
            return Result.Failure<DirectoryListing>(
                $"Cannot list '{dir}': {ex.Message}");
        }

        // Directories first, then case-insensitive by name — the display order
        // the view renders, decided once here rather than on every frame.
        directories.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        files.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var ordered = new List<DirectoryEntry>(directories.Count + files.Count);
        ordered.AddRange(directories);
        ordered.AddRange(files);

        // -1 means "we stopped early and never finished counting". A caller that
        // wanted an exact total can say "at least this many"; one that did not
        // ignores the field. A sentinel beats a fabricated 0.
        int total = truncated ? -1 : ordered.Count;
        return Result.Success(new DirectoryListing(dir, ordered, truncated, total));
    }

    private static DirectoryEntry Describe(FileSystemInfo info)
    {
        bool isDir = info is DirectoryInfo;

        // Attributes is one syscall's worth of data we already paid for. A file
        // that vanishes between the enumeration and this read (a rotating log, a
        // build artefact) throws instead of returning a flag, and the entry is
        // simply omitted rather than shown as a broken row.
        bool hidden;
        try
        {
            hidden = (info.Attributes & FileAttributes.Hidden) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DirectoryEntry(info.Name, info.FullName, isDir, IsHiddenByName(info.Name));
        }

        return new DirectoryEntry(info.Name, info.FullName, isDir, hidden);
    }

    /// <summary>
    ///     Dot-prefix fallback for an entry whose attributes could not be read.
    ///     Not a heuristic dressed as a fact: on every platform Harbor targets, a
    ///     leading dot IS the hidden marker, and the alternative is dropping the
    ///     row.
    /// </summary>
    private static bool IsHiddenByName(string name) => name.StartsWith('.');
}
