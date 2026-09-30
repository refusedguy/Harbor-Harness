// ModelRateLabelRules.cs — the guard for the pricing half of issue #686.
//
// WHY THIS FILE EXISTS
// --------------------
// Issue #686 listed "FormatPricing ×2" among the small UI duplicates. Measuring
// it on dev showed the premise had already been half-consumed by #653, and what
// survived is worse than a duplicate: the two model pickers answer the SAME
// question about the SAME model with two different answers.
//
//   ProviderModelPickerViewModel.cs:377 — a private FormatPricing helper, which
//     returned the literal "pricing unknown" when both rates were zero, and
//     otherwise spelled "$X in / $Y out per 1M" with an F2 format on each rate.
//   ProviderBrowserViewModel.cs:197 — a PricingLabel property that spelled the same
//     sentence, and had no zero case at all.
//
// Both are LIVE, and #686's own caveat ("if one copy is in a test or a dead
// branch there is nothing to converge") is the first thing this guard has to
// disprove — a duplicate nobody can reach is not a defect. Reachability was
// traced to the XAML, in the same app:
//
//   apps/Harbor.App.Avalonia/Views/Controls/ProviderModelPicker.axaml:121
//       <TextBlock Text="{Binding PricingText}" …>            (PickerModelViewModel)
//   apps/Harbor.App.Avalonia/Views/ProviderBrowserView.axaml:84
//       <TextBlock Text="{Binding PricingLabel}" …>           (ModelRowViewModel)
//
// So a user who opens the picker dropdown and the provider browser sees the same
// model priced two ways. The two copies had already drifted in exactly one
// place — the unknown-price case:
//
//   picker    if (inputPerMillion == 0m && outputPerMillion == 0m) → "pricing unknown"
//   browser   no guard at all                                     → "$0.00 in / $0.00 out per 1M"
//
// The browser therefore prints a FALSE price claim: it says a model costs
// nothing per million when in fact the catalogue entry carries no rates at all.
// The core already rules on this and says so in `Pricing.IsUnknown`'s own
// remark: "a '$0.0000' is a claim, and for the second case it is a false one."
// The picker's second-guess is the same defect wearing a different hat — it
// re-derives from the two numbers what the core publishes as a bit, and it
// re-derives it WRONG, because `IsUnknown` also considers the two cache rates
// and the picker's `if` cannot see them.
//
// WHY #653's GUARD DID NOT CATCH IT
// ---------------------------------
// `CostPricedInCoreRules` (same directory) scans `src/Harbor.Ui.Framework*`,
// `src/Harbor.Tui.*` and `apps/`. Both offenders live in
// `src/Harbor.Desktop.Abstractions`, which that scan set does not include. This
// rule therefore does NOT widen that set — widening it would flip on every
// legitimate reference the desktop abstractions make to `ModelInfo` — and
// instead polices the SHAPE that is actually wrong: a rate value spelled inside
// a user-facing string, and a rate compared to zero by hand.
//
// THE RULES
// ---------
// 1. SINGLE SURFACE. A rate-shaped identifier (`…PerMillion`) may appear
//    INSIDE A STRING LITERAL on exactly one product line, and that line must be
//    the one shared home. A presentation record may still CARRY rates
//    (`decimal InputPerMillion,` is a parameter, not a claim to a user) and a
//    call site may still PASS them (`m.Pricing.InputPerMillion,` is a hand-off);
//    what may not be repeated is the sentence that tells a user what a rate
//    means.
// 2. NO HAND-ROLLED ZERO GUESS. A product line may not compare a rate-shaped
//    identifier to `0m`, outside the core's own `Pricing` declaration. "Does
//    this model publish a price?" is `Pricing.IsUnknown`'s answer, published as
//    a bit precisely so a renderer cannot re-derive it.
//
// The rules match SHAPE, never a member name. Renaming `FormatPricing`,
// `PricingLabel` or `PricingText` to anything does not silence either rule, and
// neither does rewording the label ("out per 1M" → "out / 1M tokens"): rule 1
// keys on the rate identifier being interpolated into a string, rule 2 on it
// being compared to a zero literal.
//
// THE LOOKBEHIND IS LOAD-BEARING — the JSON that proves it
// --------------------------------------------------------
// `apps/Harbor.App.Cli/Commands/DemoCommand.cs` embeds a provider-config sample
// whose `"pricing"` object contains the JSON keys `"inputPerMillion"` and
// `"outputPerMillion"`. The rules therefore require the rate name to START at
// an identifier boundary not preceded by a letter, a digit, an underscore or a
// QUOTE: `${InputPerMillion:F2}` matches, the JSON key inside a string literal
// does not. The rules match CODE, not string payloads — a demo that prints a
// provider config is not a second rate label. The lookbehind has to be applied
// to the whole quoted span (quotes included) rather than to the extracted
// literal's contents, or the `"` that disqualifies the JSON key is stripped off
// before the lookbehind ever sees it. This is the same lookbehind, for the same
// reason, as the one in `CostPricedInCoreRules`.
//
// NON-VACUITY
// -----------
// A source scan that matches nothing is indistinguishable from a source scan
// that is broken, and a broken guard is worse than no guard because it is
// believed. Two tests below close that. The discovery step must find a real,
// non-trivial file set containing both offenders; and the SAME two matchers the
// rule runs on must fire on planted copies of both banned shapes while staying
// silent on the three spellings that are legitimate — passing a rate, carrying a
// rate, and the JSON keys of a provider-config sample. The self-test drives the
// same functions on the same comment-stripped input the scan feeds them, so a
// change to the production path cannot leave it asserting on something nobody
// runs. A third test keeps the one exemption honest: it must still match a line
// that still earns it, so the hole cannot outlive its reason.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// Like its sibling `CostPricedInCoreRules`, this is a line-level regex, not a
// C# parser: a second label written across a line break, or inside a multi-line
// block comment, can be missed. The limitation is bounded by construction — the
// only way to bring the bug back is to make a second copy DO work, and work
// lives in identifiers on code lines. The rules are deliberately LOOSE in one
// direction: a rate name quoted inside a string literal with a space in front of
// it (`"see PerMillion docs"`) is a false positive. It fails the build with the
// file and line named, and the fix is to reword the literal — the cheaper trade
// for a guard whose job is to notice a second rate label, not to parse C#.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #686 (pricing half): the per-1M rate label is built in one place, and
///     the unknown-price question is answered by the core
///     (<c>Pricing.IsUnknown</c>) rather than re-derived from the rate numbers.
/// </summary>
public sealed class ModelRateLabelRules
{
    /// <summary>
    ///     The one file allowed to spell a rate inside a user-facing string: the
    ///     shared wording helper both model pickers project through. Named, not
    ///     globbed, because the rule is about this specific declaration.
    /// </summary>
    private const string LabelHomeRelativePath =
        "src/Harbor.Desktop.Abstractions/ViewModels/ModelRateLabel.cs";

