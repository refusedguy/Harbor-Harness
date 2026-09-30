// MoneyCellSingleHomeRules.cs — the guard for #682: a session's MONEY CELL has
// ONE shape, and only the two rendering conventions are allowed to write it.
//
// THE DEFECT THIS GUARDS
// ----------------------
// #682 counted four ways to format a token count and a second finding for the
// same drift in money: "the cost cell is formatted by more than one rule, and
// the parity guard does not see it". Both halves were true and both halves had
// moved overnight. Measured on dev at the time of writing:
//
//   StatusBarText.CostToUsd   "$" + ToString("F4")      <- the canon (#488)
//   StatusViewModel.SetUsage  "$" + ToString("0.####")   <- DIFFERENT precision
//   SideBarView.FormatCostUsd hand-rolled span twin of "0.####"
//   PanelRows.TokenRows       "$" + ToString("F4")      <- a fifth copy of the canon
//   TuiViewModels.CostText    "$" + ToString("F4")      <- a sixth copy of the canon
//
// So the money cell — the one thing a person reads to answer "what has this
// session cost me?" — had SIX writers and three precedences, and the existing
// parity guard (StatusBarFactsParityTests.CostFormatting_ExistsInExactlyOne…,
// #488) could see none of them: it counted the literal `"F4"` across a
// HARD-CODED list of four files, so a fifth copy in a file nobody added to that
// list was invisible by construction. A guard with a file list is a guard over
// the list.
//
// THE RULE, IN FULL
// ------------------
// Scan the whole product tree (src/ + apps/, minus build output, tests/,
// contrib/ and .worktrees/ — the same walk SourceScan gives every other gate).
// Comments are stripped first, so the prose in this file and in the homes' own
// XML docs cannot be graded as code. Then:
//
//   RULE 1  A money cell may not be formatted by hand. A line that (a) carries
//           a `$` glyph, (b) names a session cost, and (c) turns a value into
//           text ITSELF — `.ToString(`, `string.Format`, or an `F<n>` format
//           specifier — is a violation. All three lookaheads, so order on the
//           line does not matter and an honest rewrite cannot dodge it.
//   RULE 2  Nor may a money cell be HAND-ROLLED into a span. A helper with a
//           money-shaped name and a `Span<char>` out-parameter writes the glyph
//           and the digits itself, which rule 1 cannot see line by line.
//
// Both rules are about SHAPE, never about a format-string literal. `"F4"` is
// legitimate everywhere it appears today — two benchmark reports print an F4
// float, the E2E driver maps ConsoleKey.F4 — and a guard that banned the
// literal would go red on the first honest change to the canon and teach the
// team to route around it. What is forbidden is a SECOND WRITER, not a second
// spelling.
//
// WHY THIS IS A RATCHET AND NOT A GATE
// ------------------------------------
// There are legitimately TWO conventions and this file does NOT choose between
// them, because choosing is a rendering decision with golden-frame and
// screenshot-hash blast radius, not a refactor:
//
//   A  F4         "$0.0000" / "$12.5000"   fixed four decimals — the status
//                                             bar's cells, the projection layer.
//   B  0.####     "$0.05"    / "$12.5"     trailing zeros trimmed — the narrow
//                                             sidebar and the status widget,
//                                             where four always-on zeros eat
//                                             width that is worth more.
//
// Collapsing B into A (or A into B) is what #682's author asked for and it is
// the right end state, but it changes what a person sees and it is tracked
// separately (see the follow-up filed with this PR). Until that decision is
// taken, this file freezes the count: two conventions, three files (A's writer
// becomes ONE shared home — see TheShapeHome_Exists), and every ADDITIONAL
// writer turns the build red. The same reasoning, and the same trade, as
// TokenTrackingRatchet (#471): a gate that demands a refactor nobody has done
// is a standing build failure, and a standing build failure is how a team
// learns to reach for `--no-verify`.
//
// WHY THE SHAPE LIVES BELOW THE PRESENTATION LAYERS
// --------------------------------------------------
// Two of the copies could not call StatusBarText even in principle:
// `Harbor.Ui.Framework.Rendering` is a BCL-only leaf (its own XML doc at
// StatusViewModel.SetUsage says so), and `LayerDependencyTests.
// TuiAbstractions_ReferencesOnlyAbstractions` forbids Harbor.Terminal.Abstractions
// from referencing any Harbor assembly but Harbor.Abstractions. That is the
// same wall `ContextUsage` (#75, #651) was placed inside Harbor.
// Abstractions.Contracts to get around — the repo already keeps one canonical
// status NUMBER in the contracts assembly for exactly this reason, and the
// money cell's TEXT is now beside it. A canon that two of its five callers are
// architecturally forbidden to reach is not a canon; it is the first copy.
//
// KNOWN LIMITATION — stated, not hidden
// -------------------------------------
// This is a line-level regex, not a C# parser. A money cell assembled by a
// call to some other formatter (`x.ToString(provider)`, a FormattableString, a
// string.Format spread over two lines) can be missed. The limitation is bounded
// by construction: the only way to bring the bug back is to make a second copy
// DO work, and work happens in identifiers and literals on code lines. The
// planted controls below pin both halves of that claim against the SAME matcher
// the rule runs.

