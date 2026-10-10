using System.Diagnostics;
using System.Text;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Results;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Renders an ASCII directory tree. Respects <c>.gitignore</c> when <c>git</c> is
///     available; otherwise falls back to a built-in heavy-dir prune list. Caps depth and
///     entry count to keep output bounded.
/// </summary>
public sealed class TreeTool : ITool
{
    private const int DefaultMaxDepth = 3;
    private const int HardMaxDepth = 10;
    private const int DefaultMaxEntries = 1000;
    private const int HardMaxEntries = 10_000;
    private const int GitTimeoutMs = 4000;

    private static readonly HashSet<string> PrunedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "out",
        "target", "vendor", "__pycache__", ".next", ".nuxt",
        "coverage", ".turbo", ".cache"
    };

    private readonly ILogger<TreeTool> _logger;

    /// <summary>
    ///     Construct a <see cref="TreeTool" />.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public TreeTool(ILogger<TreeTool> logger) { _logger = logger; }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("tree");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Path();

    /// <inheritdoc />
    public string DisplayName => "Tree";

    /// <inheritdoc />
    public string Description =>
        "Render an ASCII directory tree. Respects .gitignore when git is available " +
        "(else prunes common build/VCS dirs). Caps depth (default 3) and entries (default 1000).";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "tree: ASCII directory tree (respects .gitignore)";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `tree` to get a quick mental model of a project layout",
        "Default depth=3 — raise for deeper trees, lower for an overview",
        "Set gitignore=false to include node_modules/bin/obj/.git/etc.",
        "Use `ls` for one-level directory listing with sizes and mtimes"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path":           { "type": "string",  "description": "Directory to tree (default: cwd)" },
                                                                          "maxDepth":       { "type": "integer", "description": "Max depth (default: 3, max: 10)" },
                                                                          "includeHidden":  { "type": "boolean", "description": "Include hidden files (default: false)" },
                                                                          "gitignore":      { "type": "boolean", "description": "Respect .gitignore via git ls-files (default: true)" },
                                                                          "maxEntries":     { "type": "integer", "description": "Max entries (default: 1000, max: 10000)" }
                                                                        }
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result depth = JsonArgValidator.OptionalIntInRange(
            args, "maxDepth", 1, HardMaxDepth, $"'maxDepth' must be between 1 and {HardMaxDepth}.");
        if (depth.IsFailure)
            return depth;

        return JsonArgValidator.OptionalIntInRange(
            args, "maxEntries", 1, HardMaxEntries, $"'maxEntries' must be between 1 and {HardMaxEntries}.");
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default) => Task.Run(() => ExecuteCore(args, context, cancellationToken), cancellationToken);

    private ToolResult ExecuteCore(JsonElement args, ToolContext context, CancellationToken ct)
    {
        // S2 (#376): the default root and relative resolution anchor at the
        // context working directory (the isolated worktree for sub-agent runs).
        string baseDir = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory!
            : Environment.CurrentDirectory;
        string path = JsonArgs.GetString(args, "path") ?? baseDir;
        int maxDepth = JsonArgs.GetInt(args, "maxDepth") is { } depth
            ? Math.Clamp(depth, 1, HardMaxDepth)
            : DefaultMaxDepth;
        bool includeHidden = JsonArgs.GetBool(args, "includeHidden");
        bool useGitignore = JsonArgs.GetBoolOrNull(args, "gitignore") ?? true;
        int maxEntries = JsonArgs.GetInt(args, "maxEntries") is { } entries
            ? Math.Clamp(entries, 1, HardMaxEntries)
            : DefaultMaxEntries;

        var resolvedPath = ToolPaths.ResolveAgainst(baseDir, path);
        if (resolvedPath.IsFailure)
            return ToolResult.Error(resolvedPath.Error);
        path = resolvedPath.Value;

        if (!Directory.Exists(path))
            return ToolResult.Error($"Directory not found: {path}");

        _logger.LogDebug("Tree: {Path} (maxDepth={MaxDepth})", path, maxDepth);

        // Try to get the tracked-files set from `git ls-files` (cached per call).
        var tracked = useGitignore ? TryGetGitTrackedFiles(path) : null;

        var state = new WalkState(maxEntries);
        using var sb = StringBuilderPool.Rent(8192);
        var b = sb.Builder;

        // Root line: show the directory name with trailing slash.
        string rootName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(rootName)) rootName = path;
        b.Append(rootName).Append('/').Append('\n');

        Walk(_logger, path, "", 0, maxDepth, includeHidden, tracked, b, state, ct);

        if (state.Truncated)
            b.Append("\n… truncated at ").Append(maxEntries).Append(" entries");

        int dirs = state.Dirs;
        int files = state.Files;

        return ToolResult.Success(
            b.ToString().TrimEnd() + $"\n\n{dirs} director{(dirs == 1 ? "y" : "ies")}, {files} file(s)",
            new
            {
                path,
                maxDepth,
                includeHidden,
                gitignore = useGitignore,
                dirs,
                files,
                truncated = state.Truncated
            });
    }

    private static void Walk(
        ILogger logger,
        string dir,
        string prefix,
        int depth,
        int maxDepth,
        bool includeHidden,
        HashSet<string>? tracked,
        StringBuilder sb,
        WalkState state,
        CancellationToken ct)
    {
        if (depth >= maxDepth || state.Truncated) return;

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // ROP-A Z1 п.15: silent skip keeps the tree contract; trace explains.
            logger.LogTrace(ex, "tree: skipping unreadable directory {Dir}", dir);
            return;
        }

        // Sort: dirs first, then files; ignore-case ordinal.
        Array.Sort(entries, static (a, b) =>
        {
            bool ad = a is DirectoryInfo;
            bool bd = b is DirectoryInfo;
            if (ad != bd) return ad ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        // Filter hidden + prune list + gitignore.
        var visible = new List<FileSystemInfo>(entries.Length);
        foreach (var e in entries)
        {
            if (!includeHidden && e.Name.StartsWith('.')) continue;

            if (e is DirectoryInfo di)
            {
                if (tracked is null && PrunedDirNames.Contains(e.Name)) continue;
                visible.Add(di);
            }
            else
            {
                if (tracked is not null)
                {
                    string rel = Path.GetRelativePath(dir, e.FullName).Replace('\\', '/');
                    if (!tracked.Contains(rel)) continue;
                }
                visible.Add(e);
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (state.Truncated) return;

            var entry = visible[i];
            bool last = i == visible.Count - 1;
            string branch = last ? "└── " : "├── ";
            string childPrefix = prefix + (last ? "    " : "│   ");

            if (!state.TryAdd()) return;

            sb.Append(prefix).Append(branch).Append(entry.Name);

            if (entry is DirectoryInfo)
            {
                state.DirAdded();
                // If prune list hit (git tracked but dir empty) we still show it.
                sb.Append('/').Append('\n');
                Walk(logger, entry.FullName, childPrefix, depth + 1, maxDepth,
                    includeHidden, tracked, sb, state, ct);
            }
            else
            {
                state.FileAdded();
                sb.Append('\n');
            }
        }
    }

    /// <summary>
    ///     ROP-A Z1 п.16: the 45-line try/catch skeleton collapses to a Result.Try
    ///     folded to nullable at the call site. "No git" and "git failed" stay
    ///     indistinguishable in the public contract (both → no pruning) but are
    ///     now visible in trace logs.
    /// </summary>
    private HashSet<string>? TryGetGitTrackedFiles(string root) =>
        Result.Try(() => CollectGitTrackedFiles(root, _logger), ResultErrors.Message)
            .TapError(reason => _logger.LogTrace("tree: gitignore pruning disabled: {Reason}", reason))
            .AsMaybe()
            .GetValueOrDefault();

    /// <summary>
    ///     <c>git ls-files</c> for the prune list, read as a set of repo-relative paths.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both pipes are drained for the whole life of the child</b> (#908). This
    ///         redirected stdout AND stderr, waited <see cref="GitTimeoutMs" /> ms, and only
    ///         then read stdout line by line — under a comment that said "don't begin async
    ///         read — read synchronously with a hard timeout". That comment was the bug.
    ///     </para>
    ///     <para>
    ///         A redirected pipe is a bounded buffer. <c>ls-files --others</c> prints one
    ///         line per untracked-but-not-ignored path, so on a large repository it
    ///         overruns a 4 KiB Linux pipe at roughly a thousand files and then blocks in
    ///         <c>write()</c>. Nothing was draining it, so the child never exited, the wait
    ///         gave up at the ceiling, <c>ToolErrors.KillQuietly</c> killed it, and the
    ///         throw became a <c>Result</c> failure that
    ///         <see cref="TryGetGitTrackedFiles" /> turns into <c>null</c> — i.e. no
    ///         gitignore pruning. The timeout did not make this safe; it is what made the
    ///         deadlock look like a slow git.
    ///     </para>
    ///     <para>
    ///         The readers are event-driven rather than <c>ReadToEndAsync</c> because this
    ///         method is sync and its caller is sync. A join on the read tasks would be the
    ///         <c>TaskAwaiter&lt;T&gt;.GetResult</c> that <c>BannedSymbols.txt</c> bans, and
    ///         <c>BeginOutputReadLine</c>/<c>BeginErrorReadLine</c> is the BCL's own
    ///         sync-side drain — the same pair <c>ProcessGitQuery</c> uses, for the same
    ///         reason.
    ///     </para>
    ///     <para>
    ///         The parameterless <see cref="Process.WaitForExit()" /> after the timed one is
    ///         load-bearing, not decoration, and it runs on the timeout path too: the timed
    ///         overload is documented NOT to wait for asynchronous readers, so it is this
    ///         that makes the collected set complete rather than a race with a thread-pool
    ///         callback. It cannot block on the child — either the child exited, or the kill
    ///         above ended it, and in both cases the pipes are at EOF.
    ///     </para>
    ///     <para>
    ///         Line-oriented reassembly loses one thing <c>ReadToEnd</c> would have kept:
    ///         whether the child's last line ended in a newline. Nothing here needs it — a
    ///         path is a path, and <c>ls-files</c> emits one per line — and blank lines are
    ///         skipped exactly as the old <c>ReadLine</c> loop skipped them. stderr is
    ///         logged rather than discarded, because a non-zero exit is otherwise
    ///         unexplainable: it is the reason <c>TryGetGitTrackedFiles</c> answers
    ///         <c>null</c> and pruning silently turns off.
    ///     </para>
    /// </remarks>
    private static HashSet<string> CollectGitTrackedFiles(string root, ILogger logger)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        psi.ArgumentList.Add("ls-files");
        psi.ArgumentList.Add("--cached");
        psi.ArgumentList.Add("--others");
        psi.ArgumentList.Add("--exclude-standard");

        using var p = new Process { StartInfo = psi };
        p.Start();

        // Both readers first, for both pipes (#908). See the remarks: the timeout this
        // used to rely on is what the deadlock hid behind.
        var set = new HashSet<string>(StringComparer.Ordinal);
        p.OutputDataReceived += (_, e) =>
        {
            // git emits LF on every platform and the paths are compared after
            // normalising the separator, so the child's own bytes are kept as they are.
            if (e.Data is { Length: > 0 } line)
            {
                set.Add(line.Replace('\\', '/'));
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                logger.LogTrace("git ls-files wrote to stderr: {Line}", line);
            }
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        bool exited = p.WaitForExit(GitTimeoutMs);
        if (!exited)
        {
            ToolErrors.KillQuietly(p);
        }

        // Waits for the asynchronous readers as well as the child, and it runs on BOTH
        // paths on purpose. The timed overload is documented not to wait for asynchronous
        // readers, so this is what makes `set` a statement rather than a race with a
        // thread-pool callback — and on the timeout path the readers are still live, so
        // skipping it would dispose the process out from under them. It cannot block on the
        // child: either the child exited, or the kill above ended it, and in both cases the
        // pipes are at EOF.
        p.WaitForExit();

        if (!exited)
        {
            throw new TimeoutException($"git ls-files did not finish within {GitTimeoutMs}ms.");
        }

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git ls-files exited with code {p.ExitCode}.");

        return set;
    }

    private sealed class WalkState(int maxEntries)
    {
        public int Count { get; private set; }
        public int Dirs { get; private set; }
        public int Files { get; private set; }
        public bool Truncated { get; private set; }

        public bool TryAdd()
        {
            if (Count >= maxEntries)
            {
                Truncated = true;
                return false;
            }
            Count++;
            return true;
        }

        public void DirAdded() => Dirs++;
        public void FileAdded() => Files++;
    }
}
