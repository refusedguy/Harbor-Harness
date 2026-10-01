// ConfigSectionDocRules.cs — #799, the guard for the documentation half of the
// same issue. RED BY CONSTRUCTION in the commit that adds it.
//
// THE DEFECT, AND WHAT IT IS NOT
// ------------------------------
// `src/Harbor.Application/Configuration/ConfigSections.cs` carried the type
// summary for `ToolingConfig` TWICE, one three-line `/// <summary>` block
// immediately followed by an identical one (lines 97-102).
//
// The interesting question is which of the two is the liar, because a duplicated
// claim is a maintenance hazard only when the copies can disagree. Here they
// cannot: `md5sum` over lines 97-99 and over lines 100-102 returns the same
// digest, so the two blocks are byte-identical and NEITHER states anything the
// other contradicts. The duplication was not a drift that already happened; it
// is a drift waiting for someone to edit one of them.
//
// `git blame` says where it came from, and it is not a considered second
// description. `bf8a1742e` (2026-07-20) wrote the original block. `65fb5e354`
// (2026-08-27, "авто-hot-reload … флаг tooling.autoReloadPlugins") then added the
// `AutoReloadPlugins` parameter to the record — and its diff opens with a freshly
// pasted copy of the type's existing summary, inserted ABOVE the original:
//
//     +/// <summary>
//     +///     Plugin + builtin-tool toggles.
/// +/// </summary>
     /// <summary>
     ///     Plugin + builtin-tool toggles.
     /// </summary>
//     public sealed record ToolingConfig(
//
// A copy-paste artifact, not an intent. The observable effect is that the
// generated `Harbor.Application.xml` (`GenerateDocumentationFile` is true,
// Directory.Build.props) carries two `<summary>` children under one member, so
// the sentence is rendered twice in every tooltip that shows it.
//
// WHY A RULE AND NOT A DELETION
// -----------------------------
// Deleting three lines fixes this instance and leaves the mechanism in place: the
// next feature that pastes a member's existing doc block above itself re-creates
// it, and a duplicate summary produces no compiler diagnostic — CS1570 (badly
// formed XML), CS1571 (duplicate `<param>`) and CS1591 (missing doc) are all in
// `NoWarn`, and a second `<summary>` is well-formed XML, so nothing upstream of a
// reviewer's eye can see it. So the deletion is the FIX; this file is what makes
// the fix stick, and it is scoped to catch the shape rather than the one type.
//
// NON-VACUITY
// -----------
//   1. `Scanner_FiresOnATwoSummaryBlock_AndStaysSilentOnOne` — the POSITIVE
//      CONTROL. The scanner is handed synthetic blocks: the real duplicate shape
//      must be reported; a block with one summary, and a file with no doc
//      comments at all, must not be. A rule that reports nothing is
//      indistinguishable from a rule that is broken, and a broken rule is worse
//      than none because it is believed.
//   2. `Scan_ReachesTheConfigSource_AndActuallySeesSummaries` — the FLOOR. The
//      directory must resolve, at least one file must be read, and the summaries
//      counted across them must clear a low bar. Without it, a renamed or moved
//      directory shrinks the scanned set and this rule reports the source clean
//      on the strength of having read less of it.
//
// SCOPE, AND ITS LIMITS — STATED, NOT HIDDEN
// ------------------------------------------
// The scan is `src/Harbor.Application/Configuration/*.cs`, nine files. That is a
// chosen scope, not an accident of convenience: it is the directory both halves
// of #799 live in, and a repository-wide sweep of the same shape has NOT been
// performed — this rule has never run outside CI, and landing a rule that fires
// on files nobody has read would report findings nobody can triage. Whether the
// same copy-paste artifact exists elsewhere under `src/` is therefore UNKNOWN,
// not absent; widening this is a decision to make with a CI log in hand, not a
// one-word change.
//
// Two known bounds of the scanner, both textual rather than a parse: a `<summary>`
// written inside a raw string literal in the source would be counted as a
// summary, and a doc block interrupted by a non-`///` comment line counts as two
// blocks. Neither occurs in the scanned directory (verified by running the same
// extraction over all nine files: exactly one block, the one above
// `ToolingConfig`, carries more than one).

namespace Harbor.Architecture.Tests;

/// <summary>One doc-comment block that opens more than one <c>&lt;summary&gt;</c>.</summary>
/// <param name="RelativePath">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line number of the block's first line.</param>
/// <param name="Summaries">How many <c>&lt;summary&gt;</c> elements the block opens.</param>
/// <param name="OwningDeclaration">The line the block documents, verbatim, trimmed.</param>
internal sealed record MultiSummaryDocSite(
    string RelativePath,
    int Line,
    int Summaries,
    string OwningDeclaration);

/// <summary>Finds doc-comment blocks that declare their subject more than once.</summary>
internal static class ConfigSectionDocProbe
{
    /// <summary>
    ///     The directory both halves of #799 live in. Scoped, not repo-wide — see
    ///     the file header for why, and for what that leaves unchecked.
    /// </summary>
    internal const string ScannedDirectory = "src/Harbor.Application/Configuration";

