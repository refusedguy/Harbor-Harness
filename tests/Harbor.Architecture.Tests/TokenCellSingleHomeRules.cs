// TokenCellSingleHomeRules.cs — the guard for #788: a session's TOKEN CELL is
// written by more than one surface, and the set of writers is frozen here
// WITHOUT choosing which notation is canonical, because that choice is the
// owner's and ADR-010 leaves it open.
//
// THE DEFECT THIS GUARDS
// ----------------------
// #788 asked for five renderings of one magnitude to be brought under one
// notation, and was correct that there is more than one. ADR-010 then counted
// six writers and stopped — its inventory walks `src/` only, because its §2
// says "все шести — в продукте (src/)". That boundary is the hole this file
// closes. Measured on dev, the Avalonia half of the product tree contains
// FOUR more hand-written writers of the same session token spend, in a
// notation no C# scan can see:
//
//   StatusBarView.axaml:57   {Binding TokensIn,  StringFormat='↓ {0:N0}'}
//   StatusBarView.axaml:66   {Binding TokensOut, StringFormat='↑ {0:N0}'}
//   TokenUsageView.axaml:20  {Binding TotalTokensIn,  StringFormat='{}{0:N0}'}
//   TokenUsageView.axaml:28  {Binding TotalTokensOut, StringFormat='{}{0:N0}'}
//
// `N0` is a fifth notation: a thousands separator and no scale at all, so the
// same session reads `12,499` in the desktop GUI and `12.5K` in the status bar
// of the same build. Two further facts about those four lines:
//
//   * The arrows are INVERTED. Every other cell in the tree is `in↑ out↓`
//     (StatusBarText.TokensCell, StatusViewModel.Tokens, TuiViewModels.
//     Formatted, SideBarView.FormatTokensLine, SessionReadTool). The Avalonia
//     status bar binds `TokensIn` to `↓` and `TokensOut` to `↑`. Nothing
//     resolves which is right, which is why it is recorded here as a declared
//     home with a reason rather than silently "fixed".
//   * The canon is already available one line away and unused.
//     MainViewModel.TokensInText / TokensOutText forward to
//     StatusMappers.TokensToCompact — the F1/K canon — and are wired for
//     change notification, but NO XAML binds them and no C# reads them
//     (`rg TokensInText` outside MainViewModel.cs returns nothing). The
//     bindings reach past them for the raw long instead. The same bypass is
//     the one #942 already repaired for MONEY in this very view: TotalCostText
//     exists precisely so the XAML no longer spells `StringFormat='${0:F4}'`,
//     and its own doc comment records the reason — "a StringFormat in XAML is
//     invisible to MoneyCellSingleHomeRules, which scans *.cs only".
//
// So the finding that generalises is not "one more notation". It is that a
// `.cs`-only gate is blind to a whole file extension, by construction, and the
// money side already learned that lesson and fixed its half.
//
// THE RULES, IN FULL
// ------------------
// Walk the product tree (src/ + apps/, minus build output, tests/, contrib/ and
// .worktrees/ — the walk `SourceScan` already gives every other gate), over
// BOTH extensions. Comments are stripped per extension: the C# stripper for
// `.cs`, the markup stripper for `.axaml`, because the C# one eats the rest of
// any line holding a URL (`Source="http://…"`). Then:
//
//   RULE A  A token cell hand-spelled in XAML onto a raw token count: a
//           binding that hands markup its own `{0:N…}` while naming a token
//           property. The one shape a `*.cs` scan cannot see at all.
//   RULE B  A token figure scaled by a thousand and rendered through an
//           explicit numeric format specifier. The shape of every hand-scaled
//           compact figure, `F1`/`F2`/`0.#` alike.
//   RULE C  The `in↑ out↓` cell assembled by hand. This is what catches the
//           RAW convention, which has no suffix and is therefore invisible to
//           rule B by construction — the third convention ADR-010 §4.1 found.
//   RULE D  A `Format…Token…` helper hand-rolling a cell into a `Span<char>`,
//           one character at a time, which a line-level rule cannot see.
//
// WHY THIS IS A RATCHET AND NOT A GATE
// -------------------------------------
// Because the notation is NOT this file's to choose. ADR-010 §6 costs three
// variants and chooses none; `docs/adr/DECISIONS.md` records the same
// deferral. So this file does exactly what `MoneyCellSingleHomeRules` does for
// money: it freezes the NUMBER of writers, requires every survivor to name the
// reason it exists, and turns red on an ADDITIONAL writer. It never requires
// the undecided choice, so it is green on day one rather than a standing
// failure — the trade both money rules make for themselves in their headers.
//
// It is also NOT the rule ADR-010 §7 forbids. That would be a rule demanding
// "scale + suffix in one expression", which is red on every writer at once and
// encodes the choice. This one encodes only *authorship*: these twenty sites,
// these twenty markers, and nothing else.
//
// `EveryHome_StillMatchesTheLinesItNames` is what makes the deferral loud
// rather than silent: a writer that changes its notation turns RED until the
// owner records the decision, so the decision cannot be taken quietly in a
// commit that touches one line.
//
// KNOWN LIMITATIONS — measured, stated, not hidden
// -----------------------------------------------
//   * Line-level regexes, not a C# parser. A cell assembled by a FormattableString
//     or a `string.Format` spread over two lines can be missed.
//   * Rule B needs a scale AND an explicit format specifier, so it does NOT
//     catch a scale written with interpolation (`{n / 1000}K`). Those lines
//     exist and are declared homes below precisely so the omission is on the
//     record — the guard must not imply it polices what it cannot see.
//   * The two `Span<char>` helpers it declares are ONE writer seen from two
//     angles (`FormatTokensLine` wraps `FormatTokensTo`), and ADR-010 §2.3
//     records that the count is really smaller than the site count. The sites
//     are frozen because the sites are what a reader has to change.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #788: one session token spend, many surfaces, several notations.
///     This file freezes who may write it and forces each surviving writer to
///     say why — and deliberately does not choose the canon, which ADR-010
///     leaves to the owner.
/// </summary>
public sealed class TokenCellSingleHomeRules
{
    /// <summary>Which extension a rule grades. Markup needs its own comment stripper.</summary>
    private enum Markup
    {
        /// <summary>A C# source line, comments stripped by the C# stripper.</summary>
        Code,

