// CostUnpricedBitRules.cs — the guard for #942: the core's "I could not price
// this session" bit must REACH the surface that paints the money.
//
// THE DEFECT THIS GUARDS
// ----------------------
// `SessionMetadata.IsCostKnown` (false ⇔ `Pricing.IsUnknown` ⇔ the model
// publishes no rate table) is carried into the UI as
// `CostSnapshot.IsCostUnpriced` precisely so that a renderer never has to guess.
// Six surfaces read it and print "—". Two did not:
//
//   PanelRows.TokenRows      StatusBarText.CostToUsd(cost), unconditionally
//   TokenUsageView.axaml     StringFormat='{}${0:F4}' on a bare decimal
//
// so on a model with no published rates — 11 of the 13 shipped providers, since
// `ProviderConfig.ParseModel` hands `Pricing.Unknown` to every JSON-served model
// and only anthropic + openai carry rate tables — the SAME FRAME showed
// "$0.0000" in the Alt+5 token-breakdown panel and "—" in the status bar beside
// it. A false claim about money, on the normal path rather than an edge one.
//
// WHY A NEW FILE AND NOT ANOTHER RULE IN MoneyCellSingleHomeRules
// --------------------------------------------------------------
// That file (#682) polices the money cell's SHAPE — who writes the "$" and the
// decimals. This polices the cell's PLACEMENT OF TRUTH — whether the writer was
// told the price is unknown. They are different questions: `StatusBarText.CostToUsd`
// and `TokenUsageViewModel.TotalCostText` are both correctly shaped, and only the
// second is honest. Folding this in would have made #682's own header false.
//
// It is also a scope hole #682 could not close. That file scans
// `SourceScan.EnumerateProductCsFiles()` — `*.cs` only. The Avalonia half of the
// defect was a `StringFormat` inside a `.axaml`, which no `*.cs` scan can see by
// construction. RULE A below scans the XAML tree for exactly that shape, so the
// class is covered on both sides of the file-extension line.
//
// THE TWO RULES, IN FULL
// ----------------------
// Scan the product tree (src/ + apps/, minus build output, tests/, contrib/ and
// .worktrees/ — the walk `SourceScan` already gives every other gate). Comments
// are stripped first. Then:
//
//   RULE A  A money cell may not be hand-spelled in XAML onto a RAW cost
//           decimal. A binding that supplies its own `${0:F<n>}` while naming a
//           `*Usd` property is a second writer that no C# scan can see. A
//           binding to a `*Text` property is exempt and is the SHAPE this rule
//           asks for: the cell is already spelled, XAML only displays it.
//
//   RULE B  A C# file that renders a money cell must consult the core's bit
//           somewhere in the file, unless it is a declared home. The bit is
//           allowed to live in a DIFFERENT member from the writer — the honest
//           surfaces read it in one method and render in another — so this is a
//           file-level rule, not a line-level one. A line-level rule was measured
//           first and rejected: it produced 8 sites, four of them the shape home
//           itself and the adapters that legally forward a bare decimal.
//
// WHY THIS IS A RATCHET WITH NAMED HOMES, NOT A HARD RULE
// -------------------------------------------------------
// Not every money cell CAN know the bit, and pretending otherwise produces a
// standing build failure — the trade `MoneyCellSingleHomeRules` already
// documents and makes for itself. The bit-blind writers that remain are shape
// functions and their adapters: `UsdCell.ToUsd` takes a decimal because a
// decimal is all a shape has; `StatusBarText.CostToUsd` and
// `StatusMappers.CostToUsd` forward to it; `CostAnimator` interpolates between
// two values the core already reported. Each is listed below with the reason it
// cannot take the bit, and `EveryHome_StillMatchesExactlyOneLine` keeps an entry
// from outliving its reason — a home that stops existing takes its mute button
// with it. A surface that shows NO cell when the bit is set is honest by
// omission rather than by shape, and is named as such: it is declining to make
// any claim. #1835 removed the last of those (the dead
// `TokenUsageViewModelBase`), so that set is empty today and every money
// surface either consults the bit or is one of the shape homes above.
//
// NON-VACUITY
// -----------
// Both halves are proven against the tree they police: the discovery test proves
// the walk reaches a real file set containing every declared home, and the
// planted controls prove the SAME matchers the rules run fire on the exact
// pre-fix lines and stay silent on the near-misses that are real lines in the
// product tree today. A guard that cannot be seen to fail is not a guard.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #942: the core publishes "this session's price is unknown"; every
///     surface that paints money must be able to say so.
/// </summary>
public sealed class CostUnpricedBitRules
{
    /// <summary>
    ///     One banned shape, the pattern that detects it, and what to do instead
    ///     — all three printed on failure so a reader never has to open this file
    ///     to learn the rule.
    /// </summary>
    private sealed record Rule(string Id, string Pattern, string Instead);

