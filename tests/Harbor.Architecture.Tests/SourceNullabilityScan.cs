// SourceNullabilityScan.cs — the text scanner behind the two `null!` gates.
//
// `null!` compiles away: by the time an assembly is loaded there is nothing
// left to inspect, so the only way to see the null-forgiving operator is the
// source text. Two gates need that scan (the TEA state layer and the
// composition root), so the matcher regex lives here once instead of twice —
// a gate that copied the scanner would be free to drift from the rule it
// claims to enforce. The comment stripper both gates need is
// SourceScan.StripComments, shared for the same non-drift reason.
//
// WHAT IS ALLOWED, and why.
//
//   * `default!` is NOT matched. It is a different idiom with a legitimate use:
//     a `default(T)` sentinel held behind a flag and read only when the flag
//     is set. Converting that to Maybe<T> would add an allocation to model an
//     absence that is already impossible. See UiFrameworkNullabilityRules for
//     the worked example.
//
//   * The literal `null!` inside a comment or a doc comment is not matched. A
//     contributor documenting *why* they did not use `null!` must not fail the
//     gate, so comments are blanked out (newlines preserved, so a line number
//     still indexes the real source) before matching. String literals are not
//     stripped — the risk of a false positive there is smaller than the risk
//     of writing a comment-stripper with a string-literal state machine.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Finds `= null!` / `= null!;` initialisers in a slice of <c>src/</c>, by
///     project directory.
/// </summary>
internal static class SourceNullabilityScan
{
    /// <summary>
    ///     Matches the <c>null!</c> token pair. The leading group rejects a
    ///     preceding word character so an identifier such as <c>xNull!</c>
    ///     cannot match; the trailing group rejects a following word character
    ///     so documentation like <c>null!x</c> cannot either.
    /// </summary>
    /// <remarks>
    ///     <c>internal</c> rather than <c>private</c> since #559: a third gate
    ///     (<c>AgentStateContractRules</c>) matches a NARROWER rule — "a
    ///     null-forgiving initialiser on an <c>AgentState</c> declaration" — over
    ///     a slice this helper's <see cref="EnumerateSources" /> does not cover
    ///     (<c>src/</c> + <c>apps/</c>). It needs the SAME matcher; a second copy
    ///     of the regex is exactly the drift this file exists to prevent.
    /// </remarks>
    internal static readonly Regex NullForgivingOnNull = new(@"(?<!\w)null!(?!\w)", RegexOptions.Compiled);

    /// <summary>
    ///     Every <c>.cs</c> file under <c>src/</c> in a directory matching
    ///     <paramref name="directoryGlob" />, sorted for a stable failure
    ///     message. Returns empty when the repository root is not discoverable
    ///     (a published test host) — each gate pairs this with its own
    ///     discoverability self-check so an empty slice cannot pass vacuously.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateSources(string directoryGlob)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            return [];
        }

        var found = new List<string>();
        foreach (string dir in Directory.GetDirectories(src, directoryGlob))
        {
            found.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
        }

        // Never descend into build output — a stale obj/ copy would be counted as
        // a second occurrence of every violation.
        return
        [
            .. found.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                  .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     One <c>relative-path(line): offending text</c> entry per
    ///     <c>null!</c> that is not inside a comment, sorted by file.
    /// </summary>
    internal static IReadOnlyList<string> FindNullForgivingNull(IEnumerable<string> files)
    {
        string? root = RepoPaths.RepoRoot;
        var violations = new List<string>();

        foreach (string file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            // Split after stripping, so a line number here indexes the real source.
            string[] lines = SourceScan.StripComments(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (NullForgivingOnNull.IsMatch(lines[i]))
                {
                    string rel = root is null ? file : Path.GetRelativePath(root, file);
                    violations.Add($"{rel}({i + 1}): {lines[i].Trim()}");
                }
            }
        }

        return violations;
    }
}
