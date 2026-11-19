// SkillPathValidator.cs: #2680 F152. Pure grammar guard for skill discovery
// paths. An aggregated or remote skill catalog contributes names and relative
// paths that a loader will combine with a skills root; both are untrusted. The
// guard rejects anything that could escape that root before any IO happens.
//
// Dependency free on purpose: no contracts, DI, filesystem access or public API
// of a frozen assembly changes. A later discovery slice can call these
// directly. This is the same shape RewindPathGuard uses in
// Harbor.Storage.Jsonl (#2659 U06).

namespace Harbor.Tools.Builtin;

/// <summary>
///     Validates the untrusted path fragments contributed by a skill source
///     (#2680 F152): one segment (a skill name or directory) and a relative
///     path made of such segments. Pure and ordinal: it never touches the
///     filesystem, so a caller can reject a bad path before resolving it.
/// </summary>
/// <remarks>
///     The grammar is deliberately stricter than the platform path rules.
///     Only ASCII letters, digits, dot, dash and underscore are allowed inside
///     a segment, and a segment may not begin with a dot. That single rule
///     rules out <c>.</c>, <c>..</c> and hidden names, so a relative path built
///     from safe segments can never contain a parent-directory traversal.
/// </remarks>
public static class SkillPathValidator
{
    /// <summary>Longest accepted single segment (a skill name or directory).</summary>
    public const int MaxSegmentLength = 100;

    /// <summary>Longest accepted relative path.</summary>
    public const int MaxRelativePathLength = 4096;

    /// <summary>
    ///     True when <paramref name="segment" /> is a safe single path
    ///     segment: non-empty, bounded, not starting with a dot, and made only
    ///     of ASCII letters, digits, dot, dash and underscore. No separator,
    ///     whitespace, control character or colon is accepted.
    /// </summary>
    /// <param name="segment">A skill name or directory name; may be null.</param>
    public static bool IsSafeSegment(string? segment) => IsSafeSegment(segment.AsSpan());

    /// <summary>
    ///     True when <paramref name="path" /> is a safe relative path:
    ///     non-empty, bounded, no leading separator, no backslash or colon,
    ///     and every forward-slash-separated segment is a
    ///     <see cref="IsSafeSegment(string)" />. Empty segments (a doubled or
    ///     trailing slash) are rejected, and <c>..</c> can never appear
    ///     because a segment may not start with a dot.
    /// </summary>
    /// <param name="path">A skill-relative path such as <c>skills/code-review/SKILL.md</c>; may be null.</param>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxRelativePathLength) return false;
        if (path[0] == '/' || path[0] == '\\') return false;
        if (path.AsSpan().IndexOfAny('\\', ':') >= 0) return false;

        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i != path.Length && path[i] != '/') continue;
            if (!IsSafeSegment(path.AsSpan(start, i - start))) return false;
            start = i + 1;
        }

        return true;
    }

    private static bool IsSafeSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment.Length > MaxSegmentLength) return false;
        if (segment[0] == '.') return false;

        foreach (char character in segment)
        {
            bool allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '-' or '_';
            if (!allowed) return false;
        }

        return true;
    }
}
