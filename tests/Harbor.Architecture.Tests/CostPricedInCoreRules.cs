// CostPricedInCoreRules.cs — the guard for issue #653: "the cost of a session
// is computed by the headless core; the presentation layer only displays it".
//
// WHY THIS FILE EXISTS
// --------------------
// The status bar showed `$0.1878` for a session on `…step-3.7-flash:free` — a
// FREE model. The number was not a price: two TEA reducers had each re-derived
// the cost formula from a hand-written pair of constants ($3/M in, $15/M out),
// identical in both files and applied to every provider and every model. The
// core, meanwhile, published `SessionMetadata.Cost = 0` for every session, so
// the two numbers never agreed with each other, let alone with the bill. The
// correct implementation (`Pricing.CalculateCost`, cache-aware) sat one file
// away and was used by exactly one caller.
//
// A reference matrix cannot see this. Both offenders are perfectly layered: the
// price constants are plain decimals and the arithmetic is plain `decimal` — no
// forbidden BCL type, no forbidden edge. What is forbidden is a DECISION taken
// in the wrong layer, so the rule is a source rule, for the same reason and in
// the same shape as DefaultModelSingleSourceTests (#599) and MaybeAbsenceTests.
//
// THE RULE
// --------
// The presentation layer may not know what a price is. Inside the presentation
// trees (every `src/Harbor.Ui.Framework*` and `src/Harbor.Tui.*` project, plus
// `apps/`) none of the following may appear on a code line:
//
//   * `Pricing`                      — the per-model rate table itself;
//   * `CalculateCost`                — the one function that turns usage into money;
//   * `…PerMillion` / `…EstimateCost`— the naming fingerprint of a hand-rolled rate table;
//   * `1_000_000m`                   — the per-million DECIMAL divisor, i.e. the
//                                      formula `tokens / 1e6 * rate` written out.
//
// The core is untouched by this: `Pricing.CalculateCost` in
// `src/Harbor.Abstractions.Contracts/Models/Session.cs` is the single home for
// the divisor, and `SessionMetadata.AddUsage` is the single fold that calls it.
// The guard asserts that home still exists (see `TheSingleHome_StillExists`),
// so the rule cannot pass by deleting the price altogether.
//
// WHY `1_000_000m` AND NOT `1_000_000`
// -----------------------------------
// The bare `1_000_000` is a TOKEN formatter ("1.2M" in StatusBarText,
// PanelRows, SideBarView, StatusViewModel) and is legitimate. The `m` suffix
// makes the literal a `decimal` — and a `decimal` divided into a token count and
// multiplied by a rate is the price formula, spelled. In this repository the
// suffixed spelling occurs in exactly the lines of `CalculateCost` plus the two
// hand-rolled copies #653 deletes, which is what makes it a usable fingerprint
// rather than a guess.
//
// QUOTES ARE EXCLUDED FROM THE NAME RULES — why the lookbehind
// -----------------------------------------------------------
// `apps/Harbor.App.Cli/Commands/DemoCommand.cs` embeds a provider-config JSON
// sample containing `"inputPerMillion"`. The rate-table rule therefore matches a
// C# identifier that STARTS at an identifier boundary not preceded by a letter,
// a digit, an underscore or a quote: `InputPricePerMillion` and `p.InputPerMillion`
// match, the JSON key inside a string literal does not. The rules match CODE,
// not string payloads — a demo that prints a price table is not a price
// computation.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan skips lines whose first non-whitespace characters are `//`, `/*` or
// `*` (a price quoted in prose is not a second implementation), and it is a
// line-level regex rather than a C# parser: a price written inside a multi-line
// block comment, or on a line that opens inside a verbatim string, can be
// missed. The limitation is bounded by construction — the only way to bring the
// bug back is to make a second copy DO work, and work happens in identifiers and
// literals on code lines.
//
// The rate-name rule is deliberately LOOSE in the other direction: a rate name
// quoted inside a string literal that has a space in front of it
// (`"see PerMillion docs"`) is a false positive. It fails the build with the
// file and line named, and the fix is to reword the literal — the cheaper trade
// for a guard whose job is to notice a second rate table, not to parse C#.
//
// ONE OUT-OF-SCOPE PROJECT, BY PATH
// --------------------------------
// `src/Harbor.Terminal.Abstractions` (ViewModels/TuiViewModels.cs:117) calls
// `_pricing.CalculateCost(sf.Usage)` and is NOT in the scanned set: the trees are
// `Harbor.Ui.Framework*` and `Harbor.Tui.*`, and that project is neither. That
// call is CORRECT — it prices with the model's real, cache-aware rate table
// rather than with constants — and it is the pre-existing path this issue leaves
// alone. It is called out here so the exclusion reads as a decision rather than
// an oversight.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #653: pricing is a core decision. The presentation layer displays
///     the number the core published; it may not re-derive it.
/// </summary>
public sealed class CostPricedInCoreRules
{
    /// <summary>
    ///     One banned shape in the presentation layer: an id, the pattern that
    ///     detects it, and what to do instead — all three printed in the failure
    ///     message so a reader never has to open this file to learn the rule.
    /// </summary>
    private sealed record Rule(string Id, string Pattern, string Instead);