        /// <summary>A <c>.axaml</c> line, comments stripped by the markup stripper.</summary>
        Xaml,
    }

    /// <summary>
    ///     One banned shape: which extension it lives in, the pattern that detects
    ///     it, and what to do instead — all three printed on failure so a reader
    ///     never has to open this file to learn the rule.
    /// </summary>
    private sealed record Rule(string Id, Markup Markup, string Pattern, string Instead);

    private static readonly Rule[] Rules =
    [
        new Rule(
            "TOKEN-CELL-HAND-SPELLED-IN-XAML",
            Markup.Xaml,
            // Three order-independent lookaheads: markup hands its own numeric
            // format, that format is `{0:N…}`, and the binding names a token
            // property. Order matters not, so re-indenting does not dodge it.
            @"(?=[^\n]*StringFormat=)(?=[^\n]*\{0:N\d)(?=[^\n]*\{Binding\s+[A-Za-z0-9_]*[Tt]oken)",
            "a `.cs` scan cannot see a token cell written inside a `.axaml`, so this is the "
            + "shape every C#-only gate is blind to by construction — the same hole #942 "
            + "found and closed for money in this very view. Bind a property that already "
            + "holds the spelled cell (MainViewModel.TokensInText exists for exactly this) "
            + "instead of a raw token long plus a StringFormat. See issue #788."),
        new Rule(
            "TOKEN-CELL-SCALED-AND-SUFFIXED-BY-HAND",
            Markup.Code,
            // A thousand-ish divisor AND an explicit numeric format specifier. The
            // format specifier is what makes this tight: `ms / 1000`,
            // `1000.0 / MaxHertz` and `{RequestTokens / 1000}k` all divide by a
            // thousand and none of them is a scaled token cell.
            @"(?=[^\n]*/\s*1_?000(?:_000)?(?:\.0)?\s*[,)])(?=[^\n]*\.ToString\(\s*\x22\s*(?:[Ff][0-9]|0\.#))(?=[^\n]*[KkMm]\b)",
            "the token cell has more than one writer, in more than one precision — `F1`, "
            + "`F2` and `0.#` all survive in the tree. Do not add a seventh: declare the "
            + "line below with the reason it cannot share a writer, or send it to "
            + "StatusBarText.TokensToCompact. Which notation is canonical is ADR-010 §6's "
            + "open question and this file does not answer it. See issue #788."),
        new Rule(
            "TOKEN-CELL-ASSEMBLED-BY-HAND",
            Markup.Code,
            // A token-named identifier AND an arrow glyph on the same line. The
            // token name is required, or every `↑↓ move · Enter view` hint and
            // every `Ctrl+↑/↓` keymap entry in the tree becomes a finding.
            @"(?=[^\n]*\b[A-Za-z0-9_]*[Tt]oken[A-Za-z0-9_]*\b)(?=[^\n]*[\u2191\u2193])",
            "the `in↑ out↓` cell is a second writer wherever it is spelled by hand. This "
            + "rule is also the only one that can see the RAW convention, which carries no "
            + "suffix at all and is invisible to the scale rule by construction. See "
            + "issue #788."),
        new Rule(
            "TOKEN-CELL-HAND-ROLLED-INTO-A-SPAN",
            Markup.Code,
            // `Format…Token…` rather than any token-shaped name: `TokenizeLine`
            // and `ExpectToken` in the CellForge syntax tokenizers are source
            // code lexers, not token cells, and a looser name matches both.
            @"\bFormat[A-Za-z0-9_]*[Tt]okens?[A-Za-z0-9_]*\s*\([^)]*Span<char>",
            "a span token cell is still a writer. Call the home that already writes this "
            + "shape, or declare the helper below with the reason it cannot allocate — the "
            + "sidebar repaints per frame. See issue #788."),
    ];

