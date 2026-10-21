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
    /// <remarks>
    ///     The directory walk is the shared snapshot's (#1791) — one walk per
    ///     directory for the whole assembly — and this filter is applied on top
    ///     of it, so the SET and the ORDER are exactly what they were before
    ///     the snapshot existed. The filter stays here because the filter is
    ///     what decides a rule's SCOPE, and the snapshot only knows the part
    ///     every rule agrees on.
    /// </remarks>
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

            found.AddRange(SharedSourceTreeSnapshot.Shared
                .EnumerateCsFiles(dir)
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
    ///     Every <c>*.axaml</c> file under the named repository-relative trees,
    ///     under the same <see cref="IsBuildOutput" /> filter and the same sort.
    /// </summary>
    /// <remarks>
    ///     Added for #942, and the reason is worth recording. The Avalonia half of
    ///     that defect was a money cell written by a <c>StringFormat</c> INSIDE a
    ///     <c>.axaml</c>, so every gate built on <see cref="EnumerateCsFiles" /> was
    ///     blind to it by construction — not by oversight. A binding is a real place
    ///     product behaviour is written, so the walk needs to be able to reach it;
    ///     this is that reach, sharing the filter so the two cannot drift on what
    ///     counts as product code.
    /// </remarks>
    /// <param name="trees">Repository-relative directories to walk.</param>
    internal static IReadOnlyList<string> EnumerateXamlFiles(params string[] trees)
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
                .EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p)));
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     Every <c>*.axaml</c> file under <c>src/</c> and <c>apps/</c> — the same
    ///     product trees <see cref="EnumerateProductCsFiles" /> walks.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateProductXamlFiles() => EnumerateXamlFiles(ProductTrees);

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
    /// <remarks>
    ///     <para>
    ///         The matched text is read ONCE, and that is the whole content of this
    ///         method now. <see cref="Match.Value" /> is a PROPERTY that
    ///         materialises the matched substring, so reading it inside the loop
    ///         allocated one string PER CHARACTER of the comment — a quadratic
    ///         cost in both time and memory.
    ///     </para>
    ///     <para>
    ///         The quadratic was invisible and enormous, because this repository's
    ///         source is comment-heavy and the architecture suite's own file
    ///         headers run to hundreds of lines: measured over
    ///         <c>tests/</c>, the 116 block comments hold 130 207 characters and
    ///         their lengths squared sum to 6 413 000 000, so ONE strip of that
    ///         tree allocated 12.8 GB of strings to blank out 127 KB of comments.
    ///         Every rule that read that tree paid it, and the stripper ran on
    ///         the tree once per reader: the architecture suite spent the better
    ///         part of its wall in the garbage collector, and every other rule on
    ///         the runner was slowed by the same pressure.
    ///     </para>
    ///     <para>
    ///         The fix is one line and it changes no verdict: the blanked text is
    ///         the same characters in the same places, it is simply not rebuilt
    ///         once per character. The whole stripper moved to
    ///         <see cref="SourceParse" /> after that, where it is a span-based
    ///         pass instead of two regexes — and it still produces the same
    ///         bytes, which <c>SourceParseParityTests</c> proves over every file
    ///         of the corpus.
    ///     </para>
    /// </remarks>
    private static string BlankOutComment(Match match)
    {
        string value = match.Value;
        var blank = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            blank[i] = value[i] == '\n' ? '\n' : ' ';
        }

        return new string(blank);
    }

    /// <summary>
    ///     Strips comments, preserving line count, so prose about a rule cannot
    ///     trip it. String literals are NOT stripped: the risk of a false
    ///     positive there is smaller than the risk of writing a string-literal
    ///     state machine, and every gate here matches a quoted literal.
    /// </summary>
    /// <remarks>
    ///     The implementation lives in <see cref="SourceParse" />, with the
    ///     rest of the parsing substrate. It is the SAME output, byte for byte:
    ///     one span-based pass over the file instead of two regexes and a
    ///     <see cref="MatchEvaluator" />, and the parity test compares the two
    ///     over every file of the corpus.
    /// </remarks>
    internal static string StripComments(string source) => SourceParse.StripComments(source);

    /// <summary>Matches an XML/markup comment, including the ones that span lines.</summary>
    private static readonly Regex MarkupComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    ///     Strips <c>&lt;!-- --&gt;</c> comments, preserving line count so a line
    ///     number computed downstream still points at the real markup line.
    /// </summary>
    /// <remarks>
    ///     <b>Not</b> <see cref="StripComments" />. That one is a C# stripper and is
    ///     wrong for markup on both counts: its <c>//</c> rule eats the rest of any
    ///     line containing a URL (<c>Source="http://…"</c>), and it does not
    ///     recognise <c>&lt;!-- --&gt;</c> at all — so a rule that ran it over a
    ///     <c>.axaml</c> would both truncate real markup and grade comment prose as
    ///     live bindings, which is exactly the false positive every other gate here
    ///     is written to avoid. Added with #942, the guard whose XAML rule needs it.
    /// </remarks>
    internal static string StripMarkupComments(string source) =>
        MarkupComment.Replace(source, BlankOutComment);
}
