// DialogKeyDispatchCensusRule.cs — MEASUREMENT + ratchet for #472 item 2 and #473.
//
// WHAT THE ISSUES CLAIM, AND WHAT THE TREE SAYS
// ----------------------------------------------
// #472 calls `DialogOverlay` a god-object: "1149 lines, _kind mentioned 50
// times, 41 are routing checks; two fully duplicated key handlers
// (:426-473 and :481-659, 248 lines for identical logic)". #473 says the same
// work again from the GoF side ("State pattern replaced by _kind enum + 41
// branch points"), and names its own overlap with #472.
//
// Measured on dev, every clause was re-derived. Three of the four do not
// survive:
//
//   * THE FILE IS BIGGER, NOT SMALLER. 1203 lines on dev, not 1149.
//
//   * "41 BRANCH POINTS" IS ONE DISPATCH MATRIX. `_kind` appears 51 times in
//     the file, and 23 of them are inside a single method,
//     `HandleKey(in KeyEvent)`. Those 23 are the second axis of ONE
//     `switch (key.Key)` — a key-by-dialog-kind table with 14 keys and 31
//     populated cells. That is one property ("what does this key do in this
//     dialog"), written out as a table, not 41 independent decisions.
//
//   * THE TWO HANDLERS ARE NOT DUPLICATES. `HandleKey(ConsoleKeyInfo)` carries
//     4 case labels; `HandleKey(in KeyEvent)` carries 14. They share 4
//     (Escape, Tab, Left, Right) and the kitty path has 10 key cases the
//     legacy path does not contain at all — Up, Down, PageUp, PageDown, Home,
//     End, Backspace, Delete, Char, and the modifier-aware Enter. There is no
//     "identical logic" to collapse; there is a shared 4-case prefix and then
//     two different vocabularies. The legacy path delegates its remainder to
//     five per-kind methods (`HandlePromptKey`, `HandleSelectKey`,
//     `HandleRadioKey`, `HandleApprovalKey`, `HandleMultilineKey`); the kitty
//     path inlines a flat matrix instead.
//
// And the per-kind split #473 asks for is already HALF DONE: five per-kind key
// handlers and five per-kind painters (`PaintSelectList`, `PaintRadioRow`,
// `PaintMultiline`, `PaintApproval`, `ApprovalControlRows`) are in the file
// today. The remaining work is "finish the split", which is #473's to decide,
// not this file's.
//
// THE FACT NOBODY HAS WRITTEN DOWN
// --------------------------------
// Of the seven `DialogKind` values, FIVE have no product caller. `ShowAlert`
// (2 sites) and `ShowPrompt` (4), both in `OnboardingFlow`, are the only
// `.Show*(` calls in `src/` + `apps/`. Confirm, Select, Radio, Multiline and
// Approval are reachable from tests only.
//
// The sharp row is Alert: it is one of the two product-shown kinds AND it
// appears in NEITHER key path. It is shown by product code, and every key it
// receives goes through the unconditional Escape/Tab/CycleFocus prefix or hits
// `_ => false` / `default: return false`. That is a real behavioural fact about
// a live path, and it is the row to look at before splitting anything — a split
// that gave each kind its own handler would have to decide what Alert does,
// and today nothing answers that.
//
// WHY THIS FILE IS NOT A FIX
// --------------------------
// The proposed remedy is seven per-kind widget classes. Those live in
// `src/Harbor.Tui.CellForge/Chat/Widgets/`, which #555 freezes by name — the
// frozen list reads "a new cellforge widget primitive
// (`src/Harbor.Tui.CellForge/Chat/Widgets/` — BorderPanel, flex, Tree, Table,
// Sparkline, gauge, form controls, overlay layers, diff viewer, which-key are
// the set)". #945 measured the same wall from the other side: seating a panel
// as a layer is "a NEW cellforge widget primitive, which #555 freezes by name".
//
// So the refactor is not merely expensive here, it is out of bounds while the
// freeze holds, and #555 explicitly allows what this file is: architecture
// enforcement, "non-vacuous `Harbor.Architecture.Tests` rules", and docs that
// reduce drift between hand-maintained lists and the code.
//
// What is pinned instead is the shape, so the decision #473 has to make is
// taken against measurements rather than against prose written before the
// per-kind handlers existed:
//
//   * which kinds each of the two key paths dispatches,
//   * which kinds already have a dedicated handler,
//   * which kinds product code can actually show,
//   * which case labels the two paths share, and which only the kitty path has.
//
// The last row is the one that stops the destructive move: someone reading
// "248 lines of identical logic" is invited to delete one of the two paths,
// and the ten key cases that exist only on the kitty path are what that
// deletion would take.
//
// WHAT THIS GATE CANNOT SEE — measured, not assumed
// -------------------------------------------------
//   1. IT CANNOT COUNT CONTROL FLOW. Everything here is a name-presence scan:
//     "does `DialogKind.Approval` occur in this method body", not "how many
//     branches does this method take". So a rewrite that moves dispatch into a
//     helper method would empty a body and read as "this kind is no longer
//     handled", while behaviour is unchanged. The Alert row above is exactly
//     that situation already: absent from both paths, and correct.
//
//   2. IT CANNOT SEE A KEY PATH THAT ISN'T NAMED `HandleKey`. Adding a third
//     overload leaves both recorded bodies untouched and this census green.
//
//   3. `HasDedicatedHandler` MATCHES THE METHOD NAME, not its contents, so a
//     `HandleSelectKey` that stopped switching on kind would still count. The
//     behaviour of those handlers is graded by `DialogKeyParityTests` in
//     `Harbor.Tui.CellForge.Tests`, not here — this file does not duplicate it.
//
//   4. `ShownInProduct` SCANS `src/` + `apps/` only (`SourceScan`), so a
//     dialog shown exclusively from `tests/` reads as unreachable, which is the
//     intent, and a `Show*` reached through reflection would not be seen.
//
// NON-VACUITY
// -----------
// Discovery returning nothing would make the table comparison pass vacuously:
// every kind would read "absent from both paths, no handler, not shown", and
// the baselines that expect five kinds on both paths would then fail — but a
// broken enum parse would fail for the wrong reason and teach nothing. So the
// enum parse is separately pinned to all seven members, both method bodies are
// separately pinned as located, and `Control_TheScannerCanSeeAKindThatIsNotThere`
// runs the SAME scanner over synthetic source to prove it can both find a kind
// that is present and miss one that is absent.

