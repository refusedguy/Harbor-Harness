using CSharpFunctionalExtensions;
using Harbor.Abstractions.Filesystem;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Builds the desktop shell's file tree out of <see cref="IDirectoryLister" />
///     listings and an <see cref="IFileTreePolicy" /> (issue #492).
/// </summary>
/// <remarks>
/// <para>
///     <b>What moved out of the view-model, and what did not.</b> Before #492
///     <c>MainViewModel</c> contained the walk (<c>Directory.GetDirectories</c> /
///     <c>Directory.GetFiles</c> / <c>new DirectoryInfo</c>), the recursion, the
///     depth cap, the ignore list, the icon <c>switch</c>, and a bare
///     <c>Task.Run</c> with no cancellation and no entry budget. Two of those
///     moved further than the others, and which is which matters:
/// </para>
/// <list type="bullet">
///     <item>
///         The WALK is <see cref="IDirectoryLister" />'s, and already was: #667
///         built that port in Application with an entry cap, a timeout and
///         per-entry cancellation, and #667's own panel now uses it. This type
///         performs no <c>System.IO</c> at all.
///     </item>
///     <item>
///         The POLICY is <see cref="IFileTreePolicy" />'s, newly: which
///         directories a tree omits and which category a file gets is a decision
///         that now lives beside the lister in Application, has tests, and can be
///         handed to a second renderer instead of being retyped.
///     </item>
///     <item>
///         The RECURSION and the BUDGETS stay here, because they are specific to
///         this tree: it is eager and depth-capped, where the TUI sidebar is lazy
///         and per-directory. What changed is that they are named, injected,
///         observable and testable instead of a literal <c>if (depth &gt; 3)
///         return;</c> in a private method of a view-model.
///     </item>
/// </list>
/// <para>
///     <b>Why a node budget is needed on top of the port's.</b>
///     <see cref="IDirectoryLister" /> caps ONE directory at 4096 entries; a
///     recursive walk multiplies that by the number of directories it visits, and
///     the old code's only limit was depth. So the walk is bounded here too, and a
///     truncated scan is reported rather than shown as a complete tree — the same
///     reasoning as <see cref="DirectoryListing.Truncated" />, one level up.
/// </para>
/// <para>
///     <b>Threading.</b> Every listing runs its enumeration on a thread-pool
///     thread (that is the port's contract), so this type's recursion never runs
///     syscalls on its caller's thread. It does NOT post anything to a UI thread:
///     building a plain object graph is not UI work, and the caller owns the
///     marshalling, because the caller is the one that knows which thread the
///     bound collection has to be filled on.
/// </para>
/// <para>
///     <b>Degradation is per-directory, not per-scan.</b> A directory that cannot
///     be read — a permission denial, a vanished path — becomes a row with no
///     children and one debug line. Only a failure to list the ROOT is a scan
///     failure, because then there is nothing to show at all and the caller has to
///     be told why.
/// </para>
/// <para>
///     <b>One walk at a time, in listing order.</b> The recursion awaits each
///     child in turn rather than fanning the siblings out concurrently. That is
///     slower in the abstract and better in every way that is observable here: the
///     node budget is then a hard ceiling on outstanding work instead of a race
///     against N pool threads, the entry order is deterministic (which is what
///     makes the tests below assertable), and a slow mount cannot multiply into N
///     simultaneous blocking enumerations.
/// </para>
/// </remarks>
public sealed class ProjectFileTreeScanner
{
    /// <summary>How many directory levels below the root get populated.</summary>
    /// <remarks>
    ///     Three is what the shell's sidebar has always drawn, kept so the fix is
    ///     not a silent behaviour change. It is a constructor argument rather than
    ///     a literal, so a host that wants a different budget states it.
    /// </remarks>
    public const int DefaultMaxDepth = 3;

    /// <summary>How many nodes one scan may produce before it stops descending.</summary>
    /// <remarks>
    ///     The port bounds one directory; this bounds the WALK. Without it, a
    ///     project with a wide shallow tree is bounded only by how many directories
    ///     it has — the unbounded case the audit named.
    /// </remarks>
    public const int DefaultMaxNodes = 4096;

    /// <summary>
    ///     Icon category for a directory row.
    /// </summary>
    /// <remarks>
    ///     Presentation vocabulary, not policy, so it is a constant here and not a
    ///     member of <see cref="IFileTreePolicy" />: the policy answers which
    ///     directories a tree shows and how a FILE is classified, and a folder row
    ///     that survived the policy has no classification left to receive. The
    ///     glyph behind the name is
    ///     <c>FileTypeToGeometryConverter</c>'s business, as it was before.
    /// </remarks>
    private const string FolderIcon = "folder";

