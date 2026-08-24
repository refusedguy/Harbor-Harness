using System.Text;
namespace Harbor.Abstractions.Permissions;
/// <summary>
///     Quote-aware shell command analysis used when evaluating <c>bash</c> permission rules.
/// </summary>
/// <remarks>
///     <para>
///         Glob rules such as <c>bash "cat *"</c> compile to regexes whose <c>.*</c> crosses shell
///         control characters, so <c>cat file; rm -rf ~</c> would match an Allow rule. This matcher
///         splits the raw command into argv (handling single/double quotes and backslash escapes,
///         like POSIX sh) and detects control characters that change shell execution semantics.
///     </para>
///     <para>
///         Contract used by <see cref="PermissionRuleset.Evaluate" /> (A2):
///         <see cref="IsDestructiveCommand" /> denies recursive-force deletions of dangerous
///         targets (<c>/</c>, <c>.</c>, <c>~</c>, glob tails) before any rule walk, including
///         flag-swapped (<c>rm -fr /</c>) and compound (<c>cd / &amp;&amp; rm -rf .</c>) forms.
///         Deny rules are additionally tested against every target returned by
///         <see cref="GetDenyMatchTargets" /> — argv[0], its basename and the normalized
///         "basename + args" form of each command segment — so <c>/usr/bin/sudo ls</c> hits a
///         <c>sudo *</c> deny even without metacharacters. When
///         <see cref="HasShellMetacharacters" /> returns <see langword="true" />, Allow rules
///         must never match (the decision escalates to Ask).
///     </para>
///     <para>
///         All methods are pure and thread-safe.
///     </para>
/// </remarks>
public static class BashArgMatcher
{
    /// <summary>
    ///     Returns <see langword="true" /> if the command contains any of <c>; | &amp; ` $(&lt; &gt;</c>
    ///   or a newline <b>outside quotes</b>, or command-substitution / escape constructs
    ///   (<c>$()</c>, backticks, backslashes) <b>inside double quotes</b>, or has an
    ///     unterminated quote / trailing escape. Single-quoted content stays safe: POSIX
    ///     single quotes are fully literal. Such commands must never be silently allowed
    ///     by a glob rule.
    /// </summary>
    /// <param name="command">The raw command string from tool arguments.</param>
    public static bool HasShellMetacharacters(string command)
    {
        int n = command.Length;
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;

        for (int i = 0; i < n; i++)
        {
            char c = command[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                // POSIX shells execute $() and backtick substitution inside double quotes
                // and process backslash escapes there, so these must be flagged exactly
                // like unquoted metacharacters (Allow rules must not match).
                if (c == '\\' || c == '`'
                    || (c == '$' && i + 1 < n && command[i + 1] == '('))
                {
                    return true;
                }

                if (c == '"') inDouble = false;
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    break;
                case '"':
                    inDouble = true;
                    break;
                case '\\':
                    escaped = true;
                    break;
                case ';':
                case '|':
                case '&':
                case '`':
                case '<':
                case '>':
                case '\n':
                case '\r':
                    return true;
                case '$' when i + 1 < n && command[i + 1] == '(':
                    return true;
            }
        }

        return inSingle || inDouble || escaped;
    }