using System.Text.RegularExpressions;
using Harbor.Abstractions.Models;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #682: a session's money cell has one shape. Rule 1 forbids
///     formatting one by hand, rule 2 forbids hand-rolling one into a span,
///     and the declared homes below are the only writers allowed to exist.
/// </summary>
public sealed class MoneyCellSingleHomeRules
{
    /// <summary>
    ///     One banned shape in the product tree: the pattern that detects it, and
    ///     what to do instead — both printed in the failure message so a reader
    ///     never has to open this file to learn the rule.
    /// </summary>
    private sealed record Rule(string Id, string Pattern, string Instead);

    private static readonly Rule[] Rules =
    [
        new Rule(
            "MONEY-CELL-FORMATTED-BY-HAND",
            // Three independent lookaheads over ONE line: a dollar glyph, a
            // session-cost name, and a self-formatting call. Order-independent
            // on purpose — `"$" + Cost.ToString("F4", …)` and
            // `$"… ${cost.ToString("F4", …)}"` are the same defect spelled two
            // ways, and a rule that only matched the first would be satisfied by
            // re-indenting.
            @"(?=\$)(?=.*\b[Cc]ost[A-Za-z0-9_]*\b)(?=.*(?:\.ToString\(|string\.Format|:\s*[Ff][0-9]))",
            "the money cell has ONE shape. Call the home "
            + "(Harbor.Abstractions.Contracts.Models.UsdCell.ToUsd for convention A, "
            + "StatusViewModel.SetUsage's own convention B, or StatusBarText.CostToUsd "
            + "from the projection layer) instead of writing the glyph and the decimals here. "
            + "Two writers, two precedences, and the person reading the bar cannot tell "
            + "which one produced the number. See issue #682."),
        new Rule(
            "MONEY-CELL-HAND-ROLLED-INTO-A-SPAN",
            // A private helper with a money-shaped name and a Span<char> out
            // parameter writes the '$' and the digits itself, one statement at a
            // time, so rule 1 — which grades a line — cannot see it.
            @"static\s+[\w<>]+\s+\w*[Cc]ost\w*\s*\([^)]*Span<char>",
            "a span money cell is still a second writer. Either call the home that "
            + "already writes this shape, or declare this helper as a home below with the "
            + "reason it cannot allocate. See issue #682."),
    ];

    /// <summary>
    ///     The one place the money cell's SHAPE is implemented. Everything else
    ///     either calls it or is a declared convention-B home.
    /// </summary>
    private const string ShapeHomeRelativePath =
        "src/Harbor.Abstractions.Contracts/Models/UsdCell.cs";

    /// <summary>
    ///     The signature the shape home must still declare. If it is renamed or
    ///     removed, this guard goes RED rather than passing over a tree that no
    ///     longer has one writer.
    /// </summary>
    private const string ShapeHomeMarker = "public static string ToUsd(decimal costUsd)";