    private static readonly Rule[] Rules =
    [
        new Rule(
            "MONEY-CELL-HAND-SPELLED-IN-XAML-ON-A-RAW-DECIMAL",
            @"StringFormat=[^>]*\$\{0:F\d",
            "a `.cs` scan cannot see a money cell written inside a `.axaml`, so this is the "
            + "one shape the #682 guard is blind to by construction. Bind a property that "
            + "already HOLDS the spelled cell (e.g. `TotalCostText`) instead of a raw `*Usd` "
            + "decimal plus a StringFormat — that is also the only way the core's "
            + "IsCostUnpriced bit can reach the glyph. See issue #942."),
        new Rule(
            "MONEY-CELL-RENDERED-WITHOUT-THE-CORE-UNPRICED-BIT",
            @"(?:CostToUsd|UsdCell\.ToUsd|FormatCostUsd)\(",
            "a money cell whose writer never saw `IsCostUnpriced` prints \"$0.0000\" for a "
            + "model that publishes no rates, while the status bar beside it prints \"—\". "
            + "Take the bit (StatusBarText.CostCell / UsdCell.Unpriced), or declare this file "
            + "a home below with the reason it cannot know. See issue #942."),
    ];

    /// <summary>
    ///     The property half of RULE A as a pattern string: a binding onto a RAW
    ///     cost decimal. The `*Text` shape is deliberately absent: a binding to an
    ///     already-spelled cell is the fix, not a violation of it. Kept as a
    ///     string (not only a <see cref="Regex" />) so the combined tabular row
    ///     below is built from the same text, not a retyped copy.
    /// </summary>
    private const string RawMoneyPropertyPattern = @"\{Binding\s+\w*[Uu]sd\b";

    /// <summary>
    ///     A line that binds a money cell onto a RAW cost decimal. The `*Text`
    ///     shape is deliberately absent from the pattern: a binding to an
    ///     already-spelled cell is the fix, not a violation of it. The oracle
    ///     half of the equivalence proof: the combined row fires exactly when
    ///     this AND <see cref="Rules" />[0] both fire.
    /// </summary>
    private static readonly Regex RawMoneyBinding = new(RawMoneyPropertyPattern, RegexOptions.Compiled);