using System.Linq;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One measured fact about one <c>DialogKind</c>: where it is dispatched and
///     whether product code can reach it at all.
/// </summary>
/// <param name="Name">The <c>DialogKind</c> member name.</param>
/// <param name="OnLegacyConsolePath">
///     Whether <c>DialogKind.<paramref name="Name"/></c> appears inside the
///     <c>HandleKey(ConsoleKeyInfo)</c> body — the BCL path.
/// </param>
/// <param name="OnKittyPath">
///     Whether it appears inside the <c>HandleKey(in KeyEvent)</c> body — the
///     kitty-capable-host path.
/// </param>
/// <param name="HasDedicatedHandler">
///     Whether a <c>private bool Handle&lt;Name&gt;Key(</c> method exists, i.e. the
///     per-kind split is already done for this kind on the legacy path.
/// </param>
/// <param name="ShownInProduct">
///     Whether a <c>.Show&lt;Name&gt;(</c> call site exists in <c>src/</c> or
///     <c>apps/</c>. False means the kind is reachable from tests only.
/// </param>
internal readonly record struct DialogKindCensus(
    string Name,
    bool OnLegacyConsolePath,
    bool OnKittyPath,
    bool HasDedicatedHandler,
    bool ShownInProduct)
{
    /// <summary>The readable form used in failure messages.</summary>
    /// <returns>The kind name.</returns>
    public override string ToString() => Name;
}

/// <summary>
///     The key-case overlap between the two <c>HandleKey</c> paths: which case
///     labels they share, and which exist on only one of them.
/// </summary>
/// <param name="Shared">Case labels both paths switch on, normalised to bare names.</param>
/// <param name="LegacyOnly">Case labels only the <c>ConsoleKeyInfo</c> path switches on.</param>
/// <param name="KittyOnly">Case labels only the <c>in KeyEvent</c> path switches on.</param>
internal readonly record struct DialogKeyPathOverlap(
    IReadOnlyList<string> Shared,
    IReadOnlyList<string> LegacyOnly,
    IReadOnlyList<string> KittyOnly);