    /// <summary>
    ///     The two assemblies that are architecturally forbidden from calling
    ///     <c>StatusBarText</c> — and therefore the two the shared shape had to be
    ///     moved down for. Each reason is measured, not assumed:
    ///     <c>Harbor.Ui.Framework.Rendering</c> is a BCL-only leaf (its own XML
    ///     doc at <c>StatusViewModel.SetUsage</c> says the State edge "would be
    ///     circular"), and <c>LayerDependencyTests.</c>
    ///     <c>TuiAbstractions_ReferencesOnlyAbstractions</c> forbids
    ///     <c>Harbor.Terminal.Abstractions</c> from referencing any Harbor
    ///     assembly but <c>Harbor.Abstractions</c>.
    /// </summary>
    private static readonly string[] BlockedFromTheCanon =
        ["Harbor.Terminal.Abstractions", "Harbor.Ui.Framework.Rendering"];

    /// <summary>
    ///     A line the scan may not read as a money cell, and why not. The
    ///     exemption is line-precise (a file + an exact substring), never
    ///     file-precise: a second line in that file that really does format a
    ///     session cost is still red, and
    ///     <see cref="EveryHome_StillMatchesExactlyOneLine" /> keeps an entry from
    ///     outliving its reason.
    /// </summary>
    private sealed record LineHome(string RelativePath, string Marker, string Reason);

    private static readonly LineHome[] Homes =
    [
        new LineHome(
            "src/Harbor.Abstractions.Contracts/Models/UsdCell.cs",
            "(costUsd < 0 ? 0m : costUsd).ToString(\"F4\"",
            "THE SHAPE HOME — convention A, the one writer every other surface is sent to. "
            + "It is a declared home rather than a tolerated absence because a rule that "
            + "cannot name where the rule lives has to be re-tuned every time the canon "
            + "moves; MoneyCellSingleHomeRules names it and asserts it still exists."),
        new LineHome(
            "src/Harbor.Ui.Framework.Rendering/Widgets/StatusViewModel.cs",
            "\"$\" + costUsd.Value.ToString(",
            "CONVENTION B (\"0.####\", trailing zeros trimmed). The narrow sidebar and the "
            + "status widget render the cell here; collapsing B into A's \"F4\" is a "
            + "rendering change with golden-frame and screenshot-hash blast radius, so it "
            + "is tracked separately and this file freezes the count instead of choosing. "
            + "The XML doc above this line records the other reason it cannot share code "
            + "with convention A: Harbor.Ui.Framework.Rendering is a BCL-only leaf."),
        new LineHome(
            "src/Harbor.Tui.CellForge/Chat/Widgets/SideBarView.cs",
            "private static int FormatCostUsd(double cost, Span<char> into)",
            "The allocation-free span twin of convention B, written once because the "
            + "sidebar repaints per frame and a string per frame is the cost this guard "
            + "would introduce. Its own doc names \"0.####\" as the shape it twins — "
            + "StatusViewModel above — so it is the SAME convention, not a third one."),
        new LineHome(
            "src/Harbor.Tools.Builtin/Tools/Session/SessionReadTool.cs",
            "sb.Append('$').Append(session.Metadata.Cost.ToString(",
            "Not a money CELL at all: it is the plain-text report the session_read tool "
            + "hands back to the MODEL, four fixed decimals by contract so a diff of two "
            + "reports lines up. A cell and a tool report are different outputs with "
            + "different readers, and neither reads the other."),
    ];

    /// <summary>A file, a 1-based line number, the rule it tripped, and the line.</summary>
    private sealed record Site(string RelativePath, int Line, string RuleId, string Text);

    [Test]
    public async Task NoSurface_FormatsAMoneyCellByHand()
    {
        List<Site> sites = [.. ScanAllSites()];

        await Assert.That(sites.Count).IsEqualTo(0)
            .Because(
                "a session's money cell has a second writer at " + Describe(sites)
                + ". " + string.Join(" | ", Rules.Select(r => r.Id + " → " + r.Instead))
                + " Declared homes (a decision, not an oversight): " + DescribeHomes()
                + " See issue #682.");
    }