    private static readonly Rule[] Rules =
    [
        new Rule(
            "PRESENTATION-MUST-NOT-KNOW-PRICING",
            @"\bPricing\b",
            "the per-model rate table is a core decision: the model the core called is the "
            + "model whose rates apply, and a renderer never resolves one. Display "
            + "SessionMetadata.Cost from the SessionStatsEvent the core publishes."),
        new Rule(
            "PRESENTATION-MUST-NOT-CALL-CALCULATECOST",
            @"\bCalculateCost\b",
            "Pricing.CalculateCost is the ONE implementation of the price formula and it lives "
            + "in Harbor.Abstractions.Contracts. Call it from the core (SessionMetadata.AddUsage "
            + "already does) and display the total — a second call site is a second rate source."),
        new Rule(
            "PRESENTATION-MUST-NOT-NAME-A-RATE-TABLE",
            @"(?<![A-Za-z0-9_""])(?:[A-Za-z_][A-Za-z0-9_]*)?(?:PerMillion|EstimateCost)",
            "'…PerMillion' / '…EstimateCost' is the naming fingerprint of a hand-rolled rate "
            + "table. The rates belong to ModelInfo.Pricing, declared by the provider that "
            + "serves the model."),
        new Rule(
            "PRESENTATION-MUST-NOT-DO-PER-MILLION-ARITHMETIC",
            @"1_000_000m",
            "`tokens / 1_000_000m * rate` IS the price formula, written out. A renderer may "
            + "divide token COUNTS by 1_000_000 to abbreviate them ('1.2M') — the `m` suffix is "
            + "what makes this literal a decimal, and a decimal divided into tokens and "
            + "multiplied by a rate is money."),
    ];

    /// <summary>
    ///     The one file allowed to hold the price formula. Named, not globbed: the
    ///     rule is about this specific declaration.
    /// </summary>
    private const string CorePriceHomeRelativePath =
        "src/Harbor.Abstractions.Contracts/Models/Session.cs";

    /// <summary>
    ///     The two projects the defect was found in. Named explicitly so the
    ///     non-vacuity test can assert the scan really reaches them.
    /// </summary>
    private static readonly string[] OffenderFiles =
    [
        "src/Harbor.Ui.Framework.State/State/ChatAppReducer.cs",
        "src/Harbor.Ui.Framework.Reducers/AppReducer.cs",
    ];

    /// <summary>
    ///     Project directory prefixes that make up the presentation layer, matched
    ///     against the directory name directly under <c>src/</c>. <c>apps/</c> is
    ///     scanned whole (it is the composition root of the same layer).
    /// </summary>
    private static readonly string[] PresentationProjectPrefixes =
        ["Harbor.Ui.Framework", "Harbor.Tui."];

    /// <summary>A file, a 1-based line number, the rule it tripped and the line.</summary>
    private sealed record Site(string RelativePath, int Line, string RuleId, string Text);

    [Test]
    public async Task Presentation_DoesNotPriceTheCost()
    {
        string root = RequireRepoRoot();
        IReadOnlyList<string> files = EnumeratePresentationFiles(root);
        List<Site> sites = [.. files.SelectMany(f => FindSites(root, f))];

        await Assert.That(sites.Count).IsEqualTo(0)
            .Because(
                "the presentation layer is re-deriving the price of a session at "
                + Describe(sites)
                + ". " + string.Join(" | ", Rules.Select(r => r.Id + " → " + r.Instead))
                + " See issue #653.");

        // The rule is about the presentation layer, so the core's own copy must
        // not be able to satisfy it by accident: a hit on the file that
        // legitimately owns the formula would mean the trees are wrong.
        await Assert.That(files.Contains(CorePriceHomeRelativePath)).IsFalse()
            .Because(CorePriceHomeRelativePath + " is the CORE price home — it must not be inside "
                     + "the scanned presentation trees, or this rule polices the wrong layer.");
    }