    /// <summary>
    ///     A line the scan may not read as a token-cell writer, and why not. The
    ///     exemption is line-precise (a file plus an exact substring) and pinned
    ///     to an expected line count, so a second writer in the same file is still
    ///     red and a home cannot outlive its reason.
    /// </summary>
    private sealed record LineHome(string RelativePath, string Marker, int ExpectedMatches, string Reason);

    /// <summary>
    ///     Every writer that survives today. Nineteen entries over twenty sites
    ///     (StatusViewModel spells its cell on two lines). Populated by the
    ///     follow-up commit so the first CI run of this file is RED and names
    ///     all twenty sites — see the commit log.
    /// </summary>
    private static readonly LineHome[] Homes = [];

    /// <summary>A file, a 1-based line number, the rule it tripped, and the line.</summary>
    private sealed record Site(string RelativePath, int Line, string RuleId, string Text);

    [Test]
    public async Task NoSurface_WritesATokenCellByHand()
    {
        List<Site> sites = [.. ScanAllSites()];

        await Assert.That(sites.Count).IsEqualTo(0)
            .Because(
                "one session token spend is written by hand at " + Describe(sites)
                + ". " + string.Join(" | ", Rules.Select(r => r.Id + " → " + r.Instead))
                + " Declared homes (a decision, not an oversight): " + DescribeHomes()
                + " See issue #788 and ADR-010.");
    }