    /// <summary>
    ///     RULE A as ONE tabular pattern: the hand-spelled
    ///     <c>StringFormat='${0:F…}'</c> AND the raw-<c>*Usd</c> binding as two
    ///     order-independent lookaheads over the line (the TokenCell precedent,
    ///     batch-9). A tabular row holds a single pattern, and the rule IS this
    ///     conjunction — two separate rows would each fire on a near-miss the
    ///     conjunction deliberately permits. Built from the two original texts,
    ///     so the row cannot drift from the oracle it is proven against.
    /// </summary>
    private static readonly Regex CombinedMoneyBinding = new(
        $@"(?=[^\n]*{Rules[0].Pattern})(?=[^\n]*{RawMoneyPropertyPattern})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The core's own answer to "is this model's price published?".</summary>
    private static readonly Regex UnpricedBit = new(
        @"IsCostUnpriced|IsCostKnown|UnknownCostCell|UsdCell\.Unpriced",
        RegexOptions.Compiled);

    /// <summary>A money-cell writer, in either its qualified or bare spelling.</summary>
    private static readonly Regex MoneyWriter = new(
        @"(?:CostToUsd|UsdCell\.ToUsd|FormatCostUsd)\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     A file that may render a money cell without naming the bit, and why
    ///     the bit is genuinely out of its reach. A reason is mandatory.
    /// </summary>
    /// <remarks>
    ///     Two kinds, because they are exempt for different reasons and only one of
    ///     them is falsifiable by looking at the file:
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="RendersMoney" /> — the file really does write a money
    ///             cell and genuinely cannot know the bit (a shape function, or an
    ///             adapter whose signature carries only a decimal). Part 4 asserts
    ///             it STILL renders money: stop rendering money and the exemption
    ///             expires, because the rule should be watching the file again.
    ///         </item>
    ///         <item>
    ///             <see cref="NoSurface" /> — the file forwards a cost to a
    ///             property that has NO reader, so there is no money on screen to
    ///             misreport. Part 4 asserts the reason still holds: give the
    ///             property a reader and the exemption must be re-argued, not
    ///             inherited silently.
    ///         </item>
    ///     </list>
    /// </remarks>
    private sealed record FileHome(string RelativePath, string Reason, HomeKind Kind, string? UnreadProperty = null);

    private enum HomeKind
    {
        /// <summary>The file writes a money cell and cannot know the bit.</summary>
        RendersMoney,

        /// <summary>The file exposes a cost that nothing on screen reads, yet.</summary>
        NoSurface
    }

    private static readonly FileHome[] Homes =
    [
        new FileHome(
            "src/Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs",
            "A pure adapter: `CostToUsd(decimal) => StatusBarText.CostToUsd(costUsd)`. It "
            + "writes no glyph and no decimals of its own, and it is the bridge the "
            + "Avalonia tree reaches StatusBarText through. Its callers supply the bit, or "
            + "they do not; this file cannot.",
            HomeKind.RendersMoney),
        new FileHome(
            "src/Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs",
            "Interpolates between two values THE CORE already reported (`Report(decimal)`), "
            + "on the #676 ruling that the readout may only ever show a number the core "
            + "produced. It never consults a session snapshot, so there is no bit here to "
            + "read; the gate belongs at the surface that feeds it, which is where "
            + "`MainViewModel.HasCost` lives.",
            HomeKind.RendersMoney),
        new FileHome(
            "apps/Harbor.App.Avalonia/Views/Converters.cs",
            "A `decimal → string` IValueConverter over `StatusMappers.CostToUsd`, the "
            + "XAML-facing twin of the previous entry. Its fallback arm returns a literal "
            + "\"$0.0000\" for a non-decimal input, which is a SHAPE question "
            + "(MoneyCellSingleHomeRules territory) and not this rule's — an "
            + "IValueConverter's single-value signature cannot carry the bit.",
            HomeKind.RendersMoney),
        new FileHome(
            "apps/Harbor.App.Avalonia/ViewModels/MainViewModel.cs",
            "HONEST BY OMISSION, not by shape, and named so the distinction is on the "
            + "record. `HasCost => CostUsd > 0m` with `IsVisible=\"{Binding HasCost}\"` "
            + "means an unpriced session (CostUsd == 0) shows NO cost cell at all: it "
            + "declines to claim an amount rather than claiming a false one. That is "
            + "weaker than the \"—\" the other surfaces print, and it IS a real "
            + "inconsistency between renderers — but it is not a false claim about money, "
            + "and restyling a third surface's zero-cost placement belongs to whoever "
            + "decides that convention, not to a bug fix.",
            HomeKind.RendersMoney),
        new FileHome(
            "src/Harbor.Tui.CellForge/Chat/Widgets/SideBarView.cs",
            "HONEST BY OMISSION, same reasoning as MainViewModel: `if (state.CostUsd > 0)` "
            + "gates the cost line, so an unpriced session draws no money row. The sidebar "
            + "is a declared convention-B home in #682 (the allocation-free span twin of "
            + "StatusViewModel's \"0.####\"), which is why it has no access to the "
            + "UiState bit at all.",
            HomeKind.RendersMoney),
        // #1835 removed the last NoSurface home: the abstract base
        // `TokenUsageViewModelBase` (an abstract class with no derived class in
        // the product tree, whose `EstimatedCostUsd` had no reader anywhere) was
        // deleted, because a published base with nothing behind it is not a
        // surface — it is an unused promise. With it gone the set of
        // honest-by-omission homes is EMPTY, which is the stronger state: every
        // file that renders money now either consults the core's bit or is one
        // of the four shape homes above, which name why they cannot. The RULE is
        // unchanged; only the exemption list is shorter, and the liveness check
        // below has nothing left to falsify.
    ];

    // MECHANISM (#1086, step 2, conveyor batch-13)
    // --------------------------------------------
    // The two bans above are two ScanRules: RULE A as one tabular Forbidden row
    // over Glob "*.axaml" (the markup side batch-9 reached through a CustomParse;
    // this rule has no homes, so the runner executes the row directly with the
    // markup stripper), and RULE B as one documented Forbidden row over "*.cs"
    // whose verdict is ParseUnpricedCs through the Func-overload (the file-level
    // shape batch-11/12 established: a line regex cannot ask "does this file
    // name the bit ANYWHERE"). Enumeration, matching and the
    // control/discovery/baseline verdicts are ScanRunner's; this file keeps the
    // issue prose, the patterns, the homes, and the test names.
    //
    // RULE A is a conjunction of two shapes — a hand-spelled `${0:F<n>}`
    // StringFormat AND a binding onto a raw `*Usd` decimal — and a tabular row
    // holds ONE pattern, so the row carries both as order-independent
    // lookaheads over the line. The two originals stay beside it as the oracle
    // for CombinedXamlPattern_EqualsTheConjunctionItReplaces, which grades the
    // combined row against the conjunction over every planted control AND every
    // live stripped .axaml line: the row and the conjunction agree everywhere
    // they could disagree.
    //
    // The five RendersMoney homes that trip the file-level verdict live in the
    // C# baseline, each with its reason as a VALUE; StaleBaselineKeys is their
    // liveness (stop rendering money and the row rots loudly). Two old entries
    // are NOT rows, deliberately: UsdCell.cs names no writer call and
    // StatusBarText.cs names the bit in-file, so the file-level verdict never
    // flagged them — they were documentation, and their facts stay pinned by
    // TheBitAwareCell_StillExists_AndTheHonestSurfacesStillUseIt. The NoSurface
    // home cannot be a row under any matcher (a file with no writer emits no
    // hit by construction) and stays a small handwritten liveness check below.
    //
    // Scope parity is exact: Trees ["src", "apps"] walk what
    // EnumerateProductCsFiles/EnumerateProductXamlFiles walked, and the runner
    // chooses the stripper by extension (StripComments for .cs,
    // StripMarkupComments for .axaml) — the choice the URL control in
    // Matchers_FireOnThePreFixLines_AndStaySilentOnNearMisses pins.

    /// <summary>The XAML money-cell ban as data: the combined lookahead shape, four planted controls, a floor.</summary>
    private static readonly ScanRule XamlRule = new()
    {
        Id = "CostUnpricedBitXaml",
        Trees = ["src", "apps"],
        Glob = "*.axaml",
        Forbidden =
        [
            new ScanForbidden(
                Rules[0].Id,
                CombinedMoneyBinding,
                Rules[0].Instead),
        ],
        Controls =
        [
            new ScanControl("Planted/RawDecimalBinding.axaml", """
                <TextBlock Text="{Binding TotalCostUsd, StringFormat='{}${0:F4}'}" />
                """, Rules[0].Id),
            // A binding to an already-spelled cell is the SHAPE this rule asks
            // for, not a violation of it — StatusBarView.axaml:98 is that line.
            new ScanControl("NearMiss/SpelledCellBinding.axaml", """
                <TextBlock Text="{Binding CostText, StringFormat='${0:F2}'}" />
                """, null),
            // The fixed binding, with no StringFormat at all, must be silent.
            new ScanControl("Fixed/SpelledCellNoFormat.axaml", """
                <TextBlock Text="{Binding TotalCostText}" />
                """, null),
            // A markup comment is not a live binding — the runner reads markup
            // comments stripped, and this control is what proves the stripper
            // is the one that ran.
            new ScanControl("NearMiss/MarkupComment.axaml", """
                <!-- <TextBlock Text="{Binding TotalCostUsd, StringFormat='{}${0:F4}'}" /> -->
                """, null),
        ],
        MinHits = 20,
        MustContain =
        [
            "apps/Harbor.App.Avalonia/Views/Shell/StatusBarView.axaml",
            "apps/Harbor.App.Avalonia/Views/TokenUsageView.axaml",
        ],
    };

    /// <summary>The C# money-cell ban as data: one documented shape, five planted controls, the homes as the baseline.</summary>
    private static readonly ScanRule CsRule = new()
    {
        Id = "CostUnpricedBitCs",
        Trees = ["src", "apps"],
        Forbidden =
        [
            new ScanForbidden(
                Rules[1].Id,
                MoneyWriter,
                Rules[1].Instead),
        ],
        Baseline =
        [
            .. Homes.Where(static h => h.Kind == HomeKind.RendersMoney)
                .Select(static h => new ScanBaseline(Rules[1].Id + " " + h.RelativePath, IsPrefix: false, Reason: h.Reason)),
        ],
        Controls =
        [
            new ScanControl("Planted/PanelWritesMoneyWithoutTheBit.cs", """
                rows.Add($"total {n}  {StatusBarText.CostToUsd(cost)}");
                """, Rules[1].Id),
            // Calling the bit-aware home is the fix, not a violation of it —
            // StatusBarFacts.cs:103 is that line.
            new ScanControl("Fixed/BitAwareHomeCall.cs", """
                Cost = StatusBarText.CostCell(cost.CostUsd, cost.IsCostUnpriced);
                """, null),
            // A ternary that consults the bit is the honest shape every layer
            // can reach — TuiViewModels.cs:131-133 is that line.
            new ScanControl("Fixed/HonestTernary.cs", """
                CostText => IsCostKnown ? UsdCell.ToUsd(Cost) : UsdCell.Unpriced;
                """, null),
            // Comment prose is not an implementation.
            new ScanControl("NearMiss/Comment.cs", """
                // rows.Add($"total {n}  {StatusBarText.CostToUsd(cost)}");
                """, null),
            // A bare decimal with no writer is not a money cell — the premise
            // the (now empty) NoSurface home set rested on: a property nothing
            // reads makes no claim about money.
            new ScanControl("NearMiss/BareDecimalNoWriter.cs", """
                public decimal EstimatedCostUsd { get; }
                """, null),
        ],
        MinHits = 500,
        MustContain =
        [
            .. Homes.Select(static h => h.RelativePath),
            "src/Harbor.Abstractions.Contracts/Models/UsdCell.cs",
            "src/Harbor.Ui.Framework.State/State/StatusBarText.cs",
        ],
        CustomParse = ParseUnpricedCs,
    };

    /// <summary>
    ///     The custom parser: the original RULE B verdict over one file's raw
    ///     source — the shared comment stripper, the writer-present AND bit-
    ///     absent question at FILE scope (the bit may live in a different member
    ///     from the writer; every honest surface does exactly that), one report
    ///     at the first writer line. The Forbidden row above documents the banned
    ///     spelling; the runner does not execute it (a line regex cannot express
    ///     the file-level bit question).
    /// </summary>
    private static IEnumerable<ScanHit> ParseUnpricedCs(string displayPath, string rawSource)
    {
        string code = SourceScan.StripComments(rawSource);
        if (!MoneyWriter.IsMatch(code) || UnpricedBit.IsMatch(code))
        {
            yield break;
        }

        int line = 1;
        foreach ((int index, string candidate) in ReadAllLines(code))
        {
            if (MoneyWriter.IsMatch(candidate))
            {
                line = index;
                break;
            }
        }

        yield return new ScanHit(Rules[1].Id, displayPath, line, "renders a money cell; never names the unpriced bit");
    }

    [Test]
    public async Task NoSurface_PaintsAMoneyCellItCannotTellIsUnpriced()
    {
        List<string> violations = [.. ScanRunner.Evaluate(XamlRule), .. ScanRunner.Evaluate(CsRule)];

        await Assert.That(violations).IsEmpty()
            .Because(
                "a money cell is painted without the core's \"price unknown\" answer at "
                + (violations.Count == 0 ? "(none)" : string.Join("; ", violations)) + ". "
                + string.Join(" | ", Rules.Select(r => r.Id + " → " + r.Instead))
                + " Declared homes (a decision, not an oversight): " + DescribeHomes()
                + " See issue #942.");
    }

    /// <summary>
    ///     The planted controls — both pre-fix lines and every near-miss — drive
    ///     the REAL rules: the XAML conjunction through its combined row, the C#
    ///     file verdict through its custom parser. Offenders reported under
    ///     their sub-id, near-misses silent.
    /// </summary>
    [Test]
    public async Task Detector_ControlsDriveTheRealParser()
    {
        List<string> xamlFailures = ScanRunner.CheckControls(XamlRule);
        List<string> csFailures = ScanRunner.CheckControls(CsRule);

        await Assert.That(xamlFailures).IsEmpty()
            .Because(
                "the four planted XAML controls must agree with the four matcher cases below through "
                + "the rule's own combined row — otherwise the rule guards nothing, or forbids the fix. "
                + string.Join("; ", xamlFailures));
        await Assert.That(csFailures).IsEmpty()
            .Because(
                "the five planted C# controls must agree with the five matcher cases below through "
                + "the rule's own file-level parser — otherwise the rule guards nothing, or forbids "
                + "the fix. " + string.Join("; ", csFailures));
    }

    /// <summary>
    ///     The equivalence proof for the combined RULE A row: two zero-width
    ///     lookaheads over a line hold exactly when both shapes match the line,
    ///     so the proof is extensional — the combined row and the conjunction
    ///     must agree on every planted control AND on every live stripped
    ///     .axaml line in the product tree, which is everywhere they could
    ///     disagree. If this fails, the tabular row drifted from the oracle.
    /// </summary>
    [Test]
    public async Task CombinedXamlPattern_EqualsTheConjunctionItReplaces()
    {
        var mismatches = new List<string>();
        foreach (ScanControl control in XamlRule.Controls)
        {
            mismatches.AddRange(FindEquivalenceMismatches("control " + control.File, control.Source));
        }

        foreach (string path in SourceScan.EnumerateProductXamlFiles())
        {
            string? text = SourceScan.TryReadAllText(path);
            if (text is not null)
            {
                mismatches.AddRange(FindEquivalenceMismatches(SourceScan.Relative(path), text));
            }
        }

        await Assert.That(mismatches).IsEmpty()
            .Because(
                "the combined lookahead row must fire exactly when the StringFormat shape AND the "
                + "raw-*Usd binding shape both fire — a line where they disagree is a line the rule "
                + "judges differently from the oracle the matcher cases trust. " + string.Join("; ", mismatches));
    }

    /// <summary>
    ///     Per stripped markup line, where the combined RULE A row and the
    ///     conjunction of the two original shapes disagree. Empty means the row
    ///     IS the conjunction over this source.
    /// </summary>
    private static IEnumerable<string> FindEquivalenceMismatches(string origin, string source)
    {
        foreach ((int index, string rawLine) in SourceScan.StripMarkupComments(source).Split('\n')
                     .Select((line, i) => (Line: i + 1, Text: line.TrimEnd('\r'))))
        {
            bool combined = CombinedMoneyBinding.IsMatch(rawLine);
            bool conjunction = Regex.IsMatch(rawLine, Rules[0].Pattern, RegexOptions.CultureInvariant)
                && RawMoneyBinding.IsMatch(rawLine);
            if (combined != conjunction)
            {
                yield return $"{origin}:{index} combined={combined} conjunction={conjunction} :: {rawLine.Trim()}";
            }
        }
    }

    /// <summary>
    ///     Non-vacuity, part 1: the walk must reach a real, non-trivial file set
    ///     that contains every declared home. A scan rooted at a typo finds zero
    ///     files and then passes everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_HoldingEveryDeclaredHome()
    {
        List<string> xamlDiscovery = ScanRunner.CheckDiscovery(XamlRule);
        List<string> csDiscovery = ScanRunner.CheckDiscovery(CsRule);

        await Assert.That(csDiscovery).IsEmpty()
            .Because("src/ + apps/ hold far more than 500 C# files, including every declared home; "
                     + "a smaller count — or a scope that stopped seeing one of those files — means the "
                     + "tree filter broke and every C# rule in this file is reading nothing. "
                     + string.Join("; ", csDiscovery));
        await Assert.That(xamlDiscovery).IsEmpty()
            .Because("the Avalonia views hold more than 20 .axaml files, including the two the issue "
                     + "is about; a smaller count means RULE A — the rule that exists because #682 could "
                     + "not see XAML — is reading nothing. " + string.Join("; ", xamlDiscovery));
    }

    /// <summary>
    ///     Non-vacuity, part 2: the SAME matchers the rules run must fire on the
    ///     exact pre-fix lines and stay silent on the near-misses that are real
    ///     lines in the product tree today. This is what separates "the guard is
    ///     green" from "the guard is looking at nothing" — and these controls are
    ///     why the two defects in this issue are known to have been CAUGHT rather
    ///     than merely not repeated.
    /// </summary>
    [Test]
    public async Task Matchers_FireOnThePreFixLines_AndStaySilentOnNearMisses()
    {
        // The two lines this issue removed, verbatim.
        await Assert.That(FindXamlIn(["<TextBlock Text=\"{Binding TotalCostUsd, StringFormat='{}${0:F4}'}\""])
                .Count)
            .IsGreaterThan(0)
            .Because("this is the Avalonia half of #942 verbatim; RULE A must catch it");
        await Assert.That(FindIn(["        rows.Add($\"total {n}  {StatusBarText.CostToUsd(cost)}\");"]).Count)
            .IsGreaterThan(0)
            .Because("this is the panel half of #942 verbatim; RULE B must catch it");

        // …and the near-misses, each a real line in the tree today.
        await Assert.That(FindXamlIn(["<TextBlock Text=\"{Binding CostText, StringFormat='${0:F2}'}\""]).Count)
            .IsEqualTo(0)
            .Because("a binding to an already-spelled cell is the SHAPE this rule asks for, "
                     + "not a violation of it — StatusBarView.axaml:98 is that line");
        await Assert.That(FindXamlIn(["<TextBlock Text=\"{Binding TotalCostText}\""])
                .Count)
            .IsEqualTo(0)
            .Because("the fixed binding, with no StringFormat at all, must be silent");
        await Assert.That(FindIn(["        Cost = StatusBarText.CostCell(cost.CostUsd, cost.IsCostUnpriced);"]).Count)
            .IsEqualTo(0)
            .Because("calling the bit-aware home is the fix, not a violation of it — "
                     + "StatusBarFacts.cs:103 is that line");
        await Assert.That(FindIn(["        CostText => IsCostKnown ? UsdCell.ToUsd(Cost) : UsdCell.Unpriced;"]).Count)
            .IsEqualTo(0)
            .Because("a ternary that consults the bit is the honest shape every layer can "
                     + "reach — TuiViewModels.cs:131-133 is that line");
        await Assert.That(FindIn(["        // rows.Add($\"total {n}  {StatusBarText.CostToUsd(cost)}\");"]).Count)
            .IsEqualTo(0)
            .Because("comment prose is not an implementation");
        await Assert.That(FindXamlIn(["<!-- <TextBlock Text=\"{Binding TotalCostUsd, StringFormat='{}${0:F4}'}\" -->"])
                .Count)
            .IsEqualTo(0)
            .Because("a markup comment is not a live binding — RULE A reads markup comments "
                     + "stripped, and this control is what proves the stripper is the one that ran "
                     + "(this file's own #942 note names the old binding in a comment)");

        // The other half of that control: the markup stripper must not EAT real
        // markup. Reusing the C# stripper over a .axaml truncates any line holding a
        // URL at its `//`, which would silently blind a rule to everything after it.
        await Assert.That(SourceScan.StripMarkupComments(
                "<Image Source=\"http://example.com/a.png\" StringFormat='${0:F4}' />").Contains("example.com"))
            .IsTrue()
            .Because("a `//` in a URL is not a C# line comment; stripping markup with the C# "
                     + "stripper truncates real bindings, which is how a scan silently stops "
                     + "seeing anything. See issue #942.");
    }

    /// <summary>
    ///     Non-vacuity, part 3 — the rule's other half. Surfaces may only be
    ///     EXEMPTED from naming the bit IF the bit-aware cell still exists and is
    ///     still reachable. If `StatusBarText.CostCell` or `UsdCell.Unpriced` is
    ///     ever renamed away, "nothing names the bit" can never again be
    ///     satisfied by the answer having been deleted.
    /// </summary>
    [Test]
    public async Task TheBitAwareCell_StillExists_AndTheHonestSurfacesStillUseIt()
    {
        string root = RequireRepoRoot();

        string? facts = SourceScan.TryReadAllText(
            Path.Combine(root, "src/Harbor.Ui.Framework.Projection/Projection/StatusBarFacts.cs"));
        await Assert.That(facts).IsNotNull()
            .Because("the projection that feeds the status bar must exist; if it moved, update "
                     + "this guard rather than letting the exemption list quietly widen");
        await Assert.That(facts!).Contains("StatusBarText.CostCell(cost.CostUsd, cost.IsCostUnpriced)")
            .Because("this is the one call every surface is sent to for the unknown-price answer. "
                     + "If it is gone, the homes below have no alternative to defer to and this "
                     + "rule is protecting nothing. See issue #942.");

        string? usdCell = SourceScan.TryReadAllText(
            Path.Combine(root, "src/Harbor.Abstractions.Contracts/Models/UsdCell.cs"));
        await Assert.That(usdCell).Contains("public const string Unpriced =")
            .Because("UsdCell.Unpriced is the glyph the bit-aware surfaces print; it must keep "
                     + "existing for the same reason CostCell must. See issue #942.");
    }

    /// <summary>
    ///     The homes stay honest: each names a file that exists with a reason a
    ///     reader can check. The RendersMoney half's deeper claim — "still renders
    ///     money" — moved to Baseline_Rows_Are_Not_Stale, which re-probes it
    ///     through the rule itself. The NoSurface half's claim — "still nothing
    ///     on screen reads this" — is relational rather than a line scan and
    ///     stays a small handwritten check below.
    /// </summary>
    [Test]
    public async Task EveryHome_StillExistsAndItsReasonStillHolds()
    {
        string root = RequireRepoRoot();

        foreach (FileHome home in Homes)
        {
            await Assert.That(home.Reason.Length).IsGreaterThan(80)
                .Because("home " + home.RelativePath + " must state, in a sentence a reader can "
                         + "check, why the core's unpriced bit is out of its reach");

            string path = Path.Combine(root, home.RelativePath);
            await Assert.That(File.Exists(path)).IsTrue()
                .Because("home " + home.RelativePath + " names a file that no longer exists — "
                         + "drop the entry, do not leave the rule muted");
        }

        // HomeKind.NoSurface — the exemption was "nothing on screen reads this".
        // That is falsifiable, so it is asserted rather than trusted. The set is
        // EMPTY since #1835: the last one named the dead `TokenUsageViewModelBase`
        // and was deleted with the file. An empty set grants nobody an exemption,
        // so the rule is at full strength, and the loop below has nothing to
        // falsify — which is why it is a loop and not a `Single`.
        foreach (FileHome noSurface in Homes.Where(h => h.Kind == HomeKind.NoSurface))
        {
            await Assert.That(noSurface.UnreadProperty).IsNotNull()
                .Because("a NoSurface home must NAME the property it claims nothing reads, or "
                         + "the check below has nothing to falsify and the entry is unfalsifiable weight");

            await Assert.That(ProductReadersOf(noSurface)).IsEmpty()
                .Because("home " + noSurface.RelativePath + " was exempt because `" + noSurface.UnreadProperty
                         + "` has no reader in the product tree. It now has one ("
                         + string.Join(", ", ProductReadersOf(noSurface))
                         + ") — give it the core's unpriced bit or re-argue the exemption, do not "
                         + "inherit it silently");
        }
    }

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself — the
    ///     reasons the homes always carried, now as values the runner can read.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(CsRule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: stop rendering money without
    ///     the bit and the row that excused it fails here.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            CsRule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(CsRule)));

        await Assert.That(stale).IsEmpty()
            .Because(
                "A baseline row that no longer matches reality is a lie: the site was fixed (or the file "
                + "left the scope), the row grandfathers nothing and hides the next real violation. "
                + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    ///     Product-tree files, other than the declaring one, that mention a
    ///     <see cref="HomeKind.NoSurface" /> home's claimed-unread property. This
    ///     file is not in the product tree at all, so the rule cannot silence itself.
    /// </summary>
    private static IReadOnlyList<string> ProductReadersOf(FileHome home)
    {
        var found = new List<string>();
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            string relative = SourceScan.Relative(path);
            if (relative == home.RelativePath)
            {
                continue;
            }

            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            if (Regex.IsMatch(
                    SourceScan.StripComments(text),
                    @"\b" + Regex.Escape(home.UnreadProperty!) + @"\b",
                    RegexOptions.CultureInvariant))
            {
                found.Add(relative);
            }
        }

        return found;
    }

    // ── scanning helpers ───────────────────────────────────────────────────

    private static string RequireRepoRoot() =>
        RepoPaths.RepoRoot
        ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    /// <summary>
    ///     The C# lines that trip RULE B, from in-memory text.
    /// </summary>
    /// <remarks>
    ///     The oracle for the C# matcher cases below: the rule itself grades
    ///     whole files through ParseUnpricedCs (writer present AND bit absent
    ///     at file scope), and Detector_ControlsDriveTheRealParser proves that
    ///     parser agrees with these cases — the two tests together are what
    ///     used to be one shared helper. It must apply BOTH of the rule's
    ///     conditions, not just the writer. Testing only the writer is a laxer
    ///     helper than the rule and proves nothing: it flags
    ///     <c>CostText =&gt; IsCostKnown ? UsdCell.ToUsd(Cost) : UsdCell.Unpriced;</c>,
    ///     which is the honest shape the rule deliberately permits, and the control
    ///     that is supposed to prove the near-miss stays silent would fail on it.
    /// </remarks>
    private static IReadOnlyList<string> FindIn(IReadOnlyList<string> lines)
    {
        string code = SourceScan.StripComments(string.Join("\n", lines));
        return [.. ReadAllLines(code)
            .Where(line => MoneyWriter.IsMatch(line.Text) && !UnpricedBit.IsMatch(line.Text))
            .Select(line => line.Text)];
    }

    /// <summary>
    ///     The XAML lines that trip RULE A, from in-memory text.
    /// </summary>
    /// <remarks>
    ///     The oracle for the XAML matcher cases below: the rule itself runs the
    ///     combined lookahead row, and
    ///     CombinedXamlPattern_EqualsTheConjunctionItReplaces proves the row and
    ///     this conjunction agree on every control and every live line. Strips
    ///     markup comments FIRST, exactly as the real scan does — and that
    ///     ordering is the whole point of the control, not a detail. Without it this
    ///     helper is a laxer matcher than the rule and the control that exists to
    ///     prove the stripper ran fails on its own input instead: CI caught exactly
    ///     that, reporting a planted markup comment as a live binding.
    /// </remarks>
    private static IReadOnlyList<string> FindXamlIn(IReadOnlyList<string> lines)
    {
        var hits = new List<string>();
        foreach (string line in SourceScan.StripMarkupComments(string.Join("\n", lines)).Split('\n'))
        {
            if (Regex.IsMatch(line, Rules[0].Pattern, RegexOptions.CultureInvariant)
                && RawMoneyBinding.IsMatch(line))
            {
                hits.Add(line);
            }
        }

        return hits;
    }

    private static IEnumerable<(int Line, string Text)> ReadAllLines(string text) =>
        text.Split('\n').Select((line, index) => (Line: index + 1, Text: line.TrimEnd('\r')));

    private static string DescribeHomes() =>
        string.Join("; ", Homes.Select(h => h.RelativePath + " [" + h.Reason + "]"));
}