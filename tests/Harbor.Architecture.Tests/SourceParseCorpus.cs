// SourceParseCorpus.cs — the files the parsing substrate is proved and
// measured on.
//
// WHY A CORPUS AND NOT "THE TREE"
// -------------------------------
// The architecture suite grades three trees: `src/`, `apps/` and `tests/`. The
// `tests/` tree is the largest of the three and the one with the longest file
// headers, and those headers are where a parser's cost hides: a block comment
// of three hundred lines is three hundred lines the stripper has to blank.
//
// The corpus is read through the shared snapshot, so the files are read from
// disk ONCE for the whole assembly — the parity proof and the cost
// measurement both read the same bytes, and so does every rule that grades
// the tree.
//
// WHY `tests/` IS IN THE CORPUS
// -----------------------------
// Because the rules grade it. A rule that reads the test tree is a rule whose
// verdict depends on the test tree, and a parser that is proved on `src/`
// alone is proved on a third of the surface.

namespace Harbor.Architecture.Tests;

/// <summary>
///     The graded corpus: every <c>*.cs</c> file of the trees the
///     architecture rules read.
/// </summary>
internal static class SourceParseCorpus
{
    /// <summary>The trees the rules grade.</summary>
    internal static readonly string[] Trees = ["src", "apps", "tests"];

    /// <summary>
    ///     Every graded file, repo-relative and sorted.
    /// </summary>
    /// <returns>The graded files.</returns>
    internal static IReadOnlyList<string> Files()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var files = new List<string>();
        foreach (string tree in Trees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string path in SharedSourceTreeSnapshot.Shared.EnumerateCsFiles(dir))
            {
                files.Add(SourceScan.Relative(path));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    ///     The raw text of every graded file, in the order
    ///     <see cref="Files" /> returns them.
    /// </summary>
    /// <returns>The raw text of every graded file.</returns>
    internal static IReadOnlyList<string> Texts()
    {
        IReadOnlyList<string> files = Files();
        var texts = new List<string>(files.Count);

        foreach (string relative in files)
        {
            string full = Path.Combine(
                RepoPaths.RepoRoot ?? ".",
                relative.Replace('/', Path.DirectorySeparatorChar));

            texts.Add(SharedSourceTreeSnapshot.Shared.Get(full)?.Text ?? string.Empty);
        }

        return texts;
    }
}
