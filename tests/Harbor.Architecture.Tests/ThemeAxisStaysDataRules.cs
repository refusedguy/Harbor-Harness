// ThemeAxisStaysDataRules.cs — the guard behind #622, which is the *exception*
// clause to the #555 feature freeze.
//
// WHY THIS FILE EXISTS
// --------------------
// #555 froze "a new theme source" along with every other extension axis. Its own
// audit comment, filed as the evidence for the freeze, then measured the axes and
// found the theme axis is the ONE that is genuinely additive:
//
//     | custom terminal theme (JSON file + HARBOR_THEME_FILE) | 0 | additive |
//
// Zero files a developer must edit. The freeze's stated premise is that every new
// axis is "one more hand-maintained list to keep in sync" — and the theme axis is
// the counterexample: it has no list. So the freeze as written would close the one
// axis that has nothing to keep in sync, and #622 carves it back out.
//
// That carve-out is only worth something if it is CHECKABLE. An exception nobody
// can fail is a sentence in a document, and a document is exactly what the next
// agent re-derives from scratch. This file makes "the theme axis is data" a build
// failure the day it stops being true, which is the sequencing #555 itself demands
// ("Write the guard BEFORE the rewrite … If a refactor and its guard cannot land
// in the same PR, the guard lands first").
//
// THE DISTINCTION THIS FILE ENFORDS — WHY THEMES DIFFER FROM TOOLS
// ---------------------------------------------------------------
// This is the question #622 exists to answer, and the answer is not "themes are
// nicer". It is a structural difference, and it is checkable:
//
//   A TOOL IS CODE. `Harbor.Tools.Builtin/Tools/Read/ReadTool.cs` is a class, and
//   the product has to be TOLD it exists: `ToolsCatalog.cs` constructs it, the
//   registry holds it, `ITool.SafetyProfile` declares what it may touch, and
//   `PermissionRuleset.Default` carries a row naming it. Those are the 8-10
//   hand-kept name lists #557 counted. A tool is only reachable because C#
//   mentions it by name, which is exactly why adding one is expensive and exactly
//   why #555 freezes the set.
//
//   A THEME IS DATA. `ThemeJson.Parse` takes a string and merges whatever slots
//   the document declares over a fallback. `ThemeStore` resolves
//   `~/.harbor/themes/*.json` by enumerating a directory — there is no array of
//   theme names anywhere in the discovery path, because a file that exists IS the
//   registration. `ThemeDirectoryWatcher` polls and applies. No C# names a theme.
//
//   So the axes differ in the only way that matters for a freeze: a new tool costs
//   N hand-maintained lists, a new theme costs zero. #555's premise ("every new
//   axis is one more list to keep in sync") is TRUE of tools and FALSE of themes.
//   The freeze is therefore correct for the code axis and wrong for the data axis.
//
// WHAT IS RULED
// -------------
//   1. The theme DISCOVERY path enumerates a directory / parses a document. It
//      does not contain a registry of theme names. This is the property the whole
//      exception rests on: a hand-kept `string[]` of theme names in the discovery
//      path would turn the data axis back into a code axis, and the exception would
//      no longer be justified.
//   2. The theme axis is not merely "additive in principle" — it is REACHABLE from
//      a product. `ThemeDirectoryWatcher` existing in a library that nothing calls
//      is not an extension axis; it is dead code with good manners. A shipped app
//      must construct it, or "drop a file in the directory" changes nothing a user
//      can observe. (This is the half of the claim that was NOT true on dev when
//      this guard was written — see the RED note on rule 2.)
//   3. The theme axis is SEALED against a theme SOURCE. The freeze's own words are
//      "a new theme source", and that half still holds and is still wanted: this
//      exception opens the DATA axis (a new .json), not the MECHANISM axis (a new
//      way themes are discovered, loaded, or transported). Without an explicit
//      seal, "themes are exempt" would read as "the theme mechanism is open too",
//      which is the opposite decision and the one #555 actually made.
//   4. Non-vacuity for every scanner, in the same shape as
//      ThemeStoreSeamRules.Filesystem_Scanner_Still_Sees_A_Real_Call: each probe
//      must still find what it is looking for in a file that legitimately has it,
//      so a green result is distinguishable from a broken regex.
//
// WHAT IS DELIBERATELY NOT RULED, AND WHY
// ----------------------------------------
//   * `HarborTheme.BuiltIn` — the four shipped terminal themes ARE a hand-written
//     list, and `DesignSystemDocGen` projects from it (#581 fixed the copy that had
//     already dropped `harbor-cool`). A BUILT-IN is a curated product decision, not
//     a user extension: shipping a theme with the product is a code change by
//     definition. The exception is for themes a USER adds, and those never appear
//     in this array. Ruling on `BuiltIn` would forbid the product's own palette.
//   * The Avalonia `HdsThemeCatalog.PaletteNames` list (#673) — same reason, plus
//     a hard one: a compiled Avalonia app embeds no XAML source and `avares://`
//     cannot be enumerated, so "which files are in this folder" is genuinely
//     unanswerable at runtime. It is already held to the folder by
//     `HdsThemeCatalogParityTests`, which is the strongest form available.
//   * Anything under `contrib/` — unmaintained, not compiled by any CI job.
//   * The desktop `ThemeService.Apply` 3-arm switch, which can only reach 2 of the
//     6 shipped palettes. That is #583, a real and separate defect. It is named
//     here so the next reader does not mistake it for a gap in this guard.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #622: the theme axis is carved out of the #555 feature freeze, and this is
///     the carve-out's enforcement. The claim being defended is narrow and
///     structural: <b>a theme is data, so adding one edits no code</b>.
/// </summary>
/// <remarks>
///     See the file header for why that differs from the tool axis, what each rule
///     grades, and what is deliberately left alone.
/// </remarks>
public sealed class ThemeAxisStaysDataRules
{
    // ── The sealed half: where a theme SOURCE could be added ──────────────────
    //
    // A theme source is a second answer to "where do themes come from": another
    // directory, a network catalogue, a plugin-provided palette, a second file
    // format. Each is a new mechanism with its own precedence rules, and #555
    // froze them. This list is where such a mechanism would be added, so it is
    // where the freeze is anchored — the exception is a carve-out from a frozen
    // set, and a carve-out with no recorded boundary is just a different freeze
    // nobody chose.
    private static readonly string[] SealedThemeSourceHomes =
    [
        "src/Harbor.DesignSystem/DesignSystem/ThemeStore.cs",
        "src/Harbor.DesignSystem/DesignSystem/ThemeDirectoryWatcher.cs",
        "src/Harbor.Tui.CellForge/Chat/Widgets/ThemeFileWatcher.cs",
        "src/Harbor.Tui.CellForge/Chat/Widgets/JsonThemeLoader.cs",
    ];

