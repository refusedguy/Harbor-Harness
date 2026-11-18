// GitBaseDetector.cs — #2680 F179: pick the default base branch a code-review
// workflow should diff against. The helper is a pure function over a set of ref
// names: it starts no process, touches no repository and changes no existing
// product path. It is the primitive the review command can use instead of
// hard-coding "main" and hoping the repository agrees.
namespace Harbor.Tools.Builtin;

/// <summary>
///     Detects the default base branch from the refs available in a repository
///     (#2680 F179). Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         The candidate order mirrors the default base list of the Kilo review
///         command: <c>main</c>, then <c>master</c>, then <c>develop</c>. The
///         first candidate present in the supplied refs wins, regardless of the
///         order those refs are supplied in.
///     </para>
///     <para>
///         Local branches must match exactly (<c>main</c> or
///         <c>refs/heads/main</c>). Remote-tracking branches are recognised in
///         the short <c>origin/main</c> form used by <c>git branch -r</c> and in
///         the full <c>refs/remotes/origin/main</c> /
///         <c>remotes/upstream/main</c> forms, for the <c>origin</c> and
///         <c>upstream</c> remotes. A nested local branch such as
///         <c>feature/main</c> is therefore never mistaken for the base branch
///         <c>main</c>. Only canonical names are looked up in a set: no string
///         is split or scanned for a separator.
///     </para>
///     <para>
///         Matching is case-sensitive, as git ref names are.
///     </para>
/// </remarks>
public static class GitBaseDetector
{
    /// <summary>Default base branches, in the priority order used by review.</summary>
    public static readonly IReadOnlyList<string> DefaultCandidates = new[] { "main", "master", "develop" };

    /// <summary>Remotes whose tracking refs are recognised.</summary>
    private static readonly string[] KnownRemotes = ["origin", "upstream"];

    /// <summary>
    ///     Returns the highest-priority default base branch present in
    ///     <paramref name="refs" />, or <see langword="null" /> when none is.
    /// </summary>
    /// <param name="refs">
    ///     Available ref names. Local branches (<c>main</c>,
    ///     <c>refs/heads/main</c>) and remote-tracking branches
    ///     (<c>origin/main</c>, <c>refs/remotes/origin/main</c>, ...) are accepted.
    /// </param>
    public static string? Detect(IEnumerable<string> refs) => Detect(refs, DefaultCandidates);

    /// <summary>
    ///     Returns the first candidate present in <paramref name="refs" />, or
    ///     <see langword="null" /> when none is. Candidates are checked in the
    ///     order supplied, so callers may override the default priority list.
    /// </summary>
    /// <param name="refs">Available ref names, in any form described above.</param>
    /// <param name="candidates">Branch names to try, in priority order.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? Detect(IEnumerable<string> refs, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(candidates);

        var available = new HashSet<string>(refs, StringComparer.Ordinal);
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (IsBranchPresent(available, candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    ///     Tests whether <paramref name="name" /> is one of the default base
    ///     branches. The comparison is case-sensitive.
    /// </summary>
    public static bool IsDefaultBaseBranch(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        for (int i = 0; i < DefaultCandidates.Count; i++)
        {
            if (string.Equals(DefaultCandidates[i], name, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsBranchPresent(HashSet<string> refs, string candidate)
    {
        if (refs.Contains(candidate)) return true;
        if (refs.Contains("refs/heads/" + candidate)) return true;

        for (int i = 0; i < KnownRemotes.Length; i++)
        {
            string remote = KnownRemotes[i];
            if (refs.Contains(remote + "/" + candidate)) return true;
            if (refs.Contains("refs/remotes/" + remote + "/" + candidate)) return true;
            if (refs.Contains("remotes/" + remote + "/" + candidate)) return true;
        }

        return false;
    }
}
