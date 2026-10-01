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
// with it. Three further homes are honest-by-omission rather than by shape, and
// are named as such: a surface that shows NO cell when the bit is set is not
// making a false claim, it is declining to make any.
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
    ///     A line that binds a money cell onto a RAW cost decimal. The `*Text`
    ///     shape is deliberately absent from the pattern: a binding to an
    ///     already-spelled cell is the fix, not a violation of it.
    /// </summary>
    private static readonly Regex RawMoneyBinding = new(@"\{Binding\s+\w*[Uu]sd\b", RegexOptions.Compiled);

    /// <summary>The core's own answer to "is this model's price published?".</summary>
    private static readonly Regex UnpricedBit = new(
        @"IsCostUnpriced|IsCostKnown|UnknownCostCell|UsdCell\.Unpriced",
        RegexOptions.Compiled);

    /// <summary>A money-cell writer, in either its qualified or bare spelling.</summary>
    private static readonly Regex MoneyWriter = new(
        @"(?:CostToUsd|UsdCell\.ToUsd|FormatCostUsd)\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     The money cell written out by hand rather than delegated — the shape
    ///     #682 froze. Counted here only so a home is not required to DELEGATE:
    ///     <c>UsdCell.ToUsd</c> is the one writer that spells the cell itself, and
    ///     an exemption check that demanded delegation would fail the shape home.
    /// </summary>
    private static readonly Regex MoneyShape = new(
        @"""\$\s*\""?\s*\+|\$\{?\d*:F\d|\""F4""",
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
            "src/Harbor.Abstractions.Contracts/Models/UsdCell.cs",
            "THE SHAPE HOME (#682). `ToUsd(decimal)` takes a bare decimal because a shape "
            + "is all it has: whether the price is KNOWN is a caller's question, answered "
            + "by `UsdCell.Unpriced` which this same file declares. A shape that could "
            + "answer it would be a cell, not a shape.",
            HomeKind.RendersMoney),
        new FileHome(
            "src/Harbor.Ui.Framework.State/State/StatusBarText.cs",
            "Holds BOTH halves. `CostCell(costUsd, isCostUnpriced)` is the bit-aware entry "
            + "point this guard sends every surface to — it tests the bit three lines above "
            + "the `CostToUsd` call — and `CostToUsd` is the bare-decimal forwarder #682 "
            + "kept for callers that have already decided. Naming the canonical home as a "
            + "violation would make the rule unsatisfiable.",
            HomeKind.RendersMoney),
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
        new FileHome(
            "src/Harbor.Desktop.Abstractions/ViewModels/TokenUsageViewModelBase.cs",
            "An abstract base with no derived class in the product tree: `Rows` is declared "
            + "and never populated, and `EstimatedCostUsd` has no reader anywhere. There is "
            + "no money on screen here to misreport. Listed so that adding the first real "
            + "consumer does not arrive already non-compliant.",
            HomeKind.NoSurface,
            "EstimatedCostUsd"),
    ];

    /// <summary>A file, a 1-based line number, the rule it tripped, and the line.</summary>
    private sealed record Site(string RelativePath, int Line, string RuleId, string Text);

    [Test]
    public async Task NoSurface_PaintsAMoneyCellItCannotTellIsUnpriced()
    {
        List<Site> sites = [.. ScanAllSites()];

        await Assert.That(sites.Count).IsEqualTo(0)
            .Because(
                "a money cell is painted without the core's \"price unknown\" answer at "
                + Describe(sites) + ". " + string.Join(" | ", Rules.Select(r => r.Id + " → " + r.Instead))
                + " Declared homes (a decision, not an oversight): " + DescribeHomes()
                + " See issue #942.");
    }

    /// <summary>
    ///     Non-vacuity, part 1: the walk must reach a real, non-trivial file set
    ///     that contains every declared home. A scan rooted at a typo finds zero
    ///     files and then passes everything.
    /// </summary>
    [Test]
    public async Task Discovery_FindsARealFileSet_HoldingEveryDeclaredHome()
    {
        var files = SourceScan.EnumerateProductCsFiles();
        var xaml = SourceScan.EnumerateProductXamlFiles();

        await Assert.That(files.Count).IsGreaterThan(500)
            .Because("src/ + apps/ hold far more than 500 C# files; a smaller count means the "
                     + "tree filter broke and every C# rule in this file is reading nothing");
        await Assert.That(xaml.Count).IsGreaterThan(20)
            .Because("the Avalonia views hold more than 20 .axaml files; a smaller count means "
                     + "RULE A — the rule that exists because #682 could not see XAML — is "
                     + "reading nothing");

        foreach (FileHome home in Homes)
        {
            await Assert.That(files.Any(f => SourceScan.Relative(f) == home.RelativePath))
                .IsTrue()
                .Because(home.RelativePath + " holds a declared home and must be inside the "
                         + "scanned set, or the rule polices nothing");
        }
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
    ///     Non-vacuity, part 4: the exemption list stays honest in both
    ///     directions, each entry against the kind of reason it gives. An entry
    ///     whose file is gone, or whose stated reason has expired, is dead weight
    ///     that would silently widen the rule the next time someone re-adds a
    ///     bit-blind writer there. A reason is mandatory.
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

            string source = SourceScan.TryReadAllText(path) ?? string.Empty;
            string code = SourceScan.StripComments(source);

            if (home.Kind == HomeKind.RendersMoney)
            {
                await Assert.That(MoneyWriter.IsMatch(code) || MoneyShape.IsMatch(code)).IsTrue()
                    .Because("home " + home.RelativePath + " is exempt because it renders a money "
                             + "cell without the bit. It no longer contains a money writer, so it "
                             + "is not rendering anything — keeping the entry would mute a file "
                             + "the rule should be watching again");
                continue;
            }

            // HomeKind.NoSurface — the exemption was "nothing on screen reads this".
            // That is falsifiable, so it is asserted rather than trusted.
            await Assert.That(home.UnreadProperty).IsNotNull()
                .Because("a NoSurface home must NAME the property it claims nothing reads, or "
                         + "part 4 has nothing to falsify and the entry is unfalsifiable weight");

            await Assert.That(ProductReadersOf(home!)).IsEmpty()
                .Because("home " + home.RelativePath + " was exempt because `" + home.UnreadProperty
                         + "` has no reader in the product tree. It now has one ("
                         + string.Join(", ", ProductReadersOf(home!))
                         + ") — give it the core's unpriced bit or re-argue the exemption, do not "
                         + "inherit it silently");
        }
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

    private static IReadOnlyList<Site> ScanAllSites()
    {
        var sites = new List<Site>();
        var homes = Homes.Select(h => h.RelativePath).ToHashSet(StringComparer.Ordinal);

        // RULE A — the XAML half. Only bindings onto a RAW cost decimal count; a
        // binding to an already-spelled cell is the fix this rule asks for.
        foreach (string path in SourceScan.EnumerateProductXamlFiles())
        {
            string relative = SourceScan.Relative(path);
            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            string[] lines = SourceScan.StripMarkupComments(text).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (!Regex.IsMatch(line, Rules[0].Pattern, RegexOptions.CultureInvariant))
                {
                    continue;
                }

                if (RawMoneyBinding.IsMatch(line))
                {
                    sites.Add(new Site(relative, i + 1, Rules[0].Id, line.Trim()));
                }
            }
        }

        // RULE B — the C# half, at FILE scope. The bit is allowed to be read in a
        // different member from the writer (every honest surface does exactly
        // that), so the question is whether this file knows the answer at all.
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            string relative = SourceScan.Relative(path);
            if (homes.Contains(relative))
            {
                continue;
            }

            string? text = SourceScan.TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            string code = SourceScan.StripComments(text);
            if (!MoneyWriter.IsMatch(code) || UnpricedBit.IsMatch(code))
            {
                continue;
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

            sites.Add(new Site(relative, line, Rules[1].Id, "renders a money cell; never names the unpriced bit"));
        }

        return sites;
    }

    /// <summary>
    ///     The C# lines that trip RULE B, from in-memory text.
    /// </summary>
    /// <remarks>
    ///     Shared with the planted controls so the control proves the SAME matcher
    ///     the rule runs — and it must apply BOTH of the rule's conditions, not just
    ///     the writer. Testing only the writer is a laxer helper than the rule and
    ///     proves nothing: it flags
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
    ///     Strips markup comments FIRST, exactly as the real scan does — and that
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

    private static string Describe(IReadOnlyList<Site> sites) =>
        sites.Count == 0
            ? "(none)"
            : string.Join(
                "; ",
                sites.Select(s => s.RelativePath + ":" + s.Line + " [" + s.RuleId + "] " + s.Text));

    private static string DescribeHomes() =>
        string.Join("; ", Homes.Select(h => h.RelativePath + " [" + h.Reason + "]"));
}