    /// <summary>
    ///     Non-vacuity, part 1: the discovery step must find a real, non-trivial
    ///     file set that contains both projects the defect lived in. A scan rooted
    ///     at a typo finds zero files and then "passes" everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_ContainingBothOffenderProjects()
    {
        IReadOnlyList<string> files = EnumeratePresentationFiles(RequireRepoRoot());

        await Assert.That(files.Count).IsGreaterThan(100)
            .Because("the Ui.Framework + Tui projects plus apps/ hold far more than 100 C# files; "
                     + "a smaller count means the tree filter broke.");
        foreach (string offender in OffenderFiles)
        {
            await Assert.That(files.Contains(offender)).IsTrue()
                .Because(offender + " must be inside the scanned set, or the rule polices nothing.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME matcher the rule runs must fire on a
    ///     planted copy of every banned shape, and stay silent on the legitimate
    ///     near-misses that are real lines in the presentation layer today. This
    ///     is what separates "the guard is green" from "the guard is looking at
    ///     nothing".
    /// </summary>
    [Test]
    public async Task Matcher_FiresOnPlantedPricing_AndStaysSilentOnTokenFormatting()
    {
        await Assert.That(FindIn(["    cost += pricing.CalculateCost(usage);"]).Count)
            .IsGreaterThan(0)
            .Because("a planted CalculateCost call must be detected, or the rule is blind");
        await Assert.That(FindIn(["    private const decimal InputPricePerMillion = 3m;"]).Count)
            .IsGreaterThan(0)
            .Because("a planted rate constant must be detected, or the rule is blind");
        await Assert.That(FindIn(["        InputPerMillion = p.InputPerMillion;"]).Count)
            .IsGreaterThan(0)
            .Because("a planted rate read through a member access must be detected, or the rule is blind");
        await Assert.That(FindIn(["    private static decimal EstimateCost(int i, int o) => 0m;"]).Count)
            .IsGreaterThan(0)
            .Because("a planted EstimateCost must be detected, or the rule is blind");
        await Assert.That(FindIn(["    return i / 1_000_000m * 3m;"]).Count)
            .IsGreaterThan(0)
            .Because("a planted per-million decimal divisor must be detected, or the rule is blind");

        // …and the near-misses, each of which is a real line that exists in the
        // presentation layer today.
        await Assert.That(FindIn(["    if (tokens < 1_000_000) return \"1M\";"]).Count)
            .IsEqualTo(0)
            .Because("an INT token-count threshold is abbreviation, not pricing");
        await Assert.That(FindIn(["    return (v / 1_000_000.0).ToString(\"0.#\") + \"M\";"]).Count)
            .IsEqualTo(0)
            .Because("a DOUBLE token-count divisor is abbreviation, not pricing");
        await Assert.That(FindIn(["              \"pricing\": { \"inputPerMillion\": 0, \"outputPerMillion\": 0 },"]).Count)
            .IsEqualTo(0)
            .Because("a JSON config sample inside a string literal is a payload, not a price computation");
        await Assert.That(FindIn(["    // 61.6k tokens cost 1_000_000m per million under Pricing"]).Count)
            .IsEqualTo(0)
            .Because("comment prose is not a second implementation");
        await Assert.That(FindIn(["    /// <see cref=\"Pricing\"/> is a core concept."]).Count)
            .IsEqualTo(0)
            .Because("doc-comment prose is not a second implementation");
    }

    /// <summary>
    ///     Non-vacuity, part 3 — the rule's other half. The presentation layer
    ///     may only be forbidden from pricing IF the core still has exactly one
    ///     place that does. If the formula is ever re-implemented anywhere else
    ///     in the product tree — including in a project added after this guard
    ///     was written — this goes RED, so "the UI computes nothing" can never be
    ///     satisfied by the UI computing the same thing twice.
    /// </summary>
    [Test]
    public async Task TheSingleHome_StillExists()
    {
        string root = RequireRepoRoot();
        string home = ReadAllText(Path.Combine(root, CorePriceHomeRelativePath));

        await Assert.That(home.Contains("public decimal CalculateCost(Usage usage)", StringComparison.Ordinal))
            .IsTrue()
            .Because(CorePriceHomeRelativePath + " must still own the one price formula; if it moved, "
                     + "update this guard rather than letting a second one grow next to it.");

        var elsewhere = new List<Site>();
        foreach (string relative in EnumerateProductFiles(root))
        {
            if (string.Equals(relative, CorePriceHomeRelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((int line, string text) in ReadAllLines(Path.Combine(root, relative)))
            {
                if (IsCommentOnly(text) || !text.Contains("1_000_000m", StringComparison.Ordinal))
                {
                    continue;
                }

                elsewhere.Add(new Site(relative, line, "PER-MILLION-DIVISOR-ELSEWHERE", text));
            }
        }

        await Assert.That(elsewhere.Count).IsEqualTo(0)
            .Because(
                "the per-million price divisor is re-implemented outside "
                + CorePriceHomeRelativePath + " at " + Describe(elsewhere)
                + ". One formula, one home: Pricing.CalculateCost. See issue #653.");
    }

    // ── scanning helpers ────────────────────────────────────────────────────

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");
    }

    /// <summary>Every C# file of the presentation layer, sorted for stable messages.</summary>
    private static IReadOnlyList<string> EnumeratePresentationFiles(string root)
    {
        var files = new List<string>();

        string src = Path.Combine(root, "src");
        if (Directory.Exists(src))
        {
            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir) ?? string.Empty;
                bool isPresentation = PresentationProjectPrefixes.Any(
                    p => name.StartsWith(p, StringComparison.Ordinal));
                if (!isPresentation)
                {
                    continue;
                }

                files.AddRange(EnumerateCsFilesUnder(root, dir));
            }
        }

        // apps/ is the composition root of the same layer: a cost invented in a
        // command handler reaches the user's screen exactly as one invented in a
        // reducer does.
        string apps = Path.Combine(root, "apps");
        if (Directory.Exists(apps))
        {
            files.AddRange(EnumerateCsFilesUnder(root, apps));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Every C# file of the product tree (<c>src/</c> + <c>apps/</c>).</summary>
    private static IReadOnlyList<string> EnumerateProductFiles(string root)
    {
        var files = new List<string>();
        foreach (string tree in new[] { "src", "apps" })
        {
            string dir = Path.Combine(root, tree);
            if (Directory.Exists(dir))
            {
                files.AddRange(EnumerateCsFilesUnder(root, dir));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static IReadOnlyList<string> EnumerateCsFilesUnder(string root, string dir)
    {
        // No worktree filter: the walk starts at <root>/src and <root>/apps, so a
        // nested checkout under <root>/.worktrees is out of reach anyway — and
        // filtering on it would be actively wrong for a checkout that itself lives
        // in .worktrees, which would filter itself out and then find nothing.
        return
        [
            .. Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !IsBuildOutput(p))
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
        ];
    }

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal)
               || normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    private static IReadOnlyList<Site> FindSites(string root, string relativePath)
    {
        var sites = new List<Site>();
        foreach ((int line, string text) in ReadAllLines(Path.Combine(root, relativePath)))
        {
            if (IsCommentOnly(text))
            {
                continue;
            }

            foreach (Rule rule in Rules)
            {
                if (Regex.IsMatch(text, rule.Pattern, RegexOptions.CultureInvariant))
                {
                    sites.Add(new Site(relativePath, line, rule.Id, text));
                    break;
                }
            }
        }

        return sites;
    }

    /// <summary>
    ///     The text of every non-comment line that trips at least one rule.
    ///     Shared by the real scan and the planted controls, so the control proves
    ///     the SAME matcher the rule runs.
    /// </summary>
    private static IReadOnlyList<string> FindIn(IReadOnlyList<string> lines)
    {
        var hits = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsCommentOnly(lines[i]))
            {
                continue;
            }

            if (Rules.Any(r => Regex.IsMatch(lines[i], r.Pattern, RegexOptions.CultureInvariant)))
            {
                hits.Add(lines[i]);
            }
        }

        return hits;
    }

    private static bool IsCommentOnly(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith('*');
    }

    private static string Describe(IReadOnlyList<Site> sites)
        => sites.Count == 0
            ? "(none)"
            : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.Line + " [" + s.RuleId + "]"));

    /// <summary>1-based line number + text, or an empty list for an unreadable file.</summary>
    private static IReadOnlyList<(int Line, string Text)> ReadAllLines(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }

        var result = new (int, string)[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            result[i] = (i + 1, lines[i]);
        }

        return result;
    }

    private static string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