    /// <summary>
    ///     Returns <see langword="true" /> if any command segment invokes <c>rm</c> with BOTH a
    ///     recursive flag (<c>-r</c>/<c>-R</c>, letters inside combined clusters like <c>-rf</c>,
    ///     or <c>--recursive</c>) AND a force flag (<c>-f</c>, or <c>--force</c>) against a
    ///     dangerous target: a path that is empty after trailing-slash trim (i.e. <c>/</c>),
    ///     <c>.</c>, <c>..</c>, <c>~</c>, <c>*</c>, or ends with <c>/*</c>. Targets are the
    ///     non-flag tokens following the flags, with an optional bare <c>--</c> separator
    ///     skipped. Pure and thread-safe; runs on every bash evaluation, so it avoids LINQ.
    /// </summary>
    /// <param name="command">The raw command string from tool arguments.</param>
    public static bool IsDestructiveCommand(string command)
    {
        var segments = SplitSegments(command);
        for (int s = 0; s < segments.Count; s++)
        {
            var argv = SplitArgv(segments[s]);
            if (argv.Count == 0) continue;
            if (!BasenameOf(argv[0]).Equals("rm", StringComparison.Ordinal)) continue;

            bool recursive = false;
            bool force = false;
            int index = 1;

            // Consume the leading flag section: short clusters contribute their letters,
            // long options are recognized individually, a bare "--" ends the section.
            // S127: manual loop control — the body may skip the "--" token itself.
            int scan = index;
            while (scan < argv.Count)
            {
                string token = argv[scan];
                if (token.Length == 0 || token[0] != '-') break;
                if (token.Length == 1)
                {
                    scan++; // bare "-" carries no letters but is not a target
                    continue;
                }
                if (token[1] == '-')
                {
                    if (token.Length == 2)
                    {
                        scan++; // "--": everything after it is a literal target
                        break;
                    }
                    if (token.Equals("--recursive", StringComparison.Ordinal)) recursive = true;
                    else if (token.Equals("--force", StringComparison.Ordinal)) force = true;
                    scan++;
                    continue;
                }

                for (int k = 1; k < token.Length; k++)
                {
                    char flag = token[k];
                    if (flag == 'r' || flag == 'R') recursive = true;
                    else if (flag == 'f' || flag == 'F') force = true;
                }
                scan++;
            }
            index = scan;

            if (!recursive || !force) continue;

            for (; index < argv.Count; index++)
            {
                string target = argv[index].TrimEnd('/', '\\');
                if (target.Length == 0
                    || target.Equals(".", StringComparison.Ordinal)
                    || target.Equals("..", StringComparison.Ordinal)
                    || target.Equals("~", StringComparison.Ordinal)
                    || target.Equals("*", StringComparison.Ordinal)
                    || target.EndsWith("/*", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Returns the deduplicated strings a Deny rule is tested against for a bash command:
    ///     for EVERY segment (the command split on unquoted <c>;</c>, <c>&amp;&amp;</c>,
    ///     <c>||</c>, <c>|</c> and newlines) the segment's argv[0], its basename, and the
    ///     normalized form "basename + remaining tokens" (so <c>/usr/bin/sudo ls</c> yields
    ///     <c>sudo ls</c>, matching a <c>sudo *</c> deny). Empty when the command has no tokens.
    /// </summary>
    /// <param name="command">The raw command string from tool arguments.</param>
    public static IReadOnlyList<string> GetDenyMatchTargets(string command)
    {
        var segments = SplitSegments(command);
        List<string> targets = new();

        for (int s = 0; s < segments.Count; s++)
        {
            var argv = SplitArgv(segments[s]);
            if (argv.Count == 0) continue;

            string basename = BasenameOf(argv[0]);
            AddUnique(targets, argv[0]);
            AddUnique(targets, basename);

            if (argv.Count > 1)
            {
                var normalized = new StringBuilder(basename.Length + 8);
                normalized.Append(basename);
                for (int j = 1; j < argv.Count; j++)
                {
                    normalized.Append(' ');
                    normalized.Append(argv[j]);
                }
                AddUnique(targets, normalized.ToString());
            }
        }

        return targets.Count == 0 ? Array.Empty<string>() : (IReadOnlyList<string>)targets;
    }

    /// <summary>
    ///     Splits a command into argv using shlex-like rules: whitespace separates tokens,
    ///     single quotes are literal, double quotes allow backslash escapes, backslash escapes
    ///     the next character outside quotes. Quote characters themselves are not part of tokens.
    /// </summary>
    /// <param name="command">The raw command string from tool arguments.</param>
    public static IReadOnlyList<string> SplitArgv(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder(command.Length);
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;
        bool hasContent = false;

        void Flush()
        {
            if (!hasContent) return;
            tokens.Add(sb.ToString());
            sb.Clear();
            hasContent = false;
        }

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                else sb.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inDouble = false;
                else sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    hasContent = true;
                    break;
                case '"':
                    inDouble = true;
                    hasContent = true;
                    break;
                case '\\':
                    escaped = true;
                    hasContent = true;
                    break;
                case ' ':
                case '\t':
                case '\n':
                case '\r':
                    Flush();
                    break;
                default:
                    sb.Append(c);
                    hasContent = true;
                    break;
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>
    ///     Splits a raw command into pipeline/list segments on unquoted shell control operators
    ///     (<c>;</c>, <c>&amp;</c>, <c>|</c> — with <c>&amp;&amp;</c>/<c>||</c> consumed as one
    ///     operator — and newlines). All other characters, including quote characters and
    ///     backslash escapes, are copied verbatim so downstream <see cref="SplitArgv" /> parses
    ///     each segment exactly as written.
    /// </summary>
    private static List<string> SplitSegments(string command)
    {
        var segments = new List<string>(1);
        var sb = new StringBuilder(command.Length);
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;

        void Flush()
        {
            if (sb.Length == 0) return;
            segments.Add(sb.ToString());
            sb.Clear();
        }

        // S127: manual loop control — the body may skip a second operator char (||, &&).
        int i = 0;
        while (i < command.Length)
        {
            char c = command[i];
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (inSingle)
            {
                if (c == '\'') inSingle = false;
                sb.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inDouble = false;
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    sb.Append(c);
                    break;
                case '"':
                    inDouble = true;
                    sb.Append(c);
                    break;
                case '\\':
                    escaped = true;
                    sb.Append(c);
                    break;
                case ';':
                case '\n':
                case '\r':
                    Flush();
                    break;
                case '|':
                case '&':
                    if (i + 1 < command.Length && command[i + 1] == c) i++;
                    Flush();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
            i++;
        }

        Flush();
        return segments;
    }

    /// <summary>
    ///     Returns the substring after the last <c>/</c>; when argv[0] has no usable basename
    ///     (no slash, or a trailing slash) the input itself is returned.
    /// </summary>
    private static string BasenameOf(string argv0)
    {
        int slash = argv0.LastIndexOf('/');
        return slash >= 0 && slash < argv0.Length - 1 ? argv0[(slash + 1)..] : argv0;
    }

    private static void AddUnique(List<string> list, string candidate)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(candidate, StringComparison.Ordinal)) return;
        }
        list.Add(candidate);
    }
}
