using Harbor.Abstractions.Git;

namespace Harbor.TestKit;

/// <summary>
///     An <see cref="IGitQuery" /> that answers from values the test declares.
/// </summary>
/// <remarks>
///     Six hand-written git queries existed across four projects, and they
///     were the same object with two dials: what to answer, and whether to
///     count what was asked. The counters are not decoration — one of the
///     copies existed precisely to prove that the panel REACHED the port, and
///     that it did not reach it on a frame that changed nothing.
/// </remarks>
public class TestGitQuery : IGitQuery
{
    private readonly Lock _gate = new();
    private readonly List<string> _askedDirectories = [];

    /// <summary>
    ///     Creates a git query that answers from the given values.
    /// </summary>
    /// <param name="status">
    ///     The status to report. Defaults to
    ///     <see cref="GitWorkspaceStatus.None" /> — the "not a repository"
    ///     sentinel.
    /// </param>
    /// <param name="worktrees">The worktrees to report.</param>
    public TestGitQuery(
        GitWorkspaceStatus? status = null,
        IReadOnlyList<GitWorktreeInfo>? worktrees = null)
    {
        Status = status ?? GitWorkspaceStatus.None;
        Worktrees = worktrees ?? [];
    }

    /// <summary>The status this query reports.</summary>
    public GitWorkspaceStatus Status { get; }

    /// <summary>The worktrees this query reports.</summary>
    public IReadOnlyList<GitWorktreeInfo> Worktrees { get; }

    /// <summary>How many times the worktree list was asked for.</summary>
    public int ListWorktreesCalls { get; private set; }

    /// <summary>Every directory the worktree list was asked about, in order.</summary>
    public IReadOnlyList<string> AskedDirectories
    {
        get
        {
            lock (_gate)
            {
                return [.. _askedDirectories];
            }
        }
    }

    /// <summary>The directory the worktree list was last asked about.</summary>
    public string? AskedFor
    {
        get
        {
            lock (_gate)
            {
                return _askedDirectories.Count == 0 ? null : _askedDirectories[^1];
            }
        }
    }

    /// <inheritdoc />
    public virtual GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
        => Status;

    /// <inheritdoc />
    public virtual IReadOnlyList<GitWorktreeInfo> ListWorktrees(
        string directory,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ListWorktreesCalls++;
            _askedDirectories.Add(directory);
        }

        return Worktrees;
    }
}

/// <summary>
///     An <see cref="IGitQuery" /> that fails, to prove a caller degrades
///     instead of propagating.
/// </summary>
public sealed class ThrowingGitQuery : IGitQuery
{
    private const string Message = "git is having a day";

    /// <inheritdoc />
    public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public IReadOnlyList<GitWorktreeInfo> ListWorktrees(
        string directory,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);
}