/// <summary>
///     The scanner behind <see cref="DialogKeyDispatchCensusRule"/>. Internal
///     rather than private because the non-vacuity control must exercise the SAME
///     matcher the rule uses, and a control that ran a copy could pass while the
///     rule matched nothing.
/// </summary>
internal static class DialogKeyDispatchProbe
{
    /// <summary>The dialog kinds the census is about.</summary>
    internal const string KindEnumName = "DialogKind";

    /// <summary>The enum declaration, captured so member parsing is anchored to it.</summary>
    internal static readonly Regex KindEnumDeclaration =
        new(@"public\s+enum\s+" + KindEnumName + @"\b", RegexOptions.Compiled);

    /// <summary>
    ///     The legacy BCL key path. Anchored on the parameter type because the
    ///     method name is overloaded — both paths are called <c>HandleKey</c>.
    /// </summary>
    internal static readonly Regex LegacyKeyPathSignature =
        new(@"HandleKey\s*\(\s*ConsoleKeyInfo\s+\w+\s*\)", RegexOptions.Compiled);

    /// <summary>The kitty-capable-host key path, distinguished by its <c>in KeyEvent</c> parameter.</summary>
    internal static readonly Regex KittyKeyPathSignature =
        new(@"HandleKey\s*\(\s*in\s+KeyEvent\s+\w+\s*\)", RegexOptions.Compiled);

    /// <summary>A per-kind key handler, capturing the kind name between <c>Handle</c> and <c>Key</c>.</summary>
    internal static readonly Regex DedicatedHandler =
        new(@"private\s+bool\s+Handle(?<kind>\w+)Key\s*\(", RegexOptions.Compiled);

