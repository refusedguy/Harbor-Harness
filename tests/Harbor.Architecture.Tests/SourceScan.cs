// SourceScan.cs — the file-walking and path-filtering helpers that every
// repository-scanning gate in this project needs.
//
// WHY THIS FILE EXISTS
// --------------------
// Seven gates in this project each carried their own private
// `IsBuildOutput(path)` and their own `Directory.EnumerateFiles(src, "*.cs",
// AllDirectories)` loop. That is one convention, written seven times, and it
// already drifted: some copies excluded `obj/` and `bin/`, some also excluded
// `contrib/`, and one (a) missed `.worktrees/`, so a gate run from a checkout
// containing a sibling worktree would scan a stale copy of a file as if it were
// product code and report a violation that no longer exists. A guard whose
// result depends on which worktree it was launched from is not a guard.
//
// So the filter and the walk live here, once. The seven call sites now share
// one answer to "is this path product code?", which is the only question they
// were each really asking.
//
// This is the same decision `SourceNullabilityScan.cs` records for the `null!`
// matchers, and it is the reason a new gate must call these rather than
// re-declare them: a copied filter is a filter that will drift from the rule it
// is supposed to be enforcing.
//
// WHAT IS EXCLUDED, AND WHY
// --------------------------
//   * `obj/`, `bin/` — build output. A stale copy would be counted as a second
//     occurrence of every finding, and a deleted file would linger as a false
//     positive.
//   * `contrib/` — unmaintained and out of CI (AGENTS.md). Nothing there is
//     compiled, so a rule that fires on it describes code that cannot break.
//   * `tests/` — these rules ARE the tests; a rule must not police its own
//     fixtures, or every planted positive control would fail every other gate.
//   * `.worktrees/` — a sibling worktree is a full second copy of the
//     repository nested inside this one. Scanning it reports findings against
//     code the caller is not editing and doubles every count.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Repository file discovery shared by the source-scanning architecture rules.
/// </summary>
internal static class SourceScan
{
    /// <summary>Product trees the gates scan for real violations.</summary>
    internal static readonly string[] ProductTrees = ["src", "apps"];

    /// <summary>Matches one or more consecutive single-line comments.</summary>
    private static readonly Regex LineComment = new("//[^\n]*", RegexOptions.Compiled);

    /// <summary>Matches a C-style block comment, including the newlines it spans.</summary>
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    ///     Whether a path is build output, a test fixture, unmaintained
    ///     <c>contrib/</c> code, or a sibling worktree — in short, anything that
    ///     is not the product source a gate is meant to judge.
    /// </summary>
    internal static bool IsBuildOutput(string path)
    {
        string normalised = path.Replace('\\', '/');
        return normalised.Contains("/obj/", StringComparison.Ordinal)
            || normalised.Contains("/bin/", StringComparison.Ordinal)
            || normalised.Contains("/tests/", StringComparison.Ordinal)
            || normalised.Contains("/contrib/", StringComparison.Ordinal)
            || normalised.Contains("/.worktrees/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every <c>*.cs</c> file under the named repository-relative trees,
    ///     sorted for a stable failure message.
    /// </summary>
    /// <param name="trees">Repository-relative directories to walk.</param>
    internal static IReadOnlyList<string> EnumerateCsFiles(params string[] trees)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var found = new List<string>();
        foreach (string tree in trees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            found.AddRange(Directory
                .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p)));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     Every <c>*.cs</c> file under <c>src/</c> and <c>apps/</c> — the product
    ///     trees, minus everything <see cref="IsBuildOutput" /> rejects.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateProductCsFiles() => EnumerateCsFiles(ProductTrees);

    /// <summary>
    ///     Reads a file, returning <see langword="null" /> when it cannot be read.
    ///     A file that vanished between enumeration and read is a discovery
    ///     problem, not a rule failure — every gate pairs its use with a
    ///     discoverability self-check so an empty set cannot pass vacuously.
    /// </summary>
    internal static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Repo-relative, forward-slashed form of a path, for failure messages.</summary>
    internal static string Relative(string path) =>
        Path.GetRelativePath(RepoPaths.RepoRoot ?? ".", path).Replace('\\', '/');

    /// <summary>
    ///     Blanks out one block-comment match, preserving every newline so a line
    ///     number computed downstream still points at the real source line.
    /// </summary>
    private static string BlankOutComment(Match match)
    {
        var blank = new char[match.Length];
        for (int i = 0; i < match.Length; i++)
        {
            blank[i] = match.Value[i] == '\n' ? '\n' : ' ';
        }

        return new string(blank);
    }

    /// <summary>
    ///     Strips comments, preserving line count, so prose about a rule cannot
    ///     trip it. <see cref="LineComment" /> runs last because it cannot span a
    ///     line. String literals are NOT stripped: the risk of a false positive
    ///     there is smaller than the risk of writing a string-literal state
    ///     machine, and every gate here matches a quoted literal.
    /// </summary>
    internal static string StripComments(string source) =>
        LineComment.Replace(BlockComment.Replace(source, BlankOutComment), " ");
}
