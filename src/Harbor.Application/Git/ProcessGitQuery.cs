using System.Diagnostics;
using System.Text;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Git;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Git;

/// <summary>
///     <see cref="IGitQuery" /> over the <c>git</c> binary. The process spawn lives
///     here, beside <c>BashTool</c> and <c>WorkspaceInspector</c>, so the
///     Presentation assembly that renders the branch badge no longer forks anything
///     (#537 / <c>PRESENTATION-MUST-NOT-SPAWN-SUBPROCESSES</c>).
/// </summary>
/// <remarks>
///     <para>
///         Read-only by construction: the only argument vectors it ever builds are
///         <c>rev-parse --abbrev-ref HEAD</c>, <c>status --porcelain</c>,
///         <c>log -1 --format=%cr</c> and — since #666 —
///         <c>worktree list --porcelain</c>. There is no <c>push</c>/<c>commit</c>/<c>reset</c>
///         path here, and neither <see cref="GetStatus" /> nor
///         <see cref="ListWorktrees" /> takes a free-form command string, so this type
///         cannot be turned into a general shell-out.
///     </para>
///     <para>
///         Not an <c>ITool</c>, and deliberately so: it serves UI chrome for a
///         directory the user opened, takes no model input, and every command it runs
///         is read-only. Agent-driven git goes through <c>bash</c>, where
///         <c>PermissionRuleset.Default</c> gates it.
///     </para>
///     <para>
///         <b>Every redirected pipe is drained for the whole life of the child</b>
///         (#884). <see cref="RunGit" /> redirects stdout AND stderr and starts
///         BOTH readers before it waits. It did not: it waited, and only then read
///         stdout, while stderr was redirected and never touched at all. A child
///         that overruns an unread pipe blocks in <c>write</c>, so the wait never
///         returned, the 3 s ceiling fired on a perfectly healthy git, and the
///         caller got an empty result indistinguishable from "not a repository".
///     </para>
///     <para>
///         <b>Why the pipes are drained rather than stderr redirection simply
///         removed</b>, which is what #708 did to the notification runner and what
///         #884's first draft assumed. Two reasons, and the second is the one that
///         decides it:
///         <list type="number">
///             <item>
///                 The defect is symmetric. stdout was redirected and read only
///                 <em>after</em> the wait, so it had the identical exposure, and
///                 stdout is the likelier stream to overflow: one
///                 <c>git status --porcelain</c> line per changed path fills a 4 KiB
///                 Linux pipe at roughly a thousand files. Turning stderr
///                 redirection off would leave the branch badge able to hang on
///                 exactly the case #884 calls the plausible way in.
///             </item>
///             <item>
///                 Inheriting stderr writes straight onto the terminal, and the
///                 canonical renderer here (<c>Harbor.Tui.CellForge</c>) owns a
///                 fullscreen cell grid. #708's notifier could afford to be
///                 chatty on the user's stderr; a UI badge that shells out to git
///                 three times a refresh cannot. Draining keeps git's own
///                 diagnostics ("not a git repository", submodule warnings) and puts
///                 them in the log, which is where #708 wanted them to end up too.
///             </item>
///         </list>
///     </para>
/// </remarks>
public sealed class ProcessGitQuery(ILogger<ProcessGitQuery> logger) : IGitQuery
{
    /// <summary>Hard ceiling on a single git invocation, so a hung git cannot wedge a UI refresh.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return GitWorkspaceStatus.None;
        }

        try
        {
            string? branch = RunGit(directory, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
            if (string.IsNullOrEmpty(branch))
            {
                return GitWorkspaceStatus.None;
            }

            string? status = RunGit(directory, cancellationToken, "status", "--porcelain");
            bool isDirty = !string.IsNullOrEmpty(status?.Trim());
            int dirtyCount = isDirty
                ? status!.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

            string? lastCommit = RunGit(directory, cancellationToken, "log", "-1", "--format=%cr");

            return new GitWorkspaceStatus(
                branch.Trim(),
                isDirty,
                dirtyCount,
                string.IsNullOrEmpty(lastCommit) ? null : lastCommit.Trim());
        }
        catch (Exception ex)
        {
            // Not a repository, git missing from PATH, or a timeout: all expected
            // outcomes for a UI badge, not exceptions for the caller to handle.
            logger.LogDebug(ex, "Git status failed for {Dir}", directory);
            return GitWorkspaceStatus.None;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GitWorktreeInfo> ListWorktrees(
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<GitWorktreeInfo>();
        }

        try
        {
            string? porcelain = RunGit(directory, cancellationToken, "worktree", "list", "--porcelain");
            return porcelain is null ? Array.Empty<GitWorktreeInfo>() : WorktreePorcelainParser.Parse(porcelain);
        }
        catch (Exception ex)
        {
            // Not a repository, git missing from PATH, or a timeout — the same
            // expected outcomes GetStatus absorbs, and the same reason a jump
            // palette showing no worktree rows is a correct state rather than an
            // error the user needs to see.
            logger.LogDebug(ex, "Git worktree list failed for {Dir}", directory);
            return Array.Empty<GitWorktreeInfo>();
        }
    }

    /// <summary>
    ///     Runs one fixed git argument vector and returns its stdout, or
    ///     <see langword="null" /> when git is missing, times out, or exits non-zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two async readers are started BEFORE the wait, and that ordering is the
    ///         entire point of #884. A redirected pipe nobody is reading is a bounded
    ///         buffer the child can overrun: git then blocks in <c>write</c>, the exit
    ///         event never fires, <see cref="Process.WaitForExit(TimeSpan)" /> returns
    ///         <see langword="false" /> after the ceiling, the child is killed, and the
    ///         caller is handed an empty string that reads exactly like "not a
    ///         repository". The old order — wait, then
    ///         <c>StandardOutput.ReadToEnd()</c> — put stdout in that state too, and
    ///         dropped stderr on the floor entirely, so git's diagnostics (the very
    ///         reason the exit code was non-zero) were unrecoverable.
    ///     </para>
    ///     <para>
    ///         The reader is event-driven rather than <c>ReadToEndAsync</c> because this
    ///         method is sync and must stay sync: <c>IGitQuery</c> is a sync port with
    ///         three sync callers (<c>GitService</c>, <c>SessionGitTracker</c>, the jump
    ///         palette), and a join on the two read tasks would be exactly the
    ///         sync-over-async that <c>BannedSymbols.txt</c> bans —
    ///         <c>TaskAwaiter&lt;T&gt;.GetResult</c> — on the one path in this codebase
    ///         that runs on a render thread.
    ///     </para>
    ///     <para>
    ///         Line-oriented reassembly loses one thing <c>ReadToEnd</c> would have kept:
    ///         whether the child's last line ended in a newline. Nothing here needs it.
    ///         <c>rev-parse</c> and <c>log -1</c> are single values that every caller
    ///         <c>Trim</c>s, the porcelain <c>status</c> split is newline-delimited, and
    ///         <c>WorktreePorcelainParser</c> iterates to the end of the buffer and
    ///         already tolerates CRLF.
    ///     </para>
    /// </remarks>
    private string? RunGit(string workingDir, CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        // Both readers first, for both pipes. stderr is drained into the log rather
        // than discarded: a non-repository is the common case and its "fatal: not a
        // git repository" is the reason GetStatus answers None, so it is worth having
        // where #708 wanted a notifier's stderr to end up.
        // §PERF-006: per-call `new StringBuilder()` rented from StringBuilderPool
        // (this runs on the render thread, up to three spawns per UI refresh).
        // Deliberately NOT capped: the buffer is machine-parsed (porcelain
        // split, worktree parser), so truncation would corrupt data rather
        // than shorten a transcript. Oversized builders are simply dropped on
        // return per pool policy. Named locals so they detach in the finally
        // before the pooled builder is read or returned.
        using var stdout = StringBuilderPool.Rent(4096);
        process.OutputDataReceived += OnStdout;
        process.ErrorDataReceived += OnStderr;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            bool exited = process.WaitForExit(Timeout);
            if (!exited)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the timeout and the kill — nothing to do.
                }
            }

            // The parameterless overload is the one that waits for the ASYNCHRONOUS
            // readers as well as the child; the timed overload is documented not to. It
            // cannot block on the child here — either the child exited, or the kill above
            // ended it, and in both cases the pipes are at EOF — but it is what makes
            // reading `stdout` below a statement rather than a race.
            process.WaitForExit();

            if (!exited || process.ExitCode != 0)
            {
                return null;
            }

            // A cancelled caller gets nothing, but only once the child is accounted for:
            // the readers are already running, and abandoning them here would hand a
            // half-drained pipe back to a process that is about to be disposed.
            cancellationToken.ThrowIfCancellationRequested();
            return stdout.ToString();
        }
        finally
        {
            // Detach BEFORE the pooled builder is read or returned, so no
            // late callback can append into a recycled builder.
            process.OutputDataReceived -= OnStdout;
            process.ErrorDataReceived -= OnStderr;
        }

        void OnStdout(object _, DataReceivedEventArgs e)
        {
            // '\n', not AppendLine's Environment.NewLine. git emits LF on every
            // platform and all three consumers split or trim on it, so
            // reassembling the child's own bytes keeps their behaviour identical
            // on Windows instead of handing them a CRLF corpus they tolerate.
            if (e.Data is { } line)
            {
                stdout.Builder.Append(line).Append('\n');
            }
        }

        void OnStderr(object _, DataReceivedEventArgs e)
        {
            if (e.Data is { } line)
            {
                logger.LogDebug("git {Arguments} wrote to stderr: {Line}", string.Join(' ', args), line);
            }
        }
    }
}