    /// <summary>A product-side factory call. The leading dot is what separates a call from a declaration.</summary>
    internal static readonly Regex ShowCall =
        new(@"\.(?<kind>\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>A <c>switch</c> case label, capturing the bare enum member name.</summary>
    internal static readonly Regex CaseLabel =
        new(@"case\s+[\w\.]*\b(?<name>\w+)\s*:", RegexOptions.Compiled);

    /// <summary>
    ///     One enum member: the first identifier of a comma-separated entry, after
    ///     any per-member attributes. Written as "first identifier of a comma part"
    ///     rather than "any capitalised word in the body" so a member carrying an
    ///     attribute or an XML doc cannot inflate the count with its own type name.
    /// </summary>
    internal static readonly Regex EnumMember =
        new(@"^\s*(?:\[[^\]]*\]\s*)*(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled);

    /// <summary>
    ///     The two key vocabularies are spelled differently on the two paths
    ///     (<c>LeftArrow</c> versus <c>Left</c>). Normalising them is what makes
    ///     "the paths share four cases" a statement about the code rather than
    ///     about the BCL's naming.
    /// </summary>
    internal static string NormalizeCaseLabel(string name) => name switch
    {
        "LeftArrow" => "Left",
        "RightArrow" => "Right",
        "PageUpArrow" => "PageUp",
        "PageDownArrow" => "PageDown",
        _ => name,
    };

    /// <summary>
    ///     Parses the <c>DialogKind</c> members. Returns an empty list rather than
    ///     throwing when the enum is missing, so the caller's non-vacuity assertion
    ///     reports the real problem instead of a parse crash.
    /// </summary>
    internal static IReadOnlyList<string> ScanKinds(string source)
    {
        Match declaration = KindEnumDeclaration.Match(source);
        if (!declaration.Success)
        {
            return [];
        }

        int open = source.IndexOf('{', declaration.Index);
        if (open < 0)
        {
            return [];
        }

        int close = source.IndexOf('}', open);
        if (close < 0)
        {
            return [];
        }

        // Enum members are comma-separated. A nested brace cannot appear in a
        // plain enum body, and SourceScan has already removed comments, so the
        // comma split is the whole parse.
        var names = new List<string>();
        foreach (string part in source[(open + 1)..close].Split(','))
        {
            Match member = EnumMember.Match(part);
            if (member.Success && !names.Contains(member.Groups["name"].Value, StringComparer.Ordinal))
            {
                names.Add(member.Groups["name"].Value);
            }
        }

        return names;
    }

    /// <summary>
    ///     Extracts a method body by brace-matching from its signature. Returns
    ///     <see langword="null"/> when the signature is absent, which the caller
    ///     must treat as a finding rather than as an empty method.
    /// </summary>
    internal static string? TryMethodBody(string source, Regex signature)
    {
        Match match = signature.Match(source);
        if (!match.Success)
        {
            return null;
        }

        int open = source.IndexOf('{', match.Index + match.Length);
        if (open < 0)
        {
            return null;
        }

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     One census row per dialog kind, from the comment-stripped overlay source
    ///     plus the comment-stripped product sources used for reachability.
    /// </summary>
    /// <param name="overlay">Comment-stripped <c>DialogOverlay.cs</c> text.</param>
    /// <param name="productSources">
    ///     Comment-stripped product sources, as repo-relative path / text.
    /// </param>
    internal static IReadOnlyList<DialogKindCensus> Scan(
        string overlay,
        IReadOnlyList<(string Path, string Text)> productSources)
    {
        string? legacy = TryMethodBody(overlay, LegacyKeyPathSignature);
        string? kitty = TryMethodBody(overlay, KittyKeyPathSignature);

        var handlers = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DedicatedHandler.Matches(overlay))
        {
            handlers.Add(match.Groups["kind"].Value);
        }

        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string _, string text) in productSources)
        {
            foreach (Match match in ShowCall.Matches(text))
            {
                shown.Add(match.Groups["kind"].Value);
            }
        }

        var rows = new List<DialogKindCensus>();
        foreach (string kind in ScanKinds(overlay))
        {
            string qualified = KindEnumName + "." + kind;
            rows.Add(new DialogKindCensus(
                kind,
                Mentions(legacy, qualified),
                Mentions(kitty, qualified),
                handlers.Contains(kind),
                shown.Contains(kind)));
        }

        return rows;
    }