    /// <summary>
    ///     Non-vacuity, part 1: the walk must reach a real, non-trivial file set
    ///     over BOTH extensions, containing every declared home. A scan rooted at a
    ///     typo finds zero files and then passes everything — and a scan rooted
    ///     at `*.cs` alone silently skips every XAML finding.
    /// </summary>
    [Test]
    public async Task Discovery_ReachesARealFileSet_OverBothExtensions()
    {
        var cs = SourceScan.EnumerateProductCsFiles();
        var xaml = SourceScan.EnumerateProductXamlFiles();

        await Assert.That(cs.Count).IsGreaterThan(500)
            .Because("src/ + apps/ hold far more than 500 C# files; a smaller count means the "
                     + "tree filter broke and every rule in this file is reading nothing");
        await Assert.That(xaml.Count).IsGreaterThan(20)
            .Because("the Avalonia views hold more than 20 .axaml files; a smaller count means "
                     + "RULE A — the rule that exists because a *.cs scan is blind to markup — "
                     + "is reading nothing, which is the exact failure this file was written for");

        var csPaths = cs.Select(SourceScan.Relative).ToHashSet(StringComparer.Ordinal);
        var xamlPaths = xaml.Select(SourceScan.Relative).ToHashSet(StringComparer.Ordinal);

        foreach (LineHome home in Homes)
        {
            var reachable = home.RelativePath.EndsWith(".axaml", StringComparison.Ordinal)
                ? xamlPaths
                : csPaths;

            await Assert.That(reachable.Contains(home.RelativePath)).IsTrue()
                .Because(home.RelativePath + " holds a declared home and must be inside the "
                         + "scanned set, or the rule polices nothing");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME matchers the rules run must fire on the
    ///     shape of a new writer and stay silent on the near-misses that are real
    ///     lines in the product tree today. This is what separates "the guard is
    ///     green" from "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matchers_FireOnANewTokenCell_AndStaySilentOnNearMisses()
    {
        // Each of these is a shape an additional writer arrives in. Each must be caught.
        await Assert.That(FindIn(
                ["        rows.Add($\"in    {(n / 1000.0).ToString(\"F1\", CultureInfo.InvariantCulture)}K\");"],
                Markup.Code).Count)
            .IsGreaterThan(0)
            .Because("a hand-scaled token figure in an interpolated row must be detected");
        await Assert.That(FindIn(
                ["        return $\"{(tokens / 1_000_000.0).ToString(\"F2\", CultureInfo.InvariantCulture)}M\";"],
                Markup.Code).Count)
            .IsGreaterThan(0)
            .Because("the same defect with a different precision must be detected too — F2 is "
                     + "in the tree today, so precision cannot be the thing that makes it legal");
        await Assert.That(FindIn(
                ["        private static int FormatTokensTo(long tokens, Span<char> into)"],
                Markup.Code).Count)
            .IsGreaterThan(0)
            .Because("a token cell hand-rolled into a span writes one character at a time, so "
                     + "the scale rule cannot see it");
        await Assert.That(FindIn(
                ["            return $\"{head} | {TokensIn}\u2191 {TokensOut}\u2193 | {Status}\";"],
                Markup.Code).Count)
            .IsGreaterThan(0)
            .Because("the RAW convention carries no suffix and no scale, so only the cell rule "
                     + "can see it");
        await Assert.That(FindIn(
                ["                    <TextBlock Text=\"{Binding TokensIn, StringFormat='\u2193 {0:N0}'}\""],
                Markup.Xaml).Count)
            .IsGreaterThan(0)
            .Because("the shape that motivated this file: a token cell spelled in markup, where "
                     + "no C# scan can reach it");

        // …and the near-misses, each of which is a real line in the product tree
        // today. These are the lines a naive \"ban any thousand\" rule would turn
        // the build red on.
        await Assert.That(FindIn(["        long s = ms / 1000;"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a millisecond figure is a duration, not a token cell");
        await Assert.That(FindIn(["    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1000.0 / MaxHertz);"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a frame interval divides by a thousand and is not a scaled token cell");
        await Assert.That(FindIn(["        int remaining = _retryTotalSec - (int)((Environment.TickCount64 - _retryErrorMs) / 1000);"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a retry countdown is a duration in seconds, not a token cell");
        await Assert.That(FindIn(["        rows.Add(\"\u2191\u2193 move \u00b7 Enter view \u00b7 r refresh \u00b7 Esc close\");"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a keymap hint uses the same arrows as the cell and names no token — this "
                     + "is why rule C requires a token identifier as well as a glyph");
        await Assert.That(FindIn(["        new(\"Ctrl+\u2191/\u2193\", \"grow / shrink focused panel\"),"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a key binding is not a token cell");
        await Assert.That(FindIn(["        rows.Add(\"  \u2191 more above\");"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a scroll hint is not a token cell");
        await Assert.That(FindIn(["    private static List<CodeSpan> TokenizeLine(ReadOnlySpan<char> line, string? language, ref bool inBlockComment)"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("the CellForge syntax tokenizer lexes SOURCE CODE, and rule D is scoped to "
                     + "Format…Token… for exactly this reason");
        await Assert.That(FindIn(["    private static bool ExpectToken(ReadOnlySpan<char> header, ref int i, string token)"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("a diff header lexer is not a token cell either");
        await Assert.That(FindIn(["            sb.Append(SideBarView.FormatTokens(_msgTokensIn));"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("CALLING a writer is legal from every layer; only spelling one is a second writer");
        await Assert.That(FindIn(["        Tokens = StatusBarText.TokensCell(tokensIn, tokensOut) ?? \"\";"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("delegating the cell to the projection layer is the shape this guard asks for");
        await Assert.That(FindIn(["        // rows.Add($\"{TokensIn}\u2191 {TokensOut}\u2193\");"], Markup.Code).Count)
            .IsEqualTo(0)
            .Because("comment prose is not an implementation");
        await Assert.That(FindIn(["                    <TextBlock Text=\"{Binding MessageCount, StringFormat='{}{0} msgs'}\""], Markup.Xaml).Count)
            .IsEqualTo(0)
            .Because("a message count is not a token cell, and its format is not `{0:N…}`");
        await Assert.That(FindIn(["                    <TextBlock Text=\"{Binding CostText, StringFormat='${0:F2}'}\""], Markup.Xaml).Count)
            .IsEqualTo(0)
            .Because("#942 already moved this cell into the view model; a binding to an "
                     + "already-spelled property is the fix this rule asks for, not a violation");
    }

    /// <summary>
    ///     Non-vacuity, part 3 — the home list stays honest in both directions. An
    ///     entry whose file is gone, or which no longer matches its lines, is dead
    ///     weight that would silently widen the rule the next time someone
    ///     re-adds a token cell there. A reason is mandatory, not optional.
    /// </summary>
    [Test]
    public async Task EveryHome_StillMatchesTheLinesItNames()
    {
        string root = RequireRepoRoot();

        foreach (LineHome home in Homes)
        {
            await Assert.That(home.Reason.Length > 0).IsTrue()
                .Because("home " + home.RelativePath + " must state why that line is not a "
                         + "second implementation of the token cell");
            await Assert.That(home.ExpectedMatches).IsGreaterThan(0)
                .Because("home " + home.RelativePath + " is pinned to the number of lines it "
                         + "names; a zero-width home is a file-wide mute button");
            await Assert.That(home.ExpectedMatches).IsLessThanOrEqualTo(2)
                .Because("home " + home.RelativePath + " is pinned to the number of lines it "
                         + "names; more than two is wider than any real writer in this tree");
            await Assert.That(File.Exists(Path.Combine(root, home.RelativePath))).IsTrue()
                .Because("home " + home.RelativePath + " names a file that no longer exists — drop the entry");

            int matches = 0;
            foreach (string line in ReadAllLines(Path.Combine(root, home.RelativePath)))
            {
                if (line.Contains(home.Marker, StringComparison.Ordinal))
                {
                    matches++;
                }
            }

            await Assert.That(matches).IsEqualTo(home.ExpectedMatches)
                .Because("home " + home.RelativePath + " matched " + matches + " lines but is "
                         + "declared as " + home.ExpectedMatches + " — 0 means the reason has "
                         + "expired and the entry goes too, and more means it is no longer "
                         + "line-precise. If a writer's NOTATION changed, that is the owner's "
                         + "ADR-010 decision and this red is where it gets recorded — see "
                         + "issue #788.");
        }
    }

    // ── scanning helpers ────────────────────────────────────────────────────

    private static string RequireRepoRoot() =>
        RepoPaths.RepoRoot
        ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    private static IReadOnlyList<Site> ScanAllSites()
    {
        var sites = new List<Site>();

        foreach (Rule rule in Rules)
        {
            var paths = rule.Markup == Markup.Xaml
                ? SourceScan.EnumerateProductXamlFiles()
                : SourceScan.EnumerateProductCsFiles();

            foreach (string path in paths)
            {
                string? source = SourceScan.TryReadAllText(path);
                if (source is null)
                {
                    continue;
                }

                // The markup stripper for XAML: the C# one eats the rest of any line
                // holding a URL and does not know <!-- -->, so running it over markup
                // would truncate real bindings and grade comment prose as a writer.
                string stripped = rule.Markup == Markup.Xaml
                    ? SourceScan.StripMarkupComments(source)
                    : SourceScan.StripComments(source);

                string relative = SourceScan.Relative(path);
                string[] lines = stripped.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd('\r');
                    if (IsHome(relative, line))
                    {
                        continue;
                    }

                    if (Regex.IsMatch(line, rule.Pattern, RegexOptions.CultureInvariant))
                    {
                        sites.Add(new Site(relative, i + 1, rule.Id, line));
                    }
                }
            }
        }

        return sites;
    }

    private static bool IsHome(string relativePath, string line)
    {
        foreach (LineHome home in Homes)
        {
            if (string.Equals(relativePath, home.RelativePath, StringComparison.Ordinal)
                && line.Contains(home.Marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The lines that trip at least one rule for the given extension, from
    ///     in-memory text. Shared by the real scan and the planted controls, so a
    ///     control proves the SAME matcher the rule runs — a control that called a
    ///     second, laxer helper would prove nothing about the guard. Comments are
    ///     stripped exactly as the real scan strips them, per extension.
    /// </summary>
    private static IReadOnlyList<string> FindIn(IReadOnlyList<string> lines, Markup markup)
    {
        string source = string.Join("\n", lines);
        string stripped = markup == Markup.Xaml
            ? SourceScan.StripMarkupComments(source)
            : SourceScan.StripComments(source);

        var hits = new List<string>();
        foreach (string line in stripped.Split('\n'))
        {
            foreach (Rule rule in Rules)
            {
                if (rule.Markup != markup)
                {
                    continue;
                }

                if (Regex.IsMatch(line, rule.Pattern, RegexOptions.CultureInvariant))
                {
                    hits.Add(line);
                    break;
                }
            }
        }

        return hits;
    }

    private static string Describe(IReadOnlyList<Site> sites) =>
        sites.Count == 0
            ? "(none)"
            : string.Join(
                "; ",
                sites.Select(s => s.RelativePath + ":" + s.Line + " [" + s.RuleId + "] " + s.Text.Trim()));

    private static string DescribeHomes() =>
        Homes.Length == 0
            ? "(none)"
            : string.Join("; ", Homes.Select(h => h.RelativePath + " [" + h.Reason + "]"));

    private static IReadOnlyList<string> ReadAllLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
    }
}