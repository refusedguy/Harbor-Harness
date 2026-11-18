// GitCommandClassifier.cs — #2680 F159: tell a read-only git invocation from
// one that changes repository state. The helper is deliberately a pure function
// over the raw shell command: no process is started, no repository is touched,
// and nothing in the product's existing permission paths changes behaviour.
// It is the primitive a read-only agent (or a future sandbox policy) can use
// instead of hand-maintaining a list of forbidden git subcommands.
namespace Harbor.Tools.Builtin;

/// <summary>
///     Classifies whether a raw shell command contains a state-changing git
///     invocation (#2680 F159). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         The classifier is conservative: unknown git subcommands are treated
///         as mutating. A read-only caller that denies what this reports will
///         never accidentally allow a git verb it has never heard of. The flip
///         side is that a filter argument after <c>git branch --list</c> may be
///         reported as mutating; that is an acceptable false positive for a
///         gate, not a security hole.
///     </para>
///     <para>
///         Commands are split on unquoted shell separators first, so a mutating
///         git call hidden after <c>;</c> or <c>&amp;&amp;</c> is still seen.
///         Tokenisation is delegated to <see cref="BashArgMatcher.SplitArgv" />
///         so quotes and escapes behave exactly as they do in permission
///         evaluation. Compact global options such as <c>git -C /repo status</c>
///         are skipped before the subcommand is read.
///     </para>
/// </remarks>
public static class GitCommandClassifier
{
    /// <summary>Git subcommands that never change the worktree or the object store.</summary>
    private static readonly HashSet<string> ReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        "status", "diff", "diff-index", "diff-tree", "log", "shortlog", "show",
        "ls-files", "ls-tree", "ls-remote", "blame", "cat-file", "rev-parse",
        "rev-list", "grep", "count-objects", "describe", "name-rev", "merge-base",
        "for-each-ref", "check-ignore", "check-attr", "verify-commit", "verify-tag",
        "fsck", "var", "help", "version"
    };

    /// <summary>Sub-verbs that make a noun-like git command read-only.</summary>
    private static readonly HashSet<string> ReadOnlySubverbs = new(StringComparer.Ordinal)
    {
        "list", "show", "status", "info", "foreach", "get-url", "help"
    };

    /// <summary>Global git options that consume the following token as their value.</summary>
    private static readonly HashSet<string> ValueTakingGlobalFlags = new(StringComparer.Ordinal)
    {
        "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--super-prefix",
        "--exec-path", "--list-cmds"
    };

    /// <summary>Flags that turn <c>git config</c> into a read.</summary>
    private static readonly HashSet<string> ConfigReadFlags = new(StringComparer.Ordinal)
    {
        "--get", "--get-all", "--get-regexp", "--list", "-l", "--show-origin"
    };

    /// <summary>
    ///     Returns <see langword="true" /> when any segment of <paramref name="command" />
    ///     invokes git with a subcommand that can change repository state.
    /// </summary>
    /// <param name="command">Raw shell command, as passed to the bash tool.</param>
    public static bool IsMutating(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        IReadOnlyList<string> segments = SplitSegments(command);
        for (int i = 0; i < segments.Count; i++)
        {
            IReadOnlyList<string> argv = BashArgMatcher.SplitArgv(segments[i]);
            if (IsMutatingGitSegment(argv)) return true;
        }

        return false;
    }

    private static bool IsMutatingGitSegment(IReadOnlyList<string> argv)
    {
        int git = -1;
        for (int i = 0; i < argv.Count; i++)
        {
            string exe = Basename(argv[i]);
            if (exe.Equals("git", StringComparison.Ordinal)) { git = i; break; }

            // Wrappers and environment assignments still reach git.
            if (exe.Equals("sudo", StringComparison.Ordinal)
                || exe.Equals("env", StringComparison.Ordinal)
                || argv[i].Contains('='))
            {
                continue;
            }

            break;
        }

        if (git < 0) return false;

        int sub = -1;
        for (int i = git + 1; i < argv.Count; i++)
        {
            string token = argv[i];
            if (ValueTakingGlobalFlags.Contains(token))
            {
                i++; // the value is not the subcommand
                continue;
            }

            if (token.Length > 0 && token[0] == '-') continue;

            sub = i;
            break;
        }

        if (sub < 0) return false; // bare "git" prints help

        string verb = argv[sub];
        if (ReadOnlySubcommands.Contains(verb)) return false;

        // "git branch" and "git tag" with no name only list; a name means a write.
        if (verb.Equals("branch", StringComparison.Ordinal) || verb.Equals("tag", StringComparison.Ordinal))
        {
            for (int i = sub + 1; i < argv.Count; i++)
            {
                if (argv[i].Length > 0 && argv[i][0] != '-') return true;
            }

            return false;
        }

        // "git config --get ..." reads; the set/add/unset forms write.
        if (verb.Equals("config", StringComparison.Ordinal))
        {
            for (int i = sub + 1; i < argv.Count; i++)
            {
                if (ConfigReadFlags.Contains(argv[i])) return false;
            }

            return true;
        }

        // Noun-like verbs: their second word decides (stash list vs stash pop,
        // worktree list vs worktree add, remote -v vs remote add, ...).
        if (verb.Equals("stash", StringComparison.Ordinal)
            || verb.Equals("worktree", StringComparison.Ordinal)
            || verb.Equals("submodule", StringComparison.Ordinal)
            || verb.Equals("reflog", StringComparison.Ordinal)
            || verb.Equals("remote", StringComparison.Ordinal))
        {
            if (sub + 1 >= argv.Count) return false; // bare noun lists
            string mode = argv[sub + 1];
            return mode.Length > 0 && mode[0] != '-' && !ReadOnlySubverbs.Contains(mode);
        }

        // Unknown or explicitly mutating verb: treat as a write.
        return true;
    }

    private static string Basename(string token)
    {
        int slash = token.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? token : token[(slash + 1)..];
    }

    private static IReadOnlyList<string> SplitSegments(string command)
    {
        var segments = new List<string>();
        int start = 0;
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (escaped) { escaped = false; continue; }

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inDouble = false;
                continue;
            }

            switch (c)
            {
                case '\'': inSingle = true; break;
                case '"': inDouble = true; break;
                case '\\': escaped = true; break;
                case ';':
                case '\n':
                case '\r':
                    AddSegment(segments, command, start, i);
                    start = i + 1;
                    break;
                case '&':
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == c) i++;
                    AddSegment(segments, command, start, i + 1);
                    start = i + 1;
                    break;
            }
        }

        AddSegment(segments, command, start, command.Length);
        return segments.Count == 0 ? Array.Empty<string>() : segments;
    }

    private static void AddSegment(List<string> segments, string command, int start, int end)
    {
        if (end > start) segments.Add(command[start..end]);
    }
}