    /// <summary>
    ///     Non-vacuity, part 1: the walk must reach a real, non-trivial file set
    ///     that contains every declared home. A scan rooted at a typo finds zero
    ///     files and then "passes" everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_HoldingEveryDeclaredHome()
    {
        var files = SourceScan.EnumerateProductCsFiles();

        await Assert.That(files.Count).IsGreaterThan(500)
            .Because(
                "src/ + apps/ hold far more than 500 C# files; a smaller count means the "
                + "tree filter broke and every rule in this file is reading nothing.");

        foreach (LineHome home in Homes)
        {
            await Assert.That(files.Any(f => SourceScan.Relative(f) == home.RelativePath))
                .IsTrue()
                .Because(home.RelativePath + " holds a declared home and must be inside the "
                         + "scanned set, or the rule polices nothing.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME matcher the rule runs must fire on a
    ///     planted copy of every banned shape and stay silent on the near-misses
    ///     that are real lines in the product tree today. This is what separates
    ///     "the guard is green" from "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedMoneyCells_AndStaysSilentOnNearMisses()
    {
        // The shapes a second writer comes in. Each must be caught.
        await Assert.That(FindIn(["        ? \"$\" + Cost.ToString(\"F4\", CultureInfo.InvariantCulture)"]).Count)
            .IsGreaterThan(0)
            .Because("a concatenated glyph + self-formatted cost must be detected, or the rule is blind");
        await Assert.That(FindIn(["        rows.Add($\"total {n}  ${cost.ToString(\"F4\", CultureInfo.InvariantCulture)}\");"]).Count)
            .IsGreaterThan(0)
            .Because("the same defect inside an interpolated hole must be detected too");
        await Assert.That(FindIn(["        private static int FormatCostUsd(double cost, Span<char> into)"]).Count)
            .IsGreaterThan(0)
            .Because("a hand-rolled span money cell writes one glyph at a time and rule 1 "
                     + "cannot see it; rule 2 must");

        // …and the near-misses, each of which is a real line that exists in the
        // product tree today. These are the lines a naive \"ban every $\" rule
        // would turn the build red on.
        await Assert.That(FindIn(["        return $\"${InputPerMillion:F2} in / ${OutputPerMillion:F2} out per 1M\";"]).Count)
            .IsEqualTo(0)
            .Because("a per-million RATE row is a catalogue price, not a session cost cell — "
                     + "a different output with a different reader");
        await Assert.That(FindIn(["        Cost = StatusBarText.CostCell(cost.CostUsd, cost.IsCostUnpriced);"]).Count)
            .IsEqualTo(0)
            .Because("CALLING the home is the fix this guard asks for, not a violation of it");
        await Assert.That(FindIn(["        CostText => IsCostKnown ? UsdCell.ToUsd(Cost) : UsdCell.Unpriced;"]).Count)
            .IsEqualTo(0)
            .Because("delegating to the shared shape is legal from every layer");
        await Assert.That(FindIn(["        public string CostText => StatusMappers.CostToUsd(CostUsd);"]).Count)
            .IsEqualTo(0)
            .Because("an adapter that forwards a decimal writes no glyph and no decimals");
        await Assert.That(FindIn(["        // Cost.ToString(\"F4\") is prose about the rule, not a writer."]).Count)
            .IsEqualTo(0)
            .Because("comment prose is not a second implementation");
        await Assert.That(FindIn(["        /// <see cref=\"StatusBarText.CostToUsd\"/> renders \"$0.0123\"."]).Count)
            .IsEqualTo(0)
            .Because("doc-comment prose is not a second implementation");
    }

    /// <summary>
    ///     Non-vacuity, part 3 — the rule's other half. Every surface may be
    ///     forbidden from writing a money cell IF one shape is still written
    ///     exactly once, below the presentation layers. If the home is ever
    ///     renamed or removed this goes RED, so \"nothing formats it\" can never be
    ///     satisfied by nobody formatting it.
    /// </summary>
    [Test]
    public async Task TheShapeHome_Exists_AndIsBelowThePresentationLayers()
    {
        string root = RequireRepoRoot();
        string home = Path.Combine(root, ShapeHomeRelativePath);

        string? text = SourceScan.TryReadAllText(home);
        await Assert.That(text).IsNotNull()
            .Because(
                ShapeHomeRelativePath + " is the ONE implementation of the money cell's shape. "
                + "Harbor.Ui.Framework.Rendering is a BCL-only leaf and "
                + "LayerDependencyTests.TuiAbstractions_ReferencesOnlyAbstractions forbids "
                + "Harbor.Terminal.Abstractions from referencing any Harbor assembly but "
                + "Harbor.Abstractions — so a canon those two cannot reach is not a canon. "
                + "ContextUsage (#75, #651) already lives in Harbor.Abstractions.Contracts for "
                + "the same reason. See issue #682.");
        await Assert.That(text!).Contains(ShapeHomeMarker)
            .Because(ShapeHomeMarkerMessage());

        // The home must be REACHABLE from the two assemblies that cannot call
        // StatusBarText at all. That is the whole reason the shape lives in the
        // contracts layer rather than beside the canon it was extracted from:
        // Harbor.Ui.Framework.Rendering is a BCL-only leaf, and
        // TuiAbstractions_ReferencesOnlyAbstractions forbids
        // Harbor.Terminal.Abstractions from naming any Harbor assembly but
        // Harbor.Abstractions. A home those two cannot reach is the first copy
        // wearing the costume of a canon.
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();
        string contracts = typeof(ContextUsage).Assembly.GetName().Name ?? string.Empty;
        foreach (string blocked in BlockedFromTheCanon)
        {
            await Assert.That(loaded.ContainsKey(blocked)).IsTrue()
                .Because(blocked + " is one of the two assemblies that cannot call "
                         + "StatusBarText; if it is not loaded, this assertion is vacuous");

            await Assert.That(loaded[blocked].GetReferencedAssemblies().Any(r => r.Name == contracts))
                .IsTrue()
                .Because(blocked + " must reach " + contracts + " to read the money cell's shape — "
                         + "the reason ContextUsage (#75, #651) was put there. See issue #682.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 4: the home list stays honest in both directions. An
    ///     entry whose file is gone, or which no longer matches a line, is dead
    ///     weight that would silently widen the rule the next time someone
    ///     re-adds a money cell there. A reason is mandatory, not optional.
    /// </summary>
    [Test]
    public async Task EveryHome_StillMatchesExactlyOneLine()
    {
        string root = RequireRepoRoot();

        foreach (LineHome home in Homes)
        {
            await Assert.That(home.Reason.Length > 0).IsTrue()
                .Because("home " + home.RelativePath + " must state why that line is not a "
                         + "second implementation");
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

            await Assert.That(matches).IsEqualTo(1)
                .Because("home " + home.RelativePath + " matched " + matches + " lines — it must "
                         + "stay one line wide, or it is a file-wide mute button; and 0 means "
                         + "the reason has expired, so the entry goes too");
        }
    }

    // ── scanning helpers ────────────────────────────────────────────────────

    private static string RequireRepoRoot() =>
        RepoPaths.RepoRoot
        ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    private static string ShapeHomeMarkerMessage() =>
        ShapeHomeRelativePath + " must still declare `" + ShapeHomeMarker
        + "` — the single writer this guard sends every other surface to. If the shape moved, "
        + "update this guard and the paths that call it; if it was deleted, the two "
        + "conventions below have no shared shape to agree on and this rule is protecting "
        + "nothing. See issue #682.";

    private static IReadOnlyList<Site> ScanAllSites()
    {
        var sites = new List<Site>();
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            sites.AddRange(FindInSource(path));
        }

        return sites;
    }

    private static IReadOnlyList<Site> FindInSource(string path)
    {
        string? source = SourceScan.TryReadAllText(path);
        if (source is null)
        {
            return [];
        }

        string relative = SourceScan.Relative(path);
        var sites = new List<Site>();
        string[] lines = SourceScan.StripComments(source).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (IsHome(relative, line))
            {
                continue;
            }

            foreach (Rule rule in Rules)
            {
                if (Regex.IsMatch(line, rule.Pattern, RegexOptions.CultureInvariant))
                {
                    sites.Add(new Site(relative, i + 1, rule.Id, line));
                    break;
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
    ///     The lines that trip at least one rule, from in-memory text. Shared by
    ///     the real scan and the planted controls, so the control proves the SAME
    ///     matcher the rule runs — a control that called a second, laxer helper
    ///     would prove nothing about the guard. Comments are stripped exactly as
    ///     the real scan strips them, otherwise a control claiming "prose is not
    ///     an implementation" would pass for the wrong reason.
    /// </summary>
    private static IReadOnlyList<string> FindIn(IReadOnlyList<string> lines)
    {
        var hits = new List<string>();
        foreach (string line in SourceScan.StripComments(string.Join("\n", lines)).Split('\n'))
        {
            foreach (Rule rule in Rules)
            {
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