    /// <summary>
    ///     The file allowed to decide that a rate table is the
    ///     <c>Pricing.Unknown</c> sentinel — the core's job, and the reason
    ///     <c>Pricing</c> publishes <c>IsUnknown</c> as a bit at all.
    /// </summary>
    private const string UnknownDeclarationRelativePath =
        "src/Harbor.Abstractions.Contracts/Models/Session.cs";

    /// <summary>Exact substring identifying the exempted declaration line.</summary>
    private const string UnknownDeclarationMarker = "InputPerMillion == 0m";

    /// <summary>Why the core's own zero comparison is not a hand-rolled guess.</summary>
    private const string UnknownDeclarationReason =
        "This is the DECLARATION of the answer the rule protects: Pricing.IsUnknown is where "
        + "\"does this model publish a price at all?\" is decided, once, over all four rates "
        + "(input, output, cache-read, cache-write). A renderer that re-derives it from the "
        + "input and output numbers alone is the defect this rule exists to stop — and the "
        + "shipped picker did exactly that, which is why it also got the cache-rate case wrong.";

    /// <summary>
    ///     One C# string literal on a line, quotes included. Rule 1 runs on the
    ///     matched SPAN rather than on the extracted contents so the lookbehind
    ///     still sees the character in front of the rate name (see the header).
    /// </summary>
    private static readonly Regex QuotedLiteral = new(
        @"""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     A rate-shaped identifier: any identifier ending in <c>PerMillion</c>,
    ///     starting at a boundary that is not a letter, a digit, an underscore or
    ///     a quote. The <c>PerMillion</c> SUFFIX is the fingerprint #653 already
    ///     established for a hand-rolled rate table
    ///     (<c>CostPricedInCoreRules</c>), so the two guards agree on what a rate
    ///     name looks like instead of each inventing a spelling.
    /// </summary>
    private static readonly Regex RateIdentifier = new(
        @"(?<![A-Za-z0-9_""])(?:[A-Za-z_][A-Za-z0-9_]*)?PerMillion\b",
        RegexOptions.Compiled);

    /// <summary>A rate-shaped identifier compared against a zero decimal literal.</summary>
    private static readonly Regex RateComparedToZero = new(
        @"(?<![A-Za-z0-9_""])(?:[A-Za-z_][A-Za-z0-9_]*)?PerMillion\s*(?:==|!=|>=|<=|>|<)\s*0m",
        RegexOptions.Compiled);

    /// <summary>A file, a 1-based line number, and the offending line.</summary>
    private sealed record Site(string RelativePath, int LineNumber, string Text);

    /// <summary>
    ///     Rule 1: a rate is spelled into a user-facing string on exactly one
    ///     product line. Two lines means the picker dropdown and the provider
    ///     browser are each free to word the same model's price differently —
    ///     which is precisely the state #686 reported and #653's scan set could
    ///     not see.
    /// </summary>
    [Test]
    public async Task RateLabel_IsSpelledIntoAStringInExactlyOneProductLine()
    {
        List<Site> sites = FindRateInStringSites();

        await Assert.That(sites.Count).IsEqualTo(1)
            .Because(
                "a rate value is interpolated into a user-facing string on " + sites.Count
                + " product line(s) — " + Describe(sites) + ". The same model then reads one way "
                + "in the picker dropdown and another way in the provider browser, and the two "
                + "have already drifted: for a zero-rate catalogue entry the picker said "
                + "\"pricing unknown\" while the browser printed \"$0.00 in / $0.00 out per 1M\". "
                + "Build the sentence ONCE in " + LabelHomeRelativePath
                + " and have every surface project through it. Carrying a rate (a record "
                + "parameter) and passing one (a hand-off) stay fine as often as you like; "
                + "spelling it into a string a second time is the duplication this guard exists "
                + "to prevent. See issue #686.");
    }

    /// <summary>
    ///     The single surviving spelling must be the declared home, and that home
    ///     must exist. Without this the count rule above could be satisfied by
    ///     deleting the label — a guard that passes because the feature is gone is
    ///     not a guard.
    /// </summary>
    [Test]
    public async Task TheSingleSpelling_LivesInTheDeclaredSharedHome()
    {
        List<Site> sites = FindRateInStringSites();
        string root = RepoPaths.RepoRoot ?? ".";

        await Assert.That(File.Exists(Path.Combine(root, LabelHomeRelativePath))).IsTrue()
            .Because(
                LabelHomeRelativePath + " is the one place allowed to spell a rate into a string, "
                + "and it does not exist. Either the shared wording helper was deleted or it was "
                + "never added; in both cases the two model pickers project the same rate through "
                + "two private copies. See issue #686.");

        List<string> outsideHome =
        [
            .. sites.Select(s => s.RelativePath).Distinct().Where(p => p != LabelHomeRelativePath)
        ];
        await Assert.That(outsideHome).IsEmpty()
            .Because(
                "the surviving spelling is in " + Describe(sites) + " but the rule names "
                + LabelHomeRelativePath + " as its home. A second helper, or a copy left behind in "
                + "one of the two pickers, is the duplication this guard exists to prevent.");
    }

    /// <summary>
    ///     Rule 2: only the core decides that a rate table is the
    ///     <c>Unknown</c> sentinel. A renderer that compares rates to zero is
    ///     guessing at a fact the core publishes as a bit — and, in the one place
    ///     that shipped, guessed it wrong.
    /// </summary>
    [Test]
    public async Task UnknownPrice_IsNotReDerivedFromTheRateNumbers()
    {
        List<Site> sites =
        [
            .. FindRateZeroSites().Where(s => !(s.RelativePath == UnknownDeclarationRelativePath
                                              && s.Text.Contains(UnknownDeclarationMarker, StringComparison.Ordinal)))
        ];

        await Assert.That(sites.Count).IsEqualTo(0)
            .Because(
                "a presentation line re-derives \"this model has no price\" by comparing a rate to "
                + "zero — " + Describe(sites) + ". " + UnknownDeclarationReason
                + " Read Pricing.IsUnknown instead. Exemption (a decision, not an oversight): "
                + UnknownDeclarationRelativePath + " declares it. See issue #686.");
    }

    /// <summary>
    ///     The exemption stays honest: it must name a line that still exists and
    ///     still compares a rate to zero. An exemption that has outlived its
    ///     reason is a hole with a comment on it.
    /// </summary>
    [Test]
    public async Task TheExemption_StillMatchesALineThatStillEarnsIt()
    {
        List<Site> declaration =
        [
            .. FindRateZeroSites().Where(s => s.RelativePath == UnknownDeclarationRelativePath)
        ];

        await Assert.That(declaration.Count).IsGreaterThan(0)
            .Because(
                UnknownDeclarationRelativePath + " no longer compares a rate to zero, so the "
                + "exemption — and, more importantly, the declaration this guard points readers at "
                + "— is gone. Either Pricing.IsUnknown moved or the rule needs a new home named.");
        await Assert.That(declaration.Any(s => s.Text.Contains(UnknownDeclarationMarker, StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "no line in " + UnknownDeclarationRelativePath + " matches the marker \""
                + UnknownDeclarationMarker + "\" any more — update the marker with the rename "
                + "rather than letting the exemption cover whatever the file now contains.");
    }

    /// <summary>
    ///     Non-vacuity, part 1: the discovery step must find a real, non-trivial
    ///     file set that contains both offenders. A scan rooted at a typo finds
    ///     zero files and then "passes" everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_ContainingBothOffenders()
    {
        IReadOnlyList<string> files = SourceScan.EnumerateProductCsFiles();

        await Assert.That(files.Count).IsGreaterThan(200)
            .Because("src/ + apps/ hold an order of magnitude more than 200 C# files; a smaller "
                     + "count means the tree filter broke.");
        foreach (string offender in OffenderFiles)
        {
            await Assert.That(files.Contains(offender)).IsTrue()
                .Because(offender + " must be inside the scanned set, or the rule polices nothing.");
        }
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME two matchers the rule runs on must fire on
    ///     planted copies of both banned shapes, and stay silent on the three
    ///     spellings that are legitimate. This is what separates "the guard is
    ///     green" from "the guard is looking at nothing".
    /// </summary>
    [Test]
    public async Task Matchers_FireOnPlantedCopies_AndStaySilentOnHandOffsAndJson()
    {
        await Assert.That(FindRateInStringLines(Line(
                "    public string L => $\"${InputPerMillion:F2} in / ${OutputPerMillion:F2} out per 1M\";")))
            .IsEqualTo([0])
            .Because("the planted shape this rule was written for must be detected, or the guard is blind");
        await Assert.That(FindRateInStringLines(Line(
                "    public string L => $\"{inPerMillion:F2} in / {outPerMillion:F2} out per 1M\";")))
            .IsEqualTo([0])
            .Because("a camelCase spelling of the same shape is the same defect, or the guard is blind");
        await Assert.That(FindRateInStringLines(Line(
                "                m.Pricing.InputPerMillion, m.Pricing.OutputPerMillion))")))
            .IsEmpty()
            .Because("passing a rate to the shared home is a hand-off, not a second label");
        await Assert.That(FindRateInStringLines(Line(
                "    decimal InputPerMillion,", "    decimal OutputPerMillion)")))
            .IsEmpty()
            .Because("a projection record may carry rates — the rule is about the SENTENCE, not the data");
        await Assert.That(FindRateInStringLines(Line(
                "        \"pricing\": { \"inputPerMillion\": 0, \"outputPerMillion\": 0 },")))
            .IsEmpty()
            .Because("a JSON key inside a provider-config sample is a string payload, not code");
        await Assert.That(FindRateInStringLines(Line(
                "    // the label reads $\"{InputPerMillion:F2} in / {OutputPerMillion:F2} out per 1M\"")))
            .IsEmpty()
            .Because("comment prose is not a second implementation");

        await Assert.That(FindRateZeroLines(Line(
                "        if (inputPerMillion == 0m && outputPerMillion == 0m)")))
            .IsEqualTo([0])
            .Because("the hand-rolled zero guess this rule was written for must be detected, or the guard is blind");
        await Assert.That(FindRateZeroLines(Line("        if (InputPerMillion == 0m)")))
            .IsEqualTo([0])
            .Because("a PascalCase guess is the same defect, or the guard is blind");
        await Assert.That(FindRateZeroLines(Line("        if (Pricing.IsUnknown)")))
            .IsEmpty()
            .Because("reading the core's bit is the fix, not a violation");
        await Assert.That(FindRateZeroLines(Line("    private const decimal Rate = 0m;")))
            .IsEmpty()
            .Because("a plain constant is not a rate compared to zero");
    }

    /// <summary>
    ///     The two files the defect was found in, named so the non-vacuity test can
    ///     assert the scan really reaches them. They live in
    ///     <c>src/Harbor.Desktop.Abstractions</c>, which is deliberately outside
    ///     <c>CostPricedInCoreRules</c>'s scan set — see the header for why this
    ///     rule polices the shape instead of widening that set.
    /// </summary>
    private static readonly string[] OffenderFiles =
    [
        "src/Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs",
        "src/Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModel.cs",
    ];

    /// <summary>Every product line that spells a rate identifier inside a string.</summary>
    private static List<Site> FindRateInStringSites() => FindSites(FindRateInStringLines);

    /// <summary>
    ///     Every product line that compares a rate identifier to a zero literal, the
    ///     core's own declaration included — the caller exempts it.
    /// </summary>
    private static List<Site> FindRateZeroSites() => FindSites(FindRateZeroLines);

    private static List<Site> FindSites(Func<string, IReadOnlyList<int>> find)
    {
        var sites = new List<Site>();
        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(file) is not { } text)
            {
                continue;
            }

            // Strip comments ONCE and hand the stripped text to BOTH the line
            // splitter and the matcher, so the numbers a matcher returns index
            // the same lines the failure message prints.
            string stripped = SourceScan.StripComments(text);
            string[] lines = stripped.Split('\n');
            foreach (int index in find(stripped))
            {
                sites.Add(new Site(SourceScan.Relative(file), index + 1, lines[index].Trim()));
            }
        }

        return sites;
    }

    /// <summary>
    ///     Joins planted lines into a source string and runs it through the same
    ///     comment-stripping the scan uses, so a self-test assertion can never
    ///     exercise a pipeline the rule does not run.
    /// </summary>
    private static string Line(params string[] lines) =>
        SourceScan.StripComments(string.Join('\n', lines) + "\n");

    /// <summary>0-based line numbers whose string literals spell a rate identifier.</summary>
    private static IReadOnlyList<int> FindRateInStringLines(string source)
    {
        var hits = new List<int>();
        string[] lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match literal in QuotedLiteral.Matches(lines[i]))
            {
                // The WHOLE span, quotes included: the lookbehind must see the
                // character in front of the rate name inside the real line.
                if (RateIdentifier.IsMatch(lines[i].Substring(literal.Index, literal.Length)))
                {
                    hits.Add(i);
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>0-based line numbers that compare a rate identifier to a zero literal.</summary>
    private static IReadOnlyList<int> FindRateZeroLines(string source)
    {
        var hits = new List<int>();
        string[] lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (RateComparedToZero.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static string Describe(IReadOnlyList<Site> sites)
        => sites.Count == 0
            ? "(none)"
            : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.LineNumber));
}