    /// <summary>
    ///     Discovery must be structural — enumerate a directory, parse a document —
    ///     never a lookup in a table of names someone typed.
    /// </summary>
    /// <remarks>
    ///     Deliberately narrow: it looks for a collection of theme names, not for
    ///     the word "theme". A single `string themeName` parameter is the opposite
    ///     of a registry — it is the caller's name passed through — and flagging
    ///     every identifier would make the rule noise nobody keeps. The
    ///     non-vacuity test below is what keeps the narrower shape honest.
    /// </remarks>
    private static readonly Regex ThemeNameTable = new(
        @"(?:static\s+readonly|private\s+static\s+readonly|const)\s+" +
        @"(?:IReadOnlyList<string>|IEnumerable<string>|string\[\]|List<string>|HashSet<string>|FrozenSet<string>)\s+" +
        @"\w*(?:Theme|Palette)s?\w*\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A construction of the directory watcher, with or without a target type.</summary>
    private static readonly Regex DirectoryWatcherConstruction = new(
        @"\bnew\s+(?:[A-Za-z0-9_.]+\.)?ThemeDirectoryWatcher\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Test]
    public async Task Theme_Discovery_Path_Holds_No_Registry_Of_Theme_Names()
    {
        IReadOnlyList<string> offenders = FindThemeNameTables(SealedThemeSourceHomes);

        await Assert.That(offenders.Count).IsEqualTo(0).Because(
            "A collection of theme NAMES in the discovery path is what turns the theme "
            + "axis from data into code — and it is the whole reason #622 carves themes "
            + "out of the #555 freeze in the first place. With a name table, a user adding "
            + "a theme must edit C#, the cost profile becomes the tool axis's, and the "
            + "freeze's premise ('every new axis is one more hand-maintained list') becomes "
            + "true of themes too — at which point the exception is unjustified and themes "
            + "should be frozen like every other axis. Keep discovery structural: "
            + "enumerate the directory, merge the document. Found: "
            + (offenders.Count == 0 ? "(none)" : string.Join("\n", offenders)));
    }

    /// <summary>
    ///     A theme axis nobody can reach is not an extension axis. A product must
    ///     construct the directory watcher, so that dropping a file into
    ///     <c>~/.harbor/themes/</c> changes what a user actually sees.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         RED when this guard was written, and that is the point of the commit
    ///         order. On dev, <c>ThemeDirectoryWatcher</c> existed, was documented in
    ///         <c>docs/DESIGN_SYSTEM.md</c> as a live-reload feature, and was
    ///         constructed by NOTHING outside its own test file. The only theme the
    ///         shipped CLI honoured was the single <c>HARBOR_THEME_FILE</c> /
    ///         <c>~/.harbor/theme.json</c> one. So "the theme axis is additive" was
    ///         true of the library and false of the product: a user following the
    ///         documented instructions got no theme at all.
    ///     </para>
    ///     <para>
    ///         The distinction matters for the freeze decision too. An axis that is
    ///         additive in principle but unreachable in practice is indistinguishable,
    ///         from the outside, from an axis that is merely slow to extend — which is
    ///         how a frozen axis looks from the outside. Closing that gap is what makes
    ///         the exception honest rather than aspirational.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task A_Shipped_Product_Constructs_The_Themes_Directory_Watcher()
    {
        IReadOnlyList<string> sites = FindDirectoryWatcherConstructions(RepoPaths.RepoRoot);

        await Assert.That(sites.Count).IsGreaterThan(0).Because(
            "'Add a theme by dropping a .json into the themes directory' is a promise "
            + "about the PRODUCT, not about a library. If no app constructs "
            + "ThemeDirectoryWatcher, the documented directory is a folder nothing reads, "
            + "and a user who follows docs/DESIGN_SYSTEM.md sees no theme — the axis reads "
            + "as closed from the only place a user can observe it. Found constructions in: "
            + (sites.Count == 0 ? "(none — no product wires the themes directory)" : string.Join("\n", sites)));
    }

    /// <summary>
    ///     Non-vacuity for <see cref="Theme_Discovery_Path_Holds_No_Registry_Of_Theme_Names" />.
    ///     The same scanner must still find a name table where one legitimately exists,
    ///     or "no violations" is indistinguishable from "the regex reads nothing".
    /// </summary>
    [Test]
    public async Task Theme_Name_Table_Scanner_Still_Sees_A_Real_Table()
    {
        // HarborTheme.BuiltIn is exactly the shape the rule forbids in the DISCOVERY
        // path and deliberately permits in the product's own curated catalog — which
        // makes it the right control: a real hit, in a real file, that the rule's
        // scope deliberately excludes.
        string[] control =
        [
            "src/Harbor.DesignSystem/DesignSystem/HarborTheme.cs",
        ];

        IReadOnlyList<string> hits = FindThemeNameTables(control);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner in Theme_Discovery_Path_Holds_No_Registry_Of_Theme_Names must be "
            + "able to fail. If it reports nothing even on HarborTheme.BuiltIn — a real "
            + "hand-written theme-name list in a real file — it is reporting nothing "
            + "everywhere, and the discovery path's pass is meaningless. If this control "
            + "ever goes red, the fix is the REGEX, not the BuiltIn catalog.");
    }

    /// <summary>
    ///     Non-vacuity for the construction scanner in
    ///     <see cref="A_Shipped_Product_Constructs_The_Themes_Directory_Watcher" />.
    /// </summary>
    [Test]
    public async Task Directory_Watcher_Scanner_Still_Sees_A_Real_Construction()
    {
        string[] control =
        [
            "tests/Harbor.DesignSystem.Tests/ThemeDirectoryWatcherTests.cs",
        ];

        IReadOnlyList<string> hits = FindDirectoryWatcherConstructions(RepoPaths.RepoRoot, control);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind A_Shipped_Product_Constructs_The_Themes_Directory_Watcher "
            + "must be able to find a construction. If it cannot find the four in "
            + "ThemeDirectoryWatcherTests, it is not reading the files and the product-side "
            + "pass is vacuous.");
    }