    /// <summary>
    ///     Which case labels the two key paths share. A missing body contributes
    ///     nothing, so a renamed path empties one side and the overlap assertion
    ///     goes red instead of quietly reporting "no duplicates".
    /// </summary>
    /// <param name="overlay">Comment-stripped <c>DialogOverlay.cs</c> text.</param>
    internal static DialogKeyPathOverlap ScanOverlap(string overlay)
    {
        IReadOnlyList<string> legacy = CaseLabels(TryMethodBody(overlay, LegacyKeyPathSignature));
        IReadOnlyList<string> kitty = CaseLabels(TryMethodBody(overlay, KittyKeyPathSignature));

        var legacySet = new HashSet<string>(legacy, StringComparer.Ordinal);
        var kittySet = new HashSet<string>(kitty, StringComparer.Ordinal);

        return new DialogKeyPathOverlap(
            legacy.Where(kittySet.Contains).Order(StringComparer.Ordinal).ToList(),
            legacy.Where(label => !kittySet.Contains(label)).Order(StringComparer.Ordinal).ToList(),
            kitty.Where(label => !legacySet.Contains(label)).Order(StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<string> CaseLabels(string? body)
    {
        if (body is null)
        {
            return [];
        }

        return CaseLabel.Matches(body)
            .Select(static m => NormalizeCaseLabel(m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool Mentions(string? body, string qualified) =>
        body is not null && body.Contains(qualified, StringComparison.Ordinal);
}

/// <summary>
///     Pins the measured shape of <c>DialogOverlay</c>'s key dispatch, so the split
///     #473 has to decide is taken against numbers rather than against the
///     pre-split prose in #472. See the file header for the measurements and for
///     what this gate cannot see.
/// </summary>
public sealed class DialogKeyDispatchCensusRule
{
    private const string OverlayPath = "src/Harbor.Tui.CellForge/Chat/Widgets/DialogOverlay.cs";

    /// <summary>The seven kinds the overlay declares.</summary>
    private static readonly string[] AllKinds =
    [
        "Alert", "Confirm", "Prompt", "Select", "Radio", "Multiline", "Approval",
    ];

    /// <summary>
    ///     Kinds whose name appears in the <c>HandleKey(ConsoleKeyInfo)</c> body.
    ///     Alert and Confirm are absent: neither switches on kind, so both fall to
    ///     the <c>_ =&gt; false</c> arm of the trailing <c>_kind switch</c>.
    /// </summary>
    private static readonly string[] DispatchedOnLegacyConsolePath =
    [
        "Prompt", "Select", "Radio", "Multiline", "Approval",
    ];

    /// <summary>
    ///     Kinds whose name appears in the <c>HandleKey(in KeyEvent)</c> body — the
    ///     same five. The flat matrix covers the five kinds that take text or a
    ///     selection; the two that do not (Alert, Confirm) reach only the
    ///     unconditional Escape/Tab prefix.
    /// </summary>
    private static readonly string[] DispatchedOnKittyPath =
    [
        "Prompt", "Select", "Radio", "Multiline", "Approval",
    ];

    /// <summary>
    ///     The kinds that already have their own <c>Handle&lt;Kind&gt;Key</c>
    ///     method. This is the per-kind split #473 asks for, already half done —
    ///     and it is only reachable from the legacy path, because the kitty path
    ///     inlines its matrix instead of delegating.
    /// </summary>
    private static readonly string[] HaveDedicatedHandler =
    [
        "Prompt", "Select", "Radio", "Approval", "Multiline",
    ];

    /// <summary>
    ///     The kinds product code can actually show. Both are in
    ///     <c>Onboarding/OnboardingFlow.cs</c>. Alert is the row that matters: it
    ///     is product-reachable and dispatched on neither key path.
    /// </summary>
    private static readonly string[] ShownByProduct =
    [
        "Alert", "Prompt",
    ];

    /// <summary>
    ///     Case labels both key paths switch on. Four of fourteen: the two paths
    ///     share a prefix and then diverge, which is the fact #472's "248 lines for
    ///     identical logic" gets backwards.
    /// </summary>
    private static readonly string[] SharedKeyCases =
    [
        "Escape", "Left", "Right", "Tab",
    ];

    /// <summary>
    ///     Case labels only the <c>in KeyEvent</c> path switches on. Deleting that
    ///     path as "the duplicate" takes all ten of these with it.
    /// </summary>
    private static readonly string[] KittyOnlyKeyCases =
    [
        "Backspace", "Char", "Delete", "Down", "End", "Home", "PageDown", "PageUp", "Up",
    ];

    private static readonly Lazy<string> OverlayText = new(() =>
        SourceScan.TryReadAllText(OverlayPath) is { } source
            ? SourceScan.StripComments(source)
            : string.Empty);

    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> ProductSources = new(Read);

    private static readonly Lazy<IReadOnlyList<DialogKindCensus>> Census =
        new(() => DialogKeyDispatchProbe.Scan(OverlayText.Value, ProductSources.Value));

    private static readonly Lazy<DialogKeyPathOverlap> Overlap =
        new(() => DialogKeyDispatchProbe.ScanOverlap(OverlayText.Value));

    private static IReadOnlyList<(string Path, string Text)> Read()
    {
        var read = new List<(string, string)>();
        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(file) is { } source)
            {
                read.Add((file, SourceScan.StripComments(source)));
            }
        }

        return read;
    }

    /// <summary>
    ///     Non-vacuity, part one: the scanner found the file, found the enum, and
    ///     found seven members. If the path is renamed or the enum is re-expressed,
    ///     every other assertion in this file would compare an empty row set against
    ///     a five-element baseline and fail for the wrong reason — or, if the
    ///     baselines were ever emptied to match, pass while enforcing nothing.
    /// </summary>
    [Test]
    public async Task TheScannerFindsTheOverlayTheEnumAndBothKeyPaths()
    {
        await Assert.That(OverlayText.Value).IsNotEqualTo(string.Empty).Because(
            "this census reads one file by path. An empty read means every row below is a "
            + "vacuous pass, so the path is asserted before anything is measured.");

        IReadOnlyList<string> kinds = DialogKeyDispatchProbe.ScanKinds(OverlayText.Value);
        await Assert.That(kinds.ToArray()).IsEquivalentTo(AllKinds).Because(
            "seven dialog kinds are declared, and the census is one row per kind. A new "
            + "kind, a removed kind or a re-expressed enum is an explicit edit here rather "
            + "than a row that silently stops existing. Measured: " + Describe(kinds));

        await Assert.That(
            DialogKeyDispatchProbe.TryMethodBody(OverlayText.Value, DialogKeyDispatchProbe.LegacyKeyPathSignature))
            .IsNotNull().Because(
            "the legacy ConsoleKeyInfo overload is one of the two paths being compared. It is "
            + "distinguished from its kitty twin by parameter type, not by name, because both "
            + "are called HandleKey.");

        await Assert.That(
            DialogKeyDispatchProbe.TryMethodBody(OverlayText.Value, DialogKeyDispatchProbe.KittyKeyPathSignature))
            .IsNotNull().Because(
            "the in KeyEvent overload is the path that carries the 31-cell dispatch matrix, and "
            + "the ten key cases that exist only here.");
    }

    /// <summary>
    ///     The census equals the measured baseline, in both directions. A kind that
    ///     starts being dispatched, or stops being, has to be edited into the table
    ///     first — which is the point: the decision #473 has to make cannot be made
    ///     twice against two different sets of numbers.
    /// </summary>
    [Test]
    public async Task TheDialogKindCensusIsExactlyWhatTheBaselineRecords()
    {
        IReadOnlyList<DialogKindCensus> rows = Census.Value;

        string[] onLegacy = Names(rows.Where(static r => r.OnLegacyConsolePath));
        string[] onKitty = Names(rows.Where(static r => r.OnKittyPath));
        string[] withHandler = Names(rows.Where(static r => r.HasDedicatedHandler));
        string[] shown = Names(rows.Where(static r => r.ShownInProduct));

        await Assert.That(onLegacy).IsEquivalentTo(DispatchedOnLegacyConsolePath).Because(
            "five of the seven kinds switch on DialogKind in the ConsoleKeyInfo path. Alert and "
            + "Confirm do not, and fall through to the `_ => false` arm of the trailing kind "
            + "switch — which is why a product-shown Alert handles no kind-specific key at all. "
            + "Measured: " + Describe(onLegacy));

        await Assert.That(onKitty).IsEquivalentTo(DispatchedOnKittyPath).Because(
            "the same five kinds are dispatched by the in KeyEvent matrix. The two missing ones "
            + "are not an oversight to be fixed here: Alert and Confirm take no key that is not "
            + "already handled unconditionally above the switch, and inventing a handler for "
            + "them is a behaviour decision, not a measurement. Measured: " + Describe(onKitty));

        await Assert.That(withHandler).IsEquivalentTo(HaveDedicatedHandler).Because(
            "five per-kind Handle<Kind>Key methods already exist, which is the per-kind split "
            + "#473 asks for half performed. #472's 'five unrelated responsibilities' framing "
            + "counts as unsplit work something that has already been split out. Measured: "
            + Describe(withHandler));

        await Assert.That(shown).IsEquivalentTo(ShownByProduct).Because(
            "ONLY Alert and Prompt are reachable from product code (both in "
            + "OnboardingFlow.cs). Confirm, Select, Radio, Multiline and Approval have no .Show*( "
            + "call site in src/ or apps/ and are reachable from tests only. This is the fact "
            + "that bounds the cost of adding an eighth kind, and the reason a split has to "
            + "decide what happens to five kinds no product path reaches. Measured: "
            + Describe(shown));
    }

    /// <summary>
    ///     The two key paths are not duplicates. Four shared case labels; ten exist
    ///     only on the kitty path; none exist only on the legacy path.
    /// </summary>
    [Test]
    public async Task TheTwoKeyPathsShareFourCasesAndTheKittyPathHasTenOfItsOwn()
    {
        DialogKeyPathOverlap overlap = Overlap.Value;

        await Assert.That(overlap.Shared.ToArray()).IsEquivalentTo(SharedKeyCases).Because(
            "Escape, Tab, Left and Right are the shared prefix. #472 calls the two handlers "
            + "'248 lines for identical logic'; measured, they share four case labels and "
            + "diverge completely after them.");

        await Assert.That(overlap.KittyOnly.ToArray()).IsEquivalentTo(KittyOnlyKeyCases).Because(
            "these ten key cases exist ONLY on the in KeyEvent path — Up, Down, PageUp, "
            + "PageDown, Home, End, Backspace, Delete, Char, and the modifier-aware Enter that "
            + "implements the composer's Shift/Alt+Enter newline split. This row is the one "
            + "that makes the 'duplicate' reading dangerous: collapsing the kitty path into "
            + "the legacy one deletes all ten behaviours. Measured: " + Describe(overlap.KittyOnly));

        await Assert.That(overlap.LegacyOnly.ToArray()).IsEquivalentTo(Array.Empty<string>()).Because(
            "the legacy path has no case the kitty path lacks. The divergence is entirely "
            + "one-directional, which is what a shared-prefix-plus-different-vocabulary pair "
            + "looks like and what two interchangeable copies do not look like.");
    }

    /// <summary>
    ///     The reachability claim is measured, not inferred from the enum: the two
    ///     product-shown kinds have real call sites, and the five test-only ones do
    ///     not.
    /// </summary>
    [Test]
    public async Task OnlyTwoDialogKindsHaveAProductShowCallSite()
    {
        IReadOnlyList<(string Path, string Text)> sources = ProductSources.Value;

        var alert = new List<string>();
        var prompt = new List<string>();
        var confirm = new List<string>();
        foreach ((string path, string text) in sources)
        {
            if (text.Contains(".ShowAlert(", StringComparison.Ordinal))
            {
                alert.Add(SourceScan.Relative(path));
            }

            if (text.Contains(".ShowPrompt(", StringComparison.Ordinal))
            {
                prompt.Add(SourceScan.Relative(path));
            }

            if (text.Contains(".ShowConfirm(", StringComparison.Ordinal))
            {
                confirm.Add(SourceScan.Relative(path));
            }
        }

        await Assert.That(Distinct(alert).ToArray()).IsEquivalentTo(new[] { "src/Harbor.Tui.CellForge/Chat/Onboarding/OnboardingFlow.cs" }).Because(
            "ShowAlert is the live half of the product-reachable set, and it is reached only "
            + "from the onboarding flow's own DialogOverlay instance — not the one "
            + "ChatScreenLayout seats on the overlay stack. Measured: " + Describe(alert));

        await Assert.That(Distinct(prompt).ToArray()).IsEquivalentTo(new[] { "src/Harbor.Tui.CellForge/Chat/Onboarding/OnboardingFlow.cs" }).Because(
            "ShowPrompt is the other half, same file. Four call sites, all onboarding. "
            + "Measured: " + Describe(prompt));

        await Assert.That(confirm).IsEmpty().Because(
            "ShowConfirm has NO product call site. The first Show* a product path gains for "
            + "Confirm, Select, Radio, Multiline or Approval is the moment this census stops "
            + "describing the live surface, and it should be an explicit edit to the baseline "
            + "rather than a silent widening of what ships. A green run here is the early "
            + "warning, not the proof that the rest are unreachable.");
    }

    // ── Non-vacuity controls ────────────────────────────────────────────────
    // Every rule above compares a measurement against a recorded baseline. A
    // comparator that always found the same thing, or a scanner that never found
    // anything, would satisfy all of them. These two run the SAME matcher over
    // synthetic source so both directions are demonstrated.

    /// <summary>
    ///     The scanner can see a kind that IS there and miss one that is NOT, on
    ///     source it has never seen. Without this, "Alert is dispatched nowhere"
    ///     could mean "the matcher finds nothing anywhere".
    /// </summary>
    [Test]
    public async Task Control_TheScannerCanSeeAKindThatIsThereAndMissOneThatIsNot()
    {
        const string synthetic = """
            public enum DialogKind { Alpha, Beta }

            public sealed class DialogOverlay
            {
                public bool HandleKey(ConsoleKeyInfo key)
                {
                    if (_kind == DialogKind.Beta) { return true; }
                    return false;
                }

                public bool HandleKey(in KeyEvent key)
                {
                    return false;
                }

                private bool HandleBetaKey(ConsoleKeyInfo key) => false;
            }
            """;

        IReadOnlyList<DialogKindCensus> rows =
            DialogKeyDispatchProbe.Scan(synthetic, [("synthetic.cs", synthetic)]);

        DialogKindCensus alpha = rows.Single(static r => r.Name == "Alpha");
        DialogKindCensus beta = rows.Single(static r => r.Name == "Beta");

        await Assert.That(beta.OnLegacyConsolePath).IsTrue().Because(
            "Beta is named in the synthetic legacy body, so the matcher must find it. If it "
            + "cannot, every 'dispatched on this path' row in this file is a false negative.");

        await Assert.That(beta.HasDedicatedHandler).IsTrue().Because(
            "the synthetic source declares HandleBetaKey, so the dedicated-handler matcher "
            + "must find it too — the same both-directions proof for the second matcher.");

        await Assert.That(alpha.OnLegacyConsolePath).IsFalse().Because(
            "Alpha is declared but never dispatched. The scanner must be able to say 'no', or "
            + "the Alert row — the sharpest finding in this census — is unfalsifiable.");

        await Assert.That(beta.OnKittyPath).IsFalse().Because(
            "the synthetic kitty body mentions no kind at all, so the kitty column must read "
            + "false even where the legacy column reads true. The two columns are measured "
            + "separately and this proves they are not the same test.");

        await Assert.That(rows.Count(static r => r.ShownInProduct)).IsEqualTo(0).Because(
            "the synthetic source contains no Show*( call on a receiver, so reachability must "
            + "read false for both kinds — the leading dot is what keeps a declaration from "
            + "counting as a call.");
    }

    /// <summary>
    ///     A missing method body reads as "no cases found", not as "no duplicates
    ///     found". If it instead threw or defaulted to a full overlap, a renamed path
    ///     would report the paths as identical — the exact false conclusion #472 made.
    /// </summary>
    [Test]
    public async Task Control_AMissingKeyPathBodyIsReportedAsMissingNotAsAgreement()
    {
        const string noKittyPath = """
            public enum DialogKind { Alpha }
            public sealed class DialogOverlay
            {
                public bool HandleKey(ConsoleKeyInfo key)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Escape: return true;
                        case ConsoleKey.UpArrow: return true;
                    }
                    return false;
                }
            }
            """;

        await Assert.That(
            DialogKeyDispatchProbe.TryMethodBody(noKittyPath, DialogKeyDispatchProbe.KittyKeyPathSignature))
            .IsNull().Because(
            "the assertion the rule makes about the kitty path must be able to observe its "
            + "absence; returning an empty body here would make the method 'found and empty'.");

        DialogKeyPathOverlap overlap = DialogKeyDispatchProbe.ScanOverlap(noKittyPath);
        await Assert.That(overlap.Shared.ToArray()).IsEmpty().Because(
            "with one path absent there is nothing to share. The failure mode this controls "
            + "is the opposite one — reporting agreement when a path could not be read.");

        await Assert.That(overlap.KittyOnly.ToArray()).IsEmpty().Because(
            "an unreadable path contributes no cases, so the 'ten cases exist only on the "
            + "kitty path' row would go red rather than pass. That is the correct direction: "
            + "the census is supposed to notice, not to shrug.");
    }

    private static string[] Names(IEnumerable<DialogKindCensus> rows) =>
        rows.Select(static r => r.Name).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string Describe(IEnumerable<DialogKindCensus> rows) =>
        Describe(rows.Select(static r => r.Name));

    private static string Describe(IEnumerable<string> names)
    {
        string[] list = names.Order(StringComparer.Ordinal).ToArray();
        return list.Length == 0 ? "(none)" : string.Join(", ", list);
    }
}