    /// <summary>
    ///     Lowest number of <c>&lt;summary&gt;</c> elements the scan must count
    ///     across the scanned files before its "no violations" verdict is allowed
    ///     to mean anything.
    /// </summary>
    /// <remarks>
    ///     The directory carries 83 today. Forty is a floor low enough that
    ///     ordinary documentation work will not trip it and high enough that a
    ///     directory which stopped being scanned — renamed, moved, emptied by a
    ///     refactor that took the types elsewhere — cannot quietly satisfy it.
    /// </remarks>
    internal const int SummaryFloor = 40;

    /// <summary>Repo-relative paths of the scanned files, sorted.</summary>
    internal static IReadOnlyList<string> EnumerateScannedFiles(string repoRoot)
    {
        string dir = Path.Combine(repoRoot, ScannedDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return
        [
            .. Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Every contiguous run of <c>///</c> lines in <paramref name="lines" />
    ///     that opens two or more <c>&lt;summary&gt;</c> elements.
    /// </summary>
    /// <remarks>
    ///     It counts, it does not compare: the rule is that one member has one
    ///     summary, and that holds for two summaries that differ just as much as
    ///     for two that are identical. (In the one instance in this repository the
    ///     two blocks are byte-identical, but a guard that went green as soon as
    ///     someone edited one of them would be guarding the wrong thing.)
    /// </remarks>
    internal static IReadOnlyList<(int Line, int Summaries, string Owner)> FindMultiSummaryBlocks(
        IReadOnlyList<string> lines)
    {
        var found = new List<(int Line, int Summaries, string Owner)>();
        int runStart = -1;
        int summaries = 0;

        // The sentinel iteration at i == lines.Count flushes a block that runs to
        // the end of the file; without it the last block in a file would be the
        // one block this scanner could never see.
        for (int i = 0; i <= lines.Count; i++)
        {
            string line = i < lines.Count ? lines[i] : string.Empty;
            bool isDocLine = line.TrimStart().StartsWith("///", StringComparison.Ordinal);

            if (isDocLine)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                if (line.Contains("<summary>", StringComparison.Ordinal))
                {
                    summaries++;
                }

                continue;
            }

            if (runStart >= 0)
            {
                if (summaries > 1)
                {
                    found.Add((runStart + 1, summaries, line.Trim()));
                }

                runStart = -1;
                summaries = 0;
            }
        }

        return found;
    }

    /// <summary>How many <c>&lt;summary&gt;</c> elements the whole file declares.</summary>
    internal static int CountSummaries(IReadOnlyList<string> lines)
    {
        int count = 0;
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && line.Contains("<summary>", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     <see cref="CountSummaries" /> over one repo-relative file. A file that
    ///     cannot be read counts ZERO rather than throwing: the floor assertion
    ///     is what turns "we could not look" into a red, and it can only do that
    ///     if this returns a number rather than escaping the test.
    /// </summary>
    internal static int CountSummariesIn(string repoRoot, string relativePath)
    {
        try
        {
            return CountSummaries(File.ReadAllLines(
                Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Scans every file in <see cref="ScannedDirectory" />.</summary>
    internal static IReadOnlyList<MultiSummaryDocSite> Scan(string repoRoot)
    {
        var found = new List<MultiSummaryDocSite>();

        foreach (string relative in EnumerateScannedFiles(repoRoot))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (IOException)
            {
                // A file that cannot be read is not evidence about summaries. The
                // floor in the non-vacuity test is what stops that from reading as
                // a clean directory.
                continue;
            }

            foreach ((int line, int summaries, string owner) in FindMultiSummaryBlocks(lines))
            {
                found.Add(new MultiSummaryDocSite(relative, line, summaries, owner));
            }
        }

        return found;
    }
}

/// <summary>
///     #799 — a member carries its <c>&lt;summary&gt;</c> once.
/// </summary>
public sealed class ConfigSectionDocRules
{
    /// <summary>
    ///     The rule: no doc-comment block in the configuration source declares its
    ///     subject twice.
    /// </summary>
    [Test]
    public async Task ConfigSource_DeclaresEachSummaryOnce()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<MultiSummaryDocSite> sites = ConfigSectionDocProbe.Scan(root);

        await Assert.That(sites).IsEmpty().Because(
            "one member, one <summary>. A block that opens two renders the sentence twice in "
            + "Harbor.Application.xml (GenerateDocumentationFile is on) and gives the next editor "
            + "two places to change one claim, which is how #599's default-model drift started. "
            + "The one instance here was a copy-paste artifact in the autoReloadPlugins commit, "
            + "not a second description: the blocks were byte-identical, so neither was wrong — "
            + "they simply could not stay that way. Found: " + Describe(sites)
            + ". Delete the duplicate block; do not merge the two summaries into prose.");
    }

    /// <summary>
    ///     Non-vacuity: the scanner must fire on the shape this rule exists for,
    ///     and stay silent on the shapes that are correct.
    /// </summary>
    [Test]
    public async Task Scanner_FiresOnATwoSummaryBlock_AndStaysSilentOnOne()
    {
        // The real instance, transcribed: the same block pasted above itself.
        string[] duplicated =
        [
            "/// <summary>",
            "///     Plugin + builtin-tool toggles.",
            "/// </summary>",
            "/// <summary>",
            "///     Plugin + builtin-tool toggles.",
            "/// </summary>",
            "public sealed record ToolingConfig(",
        ];

        await Assert.That(ConfigSectionDocProbe.FindMultiSummaryBlocks(duplicated).Count).IsEqualTo(1)
            .Because("this is ConfigSections.cs:97-102 verbatim in shape, and it is the only thing "
                + "the rule is for. If the scanner misses it, the rule above is green because it "
                + "read nothing, not because the source is correct.");

        // The same two summaries with DIFFERENT text must still be reported: the
        // rule is that a member has one summary, not that two identical ones are
        // tidy. A version of this scanner that compared for equality would go
        // green the moment an author edited one of the copies — that is, as soon
        // as the duplication started to be the drift it is able to become.
        string[] differing =
        [
            "/// <summary>",
            "///     Plugin + builtin-tool toggles.",
            "/// </summary>",
            "/// <summary>",
            "///     Plugin and builtin-tool switches. Reloading is off by default.",
            "/// </summary>",
            "public sealed record ToolingConfig(",
        ];

        await Assert.That(ConfigSectionDocProbe.FindMultiSummaryBlocks(differing).Count).IsEqualTo(1)
            .Because("two summaries for one member is the defect whether or not the two agree. "
                + "The identical case is the lucky one; the differing case is where the reader is "
                + "actually misled, and a guard that only caught the first would be disarmed by "
                + "the first edit.");

        // And the correct shapes must stay silent, or the rule is noise.
        string[] single =
        [
            "/// <summary>",
            "///     Compaction tuning.",
            "/// </summary>",
            "public sealed record CompactionConfig(",
        ];

        await Assert.That(ConfigSectionDocProbe.FindMultiSummaryBlocks(single)).IsEmpty()
            .Because("one summary is the shape the rule requires — the ordinary case, and a rule "
                + "that reported it would bury the real finding");

        // A doc block that runs to the end of the file is still a block. The
        // sentinel iteration in the scanner exists for exactly this, and without
        // this control the one block a scanner structurally cannot see would be
        // the one it never reports.
        string[] trailing =
        [
            "public sealed class Solo",
            "{",
            "    /// <summary>",
            "    ///     First.",
            "    /// </summary>",
            "    /// <summary>",
            "    ///     Second.",
            "    /// </summary>",
        ];

        await Assert.That(ConfigSectionDocProbe.FindMultiSummaryBlocks(trailing).Count).IsEqualTo(1)
            .Because("a doc block at the end of a file has no following declaration line to close "
                + "it, which is the case a line-driven scanner is most likely to drop silently");
    }

    /// <summary>
    ///     Non-vacuity: the scan must actually reach the configuration source and
    ///     see the summaries in it.
    /// </summary>
    [Test]
    public async Task Scan_ReachesTheConfigSource_AndActuallySeesSummaries()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<string> files = ConfigSectionDocProbe.EnumerateScannedFiles(root);

        await Assert.That(files.Count).IsGreaterThan(0)
            .Because(ConfigSectionDocProbe.ScannedDirectory + " is the directory this rule exists to "
                + "police. If it was renamed, moved or emptied, the rule above would report the "
                + "source clean on the strength of reading none of it — point ScannedDirectory at "
                + "the new home and say so in the file header.");

        int summaries = 0;
        foreach (string relative in files)
        {
            summaries += ConfigSectionDocProbe.CountSummariesIn(root, relative);
        }

        await Assert.That(summaries).IsGreaterThanOrEqualTo(ConfigSectionDocProbe.SummaryFloor)
            .Because("counted " + summaries + " <summary> elements across " + files.Count
                + " file(s) in " + ConfigSectionDocProbe.ScannedDirectory + ". Below the floor of "
                + ConfigSectionDocProbe.SummaryFloor + " means the scan is reading far less than it "
                + "was written against, so 'no violations' is a statement about the scanner rather "
                + "than about the source.");
    }

    private static string RequireRepoRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard reads the configuration source and cannot run from a published test "
            + "host. Returning quietly here would report 'no duplicate summaries' without having "
            + "read a single file, which is the one outcome this file exists to prevent.");

    private static string Describe(IReadOnlyList<MultiSummaryDocSite> sites)
        => sites.Count == 0
            ? "(none)"
            : string.Join(" | ", sites.Select(s =>
                s.RelativePath + ":" + s.Line + " (" + s.Summaries + " summaries) above `"
                + s.OwningDeclaration + "`"));
}