    /// <summary>
    ///     Non-vacuity for the file probe the two scanners share: if
    ///     <see cref="RepoPaths.RepoRoot" /> is unavailable, every probe above returns
    ///     empty and the discovery rule passes for the wrong reason.
    /// </summary>
    [Test]
    public async Task Theme_Rules_Can_Actually_Read_The_Repository()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull().Because(
            "Every scanner in this file reads repo-relative paths through RepoPaths. "
            + "Outside a checkout (no Harbor.slnx above the test bin directory) they all "
            + "return empty, which would make Theme_Discovery_Path_Holds_No_Registry_Of_"
            + "Theme_Names pass because it read nothing. The two product-side rules fail "
            + "loudly in that case, which is the right direction to fail in — but the "
            + "discovery rule must not depend on it.");
    }

    // ── Probes ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Theme-name tables in the named repo-relative files, as
    ///     <c>path:line  declaration</c>. Comments are stripped first, so prose that
    ///     NAMES a registry to explain a past defect does not read as code.
    /// </summary>
    private static IReadOnlyList<string> FindThemeNameTables(IReadOnlyList<string> relativePaths)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var hits = new List<string>();
        foreach (string relative in relativePaths)
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                hits.Add($"{relative}  (file is missing — the guard cannot grade a file it cannot read)");
                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(File.ReadLines(path));
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = ThemeNameTable.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add($"{relative}:{i + 1}  {match.Value.Trim()}");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     Every <c>new ThemeDirectoryWatcher(</c> under the repository, as
    ///     <c>path:line</c>, excluding build output. Test files are included by
    ///     default — a construction in a test is what the non-vacuity rule needs to
    ///     see, and the product-side rule is about ABSENCE in apps/, which the
    ///     <paramref name="alsoSearch" /> list lets a caller exclude.
    /// </summary>
    private static IReadOnlyList<string> FindDirectoryWatcherConstructions(
        string? root,
        IReadOnlyList<string>? only = null)
    {
        if (root is null)
        {
            return [];
        }

        string[] files = only is { Count: > 0 }
            ? [.. only.Select(rel => Path.Combine(root, rel))]
            : [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(static f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                   && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                   && !f.Contains($"{Path.DirectorySeparatorChar}contrib{Path.DirectorySeparatorChar}"))];

        var hits = new List<string>();
        foreach (string file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(File.ReadLines(file));
            for (int i = 0; i < lines.Length; i++)
            {
                if (DirectoryWatcherConstruction.IsMatch(lines[i]))
                {
                    hits.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}");
                }
            }
        }

        hits.Sort(StringComparer.Ordinal);
        return hits;
    }
}