    private readonly IDirectoryLister _lister;
    private readonly IFileTreePolicy _policy;
    private readonly ILogger<ProjectFileTreeScanner> _logger;
    private readonly int _maxDepth;
    private readonly int _maxNodes;
    /// <summary>Construct a scanner with the default depth and node budgets.</summary>
    /// <param name="lister">The Domain port that performs each directory walk, off the caller's thread.</param>
    /// <param name="policy">The Domain port that decides what a tree shows.</param>
    /// <param name="logger">Logger for the per-directory degradation trail and the truncation notice.</param>
    public ProjectFileTreeScanner(
        IDirectoryLister lister,
        IFileTreePolicy policy,
        ILogger<ProjectFileTreeScanner> logger)
        : this(lister, policy, logger, DefaultMaxDepth, DefaultMaxNodes)
    {
    }

    /// <summary>Construct a scanner with explicit budgets.</summary>
    /// <param name="lister">The Domain port that performs each directory walk, off the caller's thread.</param>
    /// <param name="policy">The Domain port that decides what a tree shows.</param>
    /// <param name="logger">Logger for the per-directory degradation trail and the truncation notice.</param>
    /// <param name="maxDepth">Directory levels below the root to populate. Zero yields the root row alone.</param>
    /// <param name="maxNodes">Total node budget for one scan. Values below 1 are clamped to 1.</param>
    public ProjectFileTreeScanner(
        IDirectoryLister lister,
        IFileTreePolicy policy,
        ILogger<ProjectFileTreeScanner> logger,
        int maxDepth,
        int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _lister = lister;
        _policy = policy;
        _logger = logger;
        _maxDepth = Math.Max(0, maxDepth);
        _maxNodes = Math.Max(1, maxNodes);
    }

    /// <summary>
    ///     Scan <paramref name="root" /> into a single-row tree — the root node
    ///     with its descendants nested under it.
    /// </summary>
    /// <remarks>
    ///     Throws <see cref="OperationCanceledException" /> when
    ///     <paramref name="cancellationToken" /> fires, deliberately: a superseded
    ///     scan is not a failure and must not paint one. The caller owns the token
    ///     and is expected to read the throw as "a newer scan owns the state".
    /// </remarks>
    /// <param name="root">Directory to scan. Empty means the process working directory.</param>
    /// <param name="cancellationToken">Cancels the walk; observed before every entry and every directory.</param>
    /// <returns>The root node, or a failure carrying why the root could not be listed.</returns>
    public async Task<Result<FileTreeNode>> ScanAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        string directory = string.IsNullOrEmpty(root) ? Environment.CurrentDirectory : root;

        cancellationToken.ThrowIfCancellationRequested();

        Result<DirectoryListing> rootListing = await _lister
            .ListAsync(directory, cancellationToken)
            .ConfigureAwait(false);

        if (rootListing.IsSuccess)
        {
            var budget = new NodeBudget(_maxNodes);
            FileTreeNode rootNode = CreateDirectoryNode(
                Path.GetFileName(directory),
                directory,
                expanded: true);

            await PopulateAsync(rootNode, rootListing.Value, depth: 0, budget, cancellationToken)
                .ConfigureAwait(false);

            if (budget.Truncated)
            {
                // Warning, not Debug: this is the one case where the tree on screen
                // is a strict prefix of the project, and the user is entitled to
                // know it.
                _logger.LogWarning(
                    "File-tree scan of {Dir} stopped at the {MaxNodes}-node budget — the tree is a prefix of the project",
                    directory,
                    _maxNodes);
            }

            return Result.Success(rootNode);
        }

        // The root is the one listing whose failure means there is nothing to draw.
        // A NESTED failure degrades to "no children" inside PopulateAsync, because
        // a tree with one unreadable folder is still a useful tree.
        return Result.Failure<FileTreeNode>(rootListing.Error);
    }

    /// <summary>
    ///     Fill <paramref name="parent" /> from one listing, then recurse into the
    ///     directories it names, subject to the depth and node budgets.
    /// </summary>
    /// <param name="parent">Row to fill. Already has its own name and path.</param>
    /// <param name="listing">The listing of the directory <paramref name="parent" /> stands for.</param>
    /// <param name="depth">Depth of <paramref name="parent" />; the root row is 0.</param>
    /// <param name="budget">The scan-wide node budget, shared by every level of the recursion.</param>
    /// <param name="cancellationToken">Cancels the walk; observed before every entry and every directory.</param>
    private async Task PopulateAsync(
        FileTreeNode parent,
        DirectoryListing listing,
        int depth,
        NodeBudget budget,
        CancellationToken cancellationToken)
    {
        foreach (DirectoryEntry entry in listing.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory && _policy.IsIgnoredDirectory(entry.Name))
            {
                // Policy BEFORE budget: a directory the policy drops must not be
                // able to consume the node budget, and must not be listed at all.
                continue;
            }

            if (!budget.TryTake())
            {
                return;
            }

            if (!entry.IsDirectory)
            {
                parent.Children.Add(CreateFileNode(entry));
                continue;
            }

            FileTreeNode child = CreateDirectoryNode(
                entry.Name,
                entry.FullPath,
                // First level open, everything below collapsed: the shape the
                // sidebar has always had. Children are still POPULATED eagerly at
                // every level, which is what makes MaxDepth a budget rather than a
                // laziness setting.
                expanded: depth < 1);

            parent.Children.Add(child);

            if (depth >= _maxDepth)
            {
                continue;
            }

            await PopulateAsync(
                child,
                await ListOrDegradeAsync(child, cancellationToken).ConfigureAwait(false),
                depth + 1,
                budget,
                cancellationToken).ConfigureAwait(false);

            if (budget.Truncated)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     List one child directory, degrading to an empty listing when it cannot
    ///     be read.
    /// </summary>
    /// <remarks>
    ///     The old code wrapped a whole directory's contents in one
    ///     <c>catch (IOException or UnauthorizedAccessException)</c>, so a single
    ///     unreadable file silently cost that directory every child it had. This
    ///     asks the port — which already classifies a missing directory, a denial
    ///     and a malformed path as results rather than exceptions — and turns a
    ///     failure into "no children", so a partially readable directory still
    ///     shows what it could read.
    /// </remarks>
    /// <param name="child">The directory row being descended into.</param>
    /// <param name="cancellationToken">Cancels the walk; passed through so a superseded scan stops here too.</param>
    /// <returns>The child's listing, or an empty one with the reason logged at Debug.</returns>
    private async Task<DirectoryListing> ListOrDegradeAsync(
        FileTreeNode child,
        CancellationToken cancellationToken)
    {
        Result<DirectoryListing> listing = await _lister
            .ListAsync(child.FullPath, cancellationToken)
            .ConfigureAwait(false);

        if (listing.IsSuccess)
        {
            return listing.Value;
        }

        _logger.LogDebug(
            "File-tree scan skipped '{Path}': {Reason}",
            child.FullPath,
            listing.Error);

        return DirectoryListing.Empty(child.FullPath);
    }

    private static FileTreeNode CreateDirectoryNode(string name, string fullPath, bool expanded) => new()
    {
        Name = name,
        FullPath = fullPath,
        IsDirectory = true,
        IsExpanded = expanded,
        IconPath = FolderIcon,
    };

    private FileTreeNode CreateFileNode(DirectoryEntry entry) => new()
    {
        Name = entry.Name,
        FullPath = entry.FullPath,
        IsDirectory = false,
        IconPath = _policy.IconFor(entry.Name),
    };

    /// <summary>
    ///     The scan-wide node budget: one counter, shared by every level of the
    ///     recursion.
    /// </summary>
    /// <remarks>
    ///     A class, not a struct, and the reason is not taste: the recursion has
    ///     to <i>share</i> one counter, and an async method may not take
    ///     <c>ref</c>/<c>in</c> parameters — passing the struct by value would
    ///     silently hand every level of the tree its own fresh budget, which is
    ///     the unbounded walk wearing a limit. The cost is one small allocation per
    ///     SCAN, not per directory, against a walk that pays at least one syscall
    ///     per directory.
    /// </remarks>
    private sealed class NodeBudget(int limit)
    {
        private int _remaining = limit;

        /// <summary>Whether the walk stopped because the budget ran out.</summary>
        public bool Truncated { get; private set; }

        /// <summary>Claim one node's worth of budget, or report that there is none left.</summary>
        /// <returns>True when the caller may add a row; false when the walk must stop.</returns>
        public bool TryTake()
        {
            if (Truncated)
            {
                return false;
            }

            if (_remaining <= 0)
            {
                Truncated = true;
                return false;
            }

            _remaining--;
            return true;
        }
    }
}
