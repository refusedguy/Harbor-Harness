// PanelKeyRouteReachabilityRule.cs — GUARD for issue #857 (and its parent #812):
// a key hand-off must be reachable from product code, and the tests that drive
// an unreachable hand-off are not evidence of anything.
//
// THE DEFECT, AS FOUND
// --------------------
// The panel and overlay planes hand a key to a consumer by CALLING a method on
// it: `IPanelProvider.OnKey` and `IOverlayLayer.OnKey`. The whole product tree
// contains exactly THREE such hand-offs, and none of them can be entered:
//
//   OverlayStack.RouteKey
//       `if (layer.OnKey(in key))` — inside `OverlayStack.RouteKey`, which no
//       product method calls. (#812)
//   CellForgePanelAdapter.RouteKey
//       `return provider.OnKey(key, ctx);` — inside
//       `CellForgePanelAdapter.RouteKey`, whose only caller in the whole tree is
//       `ChatScreenPanelDock.RoutePanelKey`.
//   CellForgeJumpPaletteOverlayLayer.OnKey
//       `return _panel.OnKey(` — inside that layer's own `OnKey`, and the layer
//       is never constructed outside tests.
//
// And `ChatScreenPanelDock.RoutePanelKey` has NO caller either. So the chain is
// two links long and both ends are in the air.
//
// WHY THE USER-VISIBLE PART IS SMALLER THAN "7 BROKEN PANELS"
// -----------------------------------------------------------
// The blast radius reads as seven because `IPanelProvider.OnKey` is a REQUIRED
// interface member (`IPanelProvider.cs`, `OnKey`) and seven providers implement it.
// Measured by what the key actually does, it is smaller and stranger:
//
//   * `CellForgeLogsPanel.OnKey` handles F12 by dispatching
//     `AppMsg.TogglePanel("logs")` — and F12 is ALREADY bound globally to
//     `ChatAction.ToggleLogsPanel` (`ChatKeyMap._entries`), which the reducer
//     applies as the same `TogglePanel(state, "logs")` (`AppReducer.UpdateKeyGeneric`).
//     The dead override is a byte-for-byte duplicate of a live hotkey.
//   * `CellForgeHelpPanel.OnKey` handles '?' the same way, and '?' is already
//     bound to `ChatAction.HelpPanel` → `TogglePanel(state, "help")`
//     (`ChatKeyMap._entries`, `AppReducer.UpdateKeyGeneric`).
//   * `CellForgeSkillFreshnessPanel.OnKey` is `=> false` — it wants nothing.
//   * That leaves `CellForgeFileTreePanel` (j/k/h/l + Enter navigation),
//     `CellForgeDiagnosticsPanel` (j/k cursor) and `CellForgeSubagentsPanel`
//     (cursor / transcript / refresh) as the only three whose `OnKey` carries
//     capability the global keymap does not already have — plus
//     `CellForgeJumpPalettePanel`, which is keyed AND unpainted, because
//     `DefaultPlacement => Center` sends the dock home early
//     (`ChatScreenPanelDock.AttachPanels`) and the `IOverlayLayer` meant to paint it
//     is constructed by nothing but a test.
//
// So the honest count is "three panels with unreachable input, one hotkey that
// toggles state nobody draws", not "seven broken panels". This rule does not
// adjudicate that — see WHY THIS RULE DOES NOT DECIDE #857 below.
//
// WHY THE GREEN TEST IS THE ACTUAL FINDING
// ----------------------------------------
// `PanelWiringTests.RouteKey_Logs_F12_Toggles_Panel` is green. It is also
// worthless, and its shape is worth naming because it is a shape, not a slip:
//
//   * it calls `ChatScreenPanelDock.RoutePanelKey` DIRECTLY, so the missing link
//     in the chain above is skipped by construction;
//   * it dispatches `AppMsg.FocusPanel("logs")` by hand first, because
//     `RoutePanelKey` returns early unless the panel is `Focused`
//     (`ChatScreenPanelDock.RoutePanelKey`) and the product's F12 path only ever
//     reaches `Visible` — `AppReducer.TogglePanel` toggles
//     Hidden↔Visible and never sets `FocusedPanelId`. NO product host dispatches
//     `AppMsg.FocusPanel` with a non-null id; the one non-test dispatch is
//     `SpectreTuiRenderer.ChatScreen.OnKeyMessage`, and
//     `contrib/` is unbuilt.
//
// The test manufactures the precondition the product never reaches, then asserts
// on it. A test that is the SOLE caller of the method it exercises cannot fail
// when the product path is absent, which is the failure mode this file exists to
// make impossible to keep.
//
// WHAT IS RULED
// -------------
// For every hand-off — a call of the form `X.OnKey(` in a product tree — the
// method that PERFORMS it must be called from a product method that is not
// itself a hand-off performer, and that calling method must itself be called
// from somewhere in the product tree. One hop is not enough to tell "wired" from
// "wired into another dead router", which is exactly the shape #857 has
// (`RoutePanelKey` → `RouteKey` → `OnKey`, both links in the air), and that is
// why the rule asks for two.
//
// WHAT IS DELIBERATELY *NOT* RULED
// --------------------------------
// This rule does not decide whether #857 should be FIXED or DELETED, and it is
// built so that either resolution turns it green without weakening it:
//
//   * WIRING (a host calls the router) — the second hop appears and the hand-off
//     is reachable.
//   * DELETING (the routers and the `OnKey` implementations go) — there is no
//     hand-off left to rule on.
//
// That is the point of matching the hand-off BY FORM (`X.OnKey(`, read out of
// the source) rather than by any file list or member name. The same property
// #821's third rule relies on: the set is re-derived from the tree, so the edit
// that resolves the defect cannot also be the edit that satisfies the guard.
//
// Because both resolutions are the OWNER's — #857 asks whether
// `IPanelProvider.OnKey` should be wired or deleted, #812 asks the same of the
// `OverlayStack` host-routing API, and both are product calls — the two
// known-dead routers sit in `PanelKeyRouteProbe.Ledger` with the
// issue that owns each, and the gate fails only on an UNDECLARED third. The
// ledger ratchets both ways: an entry whose router stops being an orphan fails
// until it is removed, so debt is retired by an explicit edit and never by
// quietly ceasing to apply.
//
// HONEST LIMITS OF A SOURCE SCAN
// ------------------------------
// Stated here because a guard that hides its blind spots is how this defect
// class got here twice already:
//
//   1. Identity is (file, name), because two distinct methods in this tree are
//     both called `RouteKey`. A call qualified by a TYPE resolves through a
//     type→file map; anything else — a call on an expression, or a bare call in
//     another file — cannot be attributed, and is treated as REACHABLE. The rule
//     fails OPEN on what it cannot parse and fails CLOSED only on what it can.
//   2. Consequence, stated rather than hidden: the third hand-off above
//     (`CellForgeJumpPaletteOverlayLayer.OnKey`) is NOT among the orphans this
//     rule reports, because eight types in this tree implement `OnKey` and a
//     caller spelled `x.OnKey(` cannot be told apart by name. The rule reports
//     the two routers it CAN place, and does not pretend about the third.
//   3. It counts hops out from the hand-off, not from the process entry point,
//     so it does not prove a chain ends at `Main` — only that it is entered by
//     called product code two hops out. Requiring more would mean resolving entry
//     points and delegates, which this scan cannot do soundly.
//   4. `EnclosingMethod` walks upward at most `MaxEnclosingWalk` lines for the
//     nearest declaration, so a hand-off inside a local function or a lambda is
//     attributed to the enclosing named method. That direction is fail-open for
//     the ruled property.
//   5. It cannot see the test tree through `SourceScan`, which rejects `/tests/`.
//     The measurement therefore walks `tests/` itself; see `ReadTestTrees`.
//
// NON-VACUITY
// -----------
// A gate that finds nothing is indistinguishable from a gate that is satisfied,
// and a gate whose regex silently stopped matching is worse than no gate. Five
// things close that, and the third exists because this file's own first run got
// it wrong:
//
//   1. `NonVacuity_TheRealTreeStillExposesTheHandOffSeam` — a FLOOR on what the
//      scan found in the real product tree, so a moved file, a renamed method or
//      a broken regex is red here rather than vacuously green.
//   2. `NonVacuity_AWiredHandOffIsReachableAndAnOrphanIsNot` — the positive and
//      the negative control, handed SYNTHETIC sources (types that appear nowhere
//      in this tree) so they exercise the same scanner the rule uses and not a
//      copy of it. Both directions are asserted in one test, so the gate is shown
//      to discriminate rather than to answer a constant.
//   3. `NonVacuity_TheTestTreeIsActuallyVisibleToTheMeasurement` — the first run
//      of this guard reported **0** tests driving a dead router, because
//      `SourceScan.EnumerateCsFiles("tests")` is empty BY CONSTRUCTION. A
//      measurement that cannot see its subject reports a healthy zero, which is
//      the exact shape of the defect it was written to measure. This floor is the
//      fix for that class of blindness, and the reason the walker is local.
//   4. `TestsDrivingAnUnreachableRouterAreNamedAndCounted` — the measurement the
//      issue asks for, ratcheted at the measured value so it can only fall by an
//      explicit edit.
//   5. `NonVacuity_TheRepoRootWasFound` — a guard that cannot see the tree
//      reports zero hand-offs and reads as clean.

using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One place in a product tree that hands a key to a consumer that declares it consumes keys.</summary>
/// <param name="File">Repo-relative source file the hand-off sits in.</param>
/// <param name="Line">1-based line of the hand-off.</param>
/// <param name="Receiver">The expression the key is handed to.</param>
/// <param name="Performer">Name of the method the hand-off sits in — the method the rule judges.</param>
internal readonly record struct KeyHandOff(string File, int Line, string Receiver, string Performer);

/// <summary>A method in the product tree, identified the way a source scan can: by file and by name.</summary>
/// <param name="File">Repo-relative source file declaring it.</param>
/// <param name="Name">Its declared name.</param>
internal readonly record struct ProductMethod(string File, string Name)
{
    /// <summary>The readable form used in failure messages.</summary>
    /// <returns><c>file:Name</c>.</returns>
    public override string ToString() => $"{File}:{Name}";
}

/// <summary>One hand-off the rule could not find a way in to.</summary>
/// <param name="HandOff">The hand-off that cannot be reached.</param>
/// <param name="Verdict">Why, in words a reader can act on.</param>
/// <param name="Chain">
///     Every method name on the dead chain, performer first — the performer plus
///     the non-performer callers it has, which is what a test drives directly and
///     therefore what the measurement has to search for.
/// </param>
internal readonly record struct OrphanHandOff(KeyHandOff HandOff, string Verdict, IReadOnlyList<string> Chain);

/// <summary>Everything one scan of the product trees learned.</summary>
/// <param name="FilesScanned">How many product <c>*.cs</c> files were read.</param>
/// <param name="HandOffs">Every hand-off found, in scan order.</param>
/// <param name="Orphans">The subset of <paramref name="HandOffs" /> no product code can reach.</param>
internal sealed record KeyRouteReport(
    int FilesScanned,
    IReadOnlyList<KeyHandOff> HandOffs,
    IReadOnlyList<OrphanHandOff> Orphans);

/// <summary>
///     What one scan of the product trees cost, split into its phases, and how
///     much work each phase did.
/// </summary>
/// <remarks>
///     <para>
///         The rule's class span is measured on the CI runner's TRX, and a span
///         is a WALL number: it says the rule held the shard for that long, and
///         it does not say which part of the rule did it. These phases are the
///         decomposition, and they are recorded INSIDE the scan — in the thread
///         that computed it, under the shard's real contention — so the numbers
///         are the ones the class span is made of and not a clean-room
///         re-measurement of them.
///     </para>
///     <para>
///         The counters are there because a millisecond without a count cannot
///         be acted on: "the member-declaration pattern ran 11 000 times" says
///         which line of the rule to change, and "the scan took 50 s" does not.
///     </para>
/// </remarks>
/// <param name="ReadMs">How long reading the product trees took.</param>
/// <param name="TypeMapMs">How long building the type→file map took.</param>
/// <param name="HandOffScanMs">How long collecting the hand-off sites took.</param>
/// <param name="CallersMs">How long the reachability passes took.</param>
/// <param name="TotalMs">How long the whole scan took.</param>
/// <param name="Files">How many product files were read.</param>
/// <param name="Lines">How many lines were scanned.</param>
/// <param name="HandOffSites">How many hand-off sites were found.</param>
/// <param name="EnclosingWalks">How many times the enclosing-method walk ran.</param>
/// <param name="CallersPasses">How many times the whole product tree was walked for callers.</param>
/// <param name="MemberMatches">How many times the member-declaration pattern ran.</param>
/// <param name="CallSiteMatches">How many times the call-site pattern ran.</param>
/// <param name="HandOffMatches">How many times the hand-off pattern ran.</param>
internal sealed record ScanPhases(
    long ReadMs,
    long TypeMapMs,
    long HandOffScanMs,
    long CallersMs,
    long TotalMs,
    int Files,
    int Lines,
    int HandOffSites,
    int EnclosingWalks,
    int CallersPasses,
    long MemberMatches,
    long CallSiteMatches,
    long HandOffMatches);

/// <summary>
///     How the scan finds the type declarations of a file.
/// </summary>
/// <remarks>
///     The type→file map is what resolves a call qualified by a type name
///     (<c>Type.Member(</c>) to the file that declares the type, and the map is
///     built by running ONE pattern over each file's text. There are four ways
///     to run it, and they do not cost the same: the whole-file
///     <see cref="System.Text.RegularExpressions.MatchCollection" /> is LAZY,
///     and enumerating it re-enters the pattern engine once per match — which
///     on a <c>^</c>-anchored pattern over a whole file is the most expensive
///     thing this rule does. The strategies are here so the choice is made on a
///     MEASUREMENT, and so the oracle can still be run against the winner.
/// </remarks>
internal enum TypeScanStrategy
{
    /// <summary>
    ///     One whole-file match collection per file, enumerated — the way the
    ///     rule has always built the map.
    /// </summary>
    WholeText,

    /// <summary>
    ///     One match per line. The pattern is anchored per line by design (the
    ///     header says the multiline flag is load-bearing), so this is the same
    ///     shape of scan over the same bytes.
    /// </summary>
    PerLine,

    /// <summary>
    ///     The whole text, matched by hand: one <c>Match(text, position)</c> at
    ///     a time, each starting where the last one ended.
    /// </summary>
    ManualLoop,

    /// <summary>
    ///     The whole text through the span-based enumerator, which yields
    ///     matches without materialising a <c>Match</c> per hit.
    /// </summary>
    EnumerateMatches,
}

/// <summary>
///     One unreachable router the project is carrying ON PURPOSE, and the issue that
///     owns the decision to wire or delete it.
/// </summary>
/// <param name="File">Repo-relative file declaring the router.</param>
/// <param name="Name">The router's name.</param>
/// <param name="OwnedBy">The issue whose decision this entry is waiting on.</param>
internal readonly record struct AcknowledgedDebt(string File, string Name, string OwnedBy)
{
    /// <summary>The identity the scan reports orphans under.</summary>
    /// <returns>The (file, name) pair.</returns>
    internal ProductMethod Method => new(File, Name);
}

/// <summary>
///     The work one scan of the product trees did, counted.
/// </summary>
/// <remarks>
///     A millisecond without a count cannot be acted on: "the member-declaration
///     pattern ran 11 000 times" says which line of the rule to change, and
///     "the scan took fifty seconds" does not. The counters are incremented
///     around the work, never instead of it, so they cannot change a verdict.
/// </remarks>
internal sealed class ScanCounters
{
    /// <summary>How many lines the scan read.</summary>
    internal int Lines;

    /// <summary>How many times the enclosing-method walk ran.</summary>
    internal int EnclosingWalks;

    /// <summary>How many times the whole product tree was walked for callers.</summary>
    internal int CallersPasses;

    /// <summary>How many times the member-declaration pattern ran.</summary>
    internal long MemberMatches;

    /// <summary>How many times the call-site pattern ran.</summary>
    internal long CallSiteMatches;

    /// <summary>How many times the hand-off pattern ran.</summary>
    internal long HandOffMatches;
}

/// <summary>
///     The scanner both the rule and its non-vacuity controls go through, so
///     neither can be satisfied by weakening the other's half. Internal rather
///     than private because the controls must call the SAME code.
/// </summary>
internal static class PanelKeyRouteProbe
{
    /// <summary>
    ///     The seam member a hand-off calls. Spelled once because the two
    ///     consumers — <c>IPanelProvider.OnKey</c> and <c>IOverlayLayer.OnKey</c> —
    ///     agree on it, and the rule is about the CALL, not about either interface.
    /// </summary>
    internal const string SeamMember = "OnKey";

    /// <summary>How far upward <see cref="EnclosingMethod" /> will look for a declaration.</summary>
    internal const int MaxEnclosingWalk = 400;

    /// <summary>
    ///     The phases and the work counters of the LAST scan of the product
    ///     trees, for the measurement to print.
    /// </summary>
    /// <remarks>
    ///     The scan is memoised by the rule, so this is the ONE scan the rule
    ///     did — the one whose wall time the class span on the CI runner is made
    ///     of. A measurement that re-ran the scan would report a clean-room
    ///     number instead, and the difference between the two is the contention
    ///     the rule actually lives under.
    /// </remarks>
    internal static ScanPhases? LastPhases { get; private set; }

    /// <summary>
    ///     Adds the time the product-tree READ took to the phases of the scan
    ///     that followed it.
    /// </summary>
    /// <param name="readMs">How long the read took.</param>
    internal static void RecordReadMs(long readMs)
    {
        if (LastPhases is { } phases)
        {
            LastPhases = phases with { ReadMs = readMs };
        }
    }

    /// <summary>
    ///     The debt ledger: unreachable routers this project is carrying on purpose,
    ///     each with the issue that owns the decision.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is NOT a fix and NOT a decision. #857 asks whether
    ///         <c>IPanelProvider.OnKey</c> should be wired or deleted, and #812 asks
    ///         the same of the <c>OverlayStack</c> host-routing API; both are product
    ///         calls, so neither is the guard's to make. What the ledger does is stop the two known
    ///         dead routers from being indistinguishable from a THIRD one.
    ///     </para>
    ///     <para>
    ///         It ratchets in both directions, and
    ///         <see cref="PanelKeyRouteReachabilityRule.TheAcknowledgedDebtIsExactlyWhatTheScanStillFinds" />
    ///         is what holds the second: an entry that stops being an orphan — because
    ///         the router was wired or deleted — fails until it is removed here. So
    ///         the debt can only be retired by an explicit edit, never by silently
    ///         going away, and a new unreachable router fails
    ///         <see cref="PanelKeyRouteReachabilityRule.EveryKeyHandOffIsReachableFromProductCode" />
    ///         because it has no entry.
    ///     </para>
    /// </remarks>
    internal static readonly AcknowledgedDebt[] Ledger =
    [
        new(
            "src/Harbor.Tui.CellForge/Chat/Rendering/OverlayStack.cs",
            "RouteKey",
            "#812 — the chat's host-routing API (#436 moved it out of the engine with the chat-owned rendering); the two live layers are reached directly by ReplInputLoop"),
        new(
            "src/Harbor.Tui.CellForge/Chat/Panels/CellForgePanelAdapter.cs",
            "RouteKey",
            "#857 — the panel key route; wire it or delete IPanelProvider.OnKey"),
    ];

    /// <summary>Whether the scan's orphan set is exactly the ledger, no more and no less.</summary>
    /// <param name="report">What the product scan found.</param>
    /// <param name="undeclared">Orphans with no ledger entry — the gate's real failure list.</param>
    /// <param name="stale">Ledger entries that are no longer orphans — retired debt not yet removed.</param>
    internal static void PartitionDebt(
        KeyRouteReport report,
        out IReadOnlyList<OrphanHandOff> undeclared,
        out IReadOnlyList<AcknowledgedDebt> stale)
    {
        var acknowledged = Ledger.Select(entry => entry.Method).ToHashSet();

        undeclared = report.Orphans
            .Where(o => !acknowledged.Contains(new ProductMethod(o.HandOff.File, o.HandOff.Performer)))
            .ToList();

        var orphanMethods = report.Orphans
            .Select(o => new ProductMethod(o.HandOff.File, o.HandOff.Performer))
            .ToHashSet();

        stale = Ledger
            .Where(entry => !orphanMethods.Contains(entry.Method))
            .ToList();
    }

    /// <summary>Matches a hand-off: a receiver followed by a call of the seam member.</summary>
    private static readonly Regex HandOffSite =
        new($@"(?<recv>[A-Za-z_]\w*)\s*\.\s*{SeamMember}\s*\(", RegexOptions.Compiled);

    /// <summary>
    ///     Matches a member declaration by shape — an access modifier, optional
    ///     modifiers, a return type, then the name and its parameter list. Never by
    ///     name, so renaming a method cannot defeat it and cannot hide it either.
    /// </summary>
    private static readonly Regex MemberDeclaration =
        new(@"\b(?:public|internal|private|protected)\s+(?:static\s+|sealed\s+|override\s+|virtual\s+|async\s+|new\s+|partial\s+)*[A-Za-z_][\w<>\[\],\.\?\s]*?\s+(?<name>[A-Za-z_]\w*)\s*\(",
            RegexOptions.Compiled);

    /// <summary>Matches any invocation head, for counting calls to a name.</summary>
    private static readonly Regex CallSite =
        new(@"(?<![A-Za-z_0-9])(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>Matches <c>Type.Member(</c> — a call whose receiver names a type.</summary>
    private static readonly Regex TypeQualifiedCall =
        new(@"(?<type>[A-Z][A-Za-z_0-9]*)\s*\.\s*(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>
    ///     Matches a type declaration, for the type-name → declaring-file map.
    ///     Anchored per line, so <see cref="RegexOptions.Multiline" /> is load-bearing
    ///     rather than cosmetic: without it the map stays empty, every
    ///     <c>Type.Member(</c> call stops resolving, and the rule degrades into
    ///     reporting nothing — a green gate over a blind one.
    /// </summary>
    private static readonly Regex TypeDeclaration =
        new(@"^\s*(?:public|internal|private|protected)?\s*(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+|record\s+)*(?:class|struct|interface|enum|record)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    ///     Reads the PRODUCT trees into comment-stripped text. Comments go through
    ///     <see cref="SourceScan.StripComments" />, which preserves line count, so a
    ///     line number still points at real source and the prose in this header —
    ///     which names <c>RouteKey</c> a dozen times — cannot trip the scan.
    /// </summary>
    /// <returns>The snapshot's view of each product file; unreadable files are skipped.</returns>
    internal static IReadOnlyList<SourceTreeFile> ReadProductTrees() =>
        Read(SourceScan.EnumerateCsFiles(SourceScan.ProductTrees));
    /// <summary>
    ///     Reads the TEST tree, which <see cref="SourceScan" /> cannot see at all.
    /// </summary>
    /// <remarks>
    ///     This walker is local on purpose and the reason is worth recording, because
    ///     it is what the first run of this guard proved. <c>SourceScan.IsBuildOutput</c>
    ///     rejects any path containing <c>/tests/</c> — correct for a gate judging
    ///     product source — so <c>SourceScan.EnumerateCsFiles("tests")</c> returns an
    ///     EMPTY list. Reusing it here made the measurement report "0 tests drive a
    ///     dead router" on a tree that has 13, i.e. a blind spot that read as a clean
    ///     result. <c>SourceScan</c> is shared by every guard in this project and
    ///     changing <c>IsBuildOutput</c> for one caller would move all of them, so
    ///     this file walks the tree itself and excludes only build output.
    /// </remarks>
    /// <returns>The snapshot's view of each test file; unreadable files are skipped.</returns>
    internal static IReadOnlyList<SourceTreeFile> ReadTestTrees() => Read(EnumerateTestFiles());

    /// <summary>Every <c>*.cs</c> file under <c>tests/</c>, excluding build output only.</summary>
    /// <returns>Sorted absolute paths.</returns>
    private static List<string> EnumerateTestFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string dir = Path.Combine(root, "tests");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        // The walk itself is the shared snapshot's (#1791); the filter stays
        // this rule's, because the filter is what decides the rule's SCOPE and
        // the snapshot only knows the part every rule agrees on.
        return SharedSourceTreeSnapshot.Shared
            .EnumerateCsFiles(dir)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads the given files through the shared snapshot.</summary>
    /// <param name="paths">Absolute file paths.</param>
    /// <returns>The snapshot's view of each file; unreadable files are skipped.</returns>
    private static List<SourceTreeFile> Read(IReadOnlyList<string> paths)
    {
        var sources = new List<SourceTreeFile>();
        foreach (string path in paths)
        {
            if (SharedSourceTreeSnapshot.Shared.Get(path) is { } file)
            {
                sources.Add(file);
            }
        }

        return sources;
    }

    /// <summary>
    ///     Finds every hand-off and decides which of them product code can reach.
    /// </summary>
    /// <remarks>
    ///     The reachability test is two hops out from the hand-off, for the reason
    ///     in the file header: one hop cannot distinguish a wired router from a
    ///     router wired into another dead one, and that pair is the #857 shape.
    /// </remarks>
    /// <param name="sources">Product sources as the shared snapshot's file views.</param>
    /// <returns>What the scan found, including the orphans it could not place.</returns>
    internal static KeyRouteReport Scan(IReadOnlyList<SourceTreeFile> sources) =>
        Scan(sources, TypeScanStrategy.WholeText);

    /// <summary>
    ///     Finds every hand-off and decides which of them product code can reach.
    /// </summary>
    /// <param name="sources">Product sources as the shared snapshot's file views.</param>
    /// <param name="strategy">How the type→file map is built.</param>
    /// <returns>What the scan found, including the orphans it could not place.</returns>
    internal static KeyRouteReport Scan(
        IReadOnlyList<SourceTreeFile> sources,
        TypeScanStrategy strategy)
    {
        // The counters and the phase clock. Both are MEASUREMENT, not
        // behaviour: they count the work the scan did and time each phase, and
        // the counts are what say which part of the rule to change. The scan
        // this class has always done is untouched — the numbers are recorded
        // around it.
        var counters = new ScanCounters();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // The type→file map. It is built FIRST and on its own, because it is
        // the phase that decides how the rest of the scan is read: a call
        // qualified by a type name resolves through it, and a map built from a
        // different set of declarations would move a verdict.
        Dictionary<string, List<string>> typeOwner = BuildTypeOwner(sources, strategy);
        long typeMapMs = watch.ElapsedMilliseconds;

        KeyRouteReport report = ScanCore(sources, typeOwner, counters, out long handOffScanMs, out long callersMs);

        LastPhases = new ScanPhases(
            ReadMs: 0,
            TypeMapMs: typeMapMs,
            HandOffScanMs: handOffScanMs,
            CallersMs: callersMs,
            TotalMs: watch.ElapsedMilliseconds,
            Files: sources.Count,
            Lines: counters.Lines,
            HandOffSites: report.HandOffs.Count,
            EnclosingWalks: counters.EnclosingWalks,
            CallersPasses: counters.CallersPasses,
            MemberMatches: counters.MemberMatches,
            CallSiteMatches: counters.CallSiteMatches,
            HandOffMatches: counters.HandOffMatches);

        return report;
    }

    /// <summary>
    ///     The scan, given the type→file map already built.
    /// </summary>
    /// <remarks>
    ///     The measurement uses it to time the scan WITHOUT the map, so the
    ///     map's own cost is not paid twice in one measurement.
    /// </remarks>
    /// <param name="sources">Product sources as the shared snapshot's file views.</param>
    /// <param name="typeOwner">The type→file map.</param>
    /// <returns>What the scan found, including the orphans it could not place.</returns>
    internal static KeyRouteReport ScanWithTypeOwner(
        IReadOnlyList<SourceTreeFile> sources,
        IReadOnlyDictionary<string, List<string>> typeOwner)
    {
        var counters = new ScanCounters();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        KeyRouteReport report = ScanCore(sources, typeOwner, counters, out long handOffScanMs, out long callersMs);

        LastPhases = new ScanPhases(
            ReadMs: 0,
            TypeMapMs: 0,
            HandOffScanMs: handOffScanMs,
            CallersMs: callersMs,
            TotalMs: watch.ElapsedMilliseconds,
            Files: sources.Count,
            Lines: counters.Lines,
            HandOffSites: report.HandOffs.Count,
            EnclosingWalks: counters.EnclosingWalks,
            CallersPasses: counters.CallersPasses,
            MemberMatches: counters.MemberMatches,
            CallSiteMatches: counters.CallSiteMatches,
            HandOffMatches: counters.HandOffMatches);

        return report;
    }

    /// <summary>
    ///     The scan proper: the hand-off sites, and which of them product code
    ///     can reach.
    /// </summary>
    /// <param name="sources">Product sources as the shared snapshot's file views.</param>
    /// <param name="typeOwner">The type→file map.</param>
    /// <param name="counters">The work counters.</param>
    /// <param name="handOffScanMs">How long collecting the hand-off sites took.</param>
    /// <param name="callersMs">How long the reachability passes took.</param>
    /// <returns>What the scan found, including the orphans it could not place.</returns>
    private static KeyRouteReport ScanCore(
        IReadOnlyList<SourceTreeFile> sources,
        IReadOnlyDictionary<string, List<string>> typeOwner,
        ScanCounters counters,
        out long handOffScanMs,
        out long callersMs)
    {
        var handOffs = new List<KeyHandOff>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        foreach (SourceTreeFile source in sources)
        {
            string file = SourceScan.Relative(source.Path);

            // The snapshot's own line view (#1791). The scan used to split the
            // text here, and `Callers` below split it AGAIN for every hand-off
            // it looked up — so the tree was split once per hand-off, over
            // every file, on every scan. One split per file, shared.
            ImmutableArray<string> lines = source.StrippedNewlineLines;
            counters.Lines += lines.Length;

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match site in HandOffSite.Matches(lines[i]))
                {
                    counters.HandOffMatches++;

                    if (EnclosingMethod(lines, i, counters) is not { } enclosing)
                    {
                        continue;
                    }

                    handOffs.Add(new KeyHandOff(file, i + 1, site.Groups["recv"].Value, enclosing.Name));
                }
            }
        }

        handOffScanMs = watch.ElapsedMilliseconds;

        // A hand-off PERFORMER is a member that hands a key off. A consumer's own
        // `OnKey` is a performer by this definition too, and that is correct: the
        // third hand-off #857 names is a layer whose `OnKey` is reached by nothing,
        // and the rule must be able to judge it.
        var performers = new HashSet<ProductMethod>(
            handOffs.Select(h => new ProductMethod(h.File, h.Performer)));

        var orphans = new List<OrphanHandOff>();
        foreach (KeyHandOff handOff in handOffs)
        {
            var performer = new ProductMethod(handOff.File, handOff.Performer);
            CallerSet callers = Callers(sources, performer, typeOwner, counters);

            if (callers.Unattributed)
            {
                // A call exists that this scan could not place. Fail open, and say so
                // in the header rather than inventing a verdict.
                continue;
            }

            // A caller that is itself a performer is another link of the same dead
            // chain — `RoutePanelKey` calling `RouteKey` — so it does not count as
            // the way in. Only a non-performer caller does.
            List<ProductMethod> wayIn = callers.Candidates
                .Where(c => !performers.Contains(c))
                .ToList();

            if (wayIn.Count == 0)
            {
                orphans.Add(new OrphanHandOff(
                    handOff,
                    "no product method outside the hand-off chain calls the method that performs it",
                    [handOff.Performer]));
                continue;
            }

            // Second hop: the way in must itself be called product code, or the
            // router is merely wired into something else that is equally dead.
            bool entered = wayIn
                .Select(c => Callers(sources, c, typeOwner, counters))
                .Any(second => second.Unattributed || second.Candidates.Count > 0);

            if (entered)
            {
                continue;
            }

            var chain = new List<string> { handOff.Performer };
            chain.AddRange(wayIn.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal));
            orphans.Add(new OrphanHandOff(
                handOff,
                "the only product callers of the method that performs it are called by nothing "
                    + "themselves — the chain never reaches a host",
                chain));
        }

        callersMs = watch.ElapsedMilliseconds - handOffScanMs;

        return new KeyRouteReport(sources.Count, handOffs, orphans);
    }

    /// <summary>What a call search found, and whether any of it could not be attributed.</summary>
    /// <param name="Candidates">Callers identified with a file and a name.</param>
    /// <param name="Unattributed">A call existed but the scan could not place it in a method.</param>
    private readonly record struct CallerSet(IReadOnlyList<ProductMethod> Candidates, bool Unattributed);

    /// <summary>
    ///     The type→file map: the simple name of every type the tree declares,
    ///     and the files that declare it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The map is what resolves a call qualified by a type name
    ///         (<c>Type.Member(</c>) to a file: a call whose receiver names a
    ///         type is a call of that type's member, and it is the target's
    ///         member only if the type lives in the target's file. Without the
    ///         map, every qualified call would be unattributable, and the rule
    ///         would fail open on all of them.
    ///     </para>
    ///     <para>
    ///         The files are in scan order and the FIRST one wins, because a
    ///         type name used twice in the tree must resolve to one file and
    ///         not to two.
    ///     </para>
    /// </remarks>
    /// <param name="sources">Product sources as the shared snapshot's file views.</param>
    /// <param name="strategy">How the declarations are found.</param>
    /// <returns>The map.</returns>
    internal static Dictionary<string, List<string>> BuildTypeOwner(
        IReadOnlyList<SourceTreeFile> sources,
        TypeScanStrategy strategy)
    {
        var typeOwner = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (SourceTreeFile source in sources)
        {
            string file = SourceScan.Relative(source.Path);

            switch (strategy)
            {
                case TypeScanStrategy.WholeText:
                    AddTypesWholeText(typeOwner, file, source.StrippedText);
                    break;
                case TypeScanStrategy.PerLine:
                    AddTypesPerLine(typeOwner, file, source);
                    break;
                case TypeScanStrategy.ManualLoop:
                    AddTypesManualLoop(typeOwner, file, source.StrippedText);
                    break;
                case TypeScanStrategy.EnumerateMatches:
                    AddTypesEnumerateMatches(typeOwner, file, source.StrippedText);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "unknown type-scan strategy.");
            }
        }

        return typeOwner;
    }

    /// <summary>
    ///     Adds one type name to the map, keeping the files in scan order and
    ///     each file once.
    /// </summary>
    /// <param name="typeOwner">The map to add to.</param>
    /// <param name="file">The file that declares the type.</param>
    /// <param name="typeName">The type's simple name.</param>
    private static void AddTypeOwner(
        Dictionary<string, List<string>> typeOwner,
        string file,
        string typeName)
    {
        if (!typeOwner.TryGetValue(typeName, out List<string>? owners))
        {
            owners = [];
            typeOwner[typeName] = owners;
        }

        if (!owners.Contains(file))
        {
            owners.Add(file);
        }
    }

    /// <summary>
    ///     The way the map has always been built: one whole-file match
    ///     collection per file, enumerated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the ORACLE. The rule's verdicts were computed with this
    ///         map, and the strategies beside it exist to make the map cheaper.
    ///         Every one of them has to produce the same map, and
    ///         <c>PanelKeyRouteEquivalenceTests</c> is what holds them to it.
    ///     </para>
    ///     <para>
    ///         It is also the most expensive thing the rule does, and the
    ///         reason is the shape of the collection: <c>Regex.Matches</c>
    ///         returns a LAZY collection, and enumerating it re-enters the
    ///         pattern engine once per match. Over a whole file, with a
    ///         <c>^</c>-anchored pattern, that is the rule's whole wall.
    ///     </para>
    /// </remarks>
    /// <param name="typeOwner">The map to add to.</param>
    /// <param name="file">The file that declares the type.</param>
    /// <param name="text">The file's comment-stripped text.</param>
    private static void AddTypesWholeText(
        Dictionary<string, List<string>> typeOwner,
        string file,
        string text)
    {
        foreach (Match type in TypeDeclaration.Matches(text))
        {
            if (type.Groups["name"].Value is not { Length: > 0 } typeName)
            {
                continue;
            }

            AddTypeOwner(typeOwner, file, typeName);
        }
    }

    /// <summary>
    ///     One match per line.
    /// </summary>
    /// <remarks>
    ///     The pattern is anchored per line BY DESIGN — the header says the
    ///     multiline flag is load-bearing — so a line is the unit the pattern
    ///     is written for, and a line cannot match across a newline.
    /// </remarks>
    /// <param name="typeOwner">The map to add to.</param>
    /// <param name="file">The file that declares the type.</param>
    /// <param name="source">The file's view of the tree.</param>
    private static void AddTypesPerLine(
        Dictionary<string, List<string>> typeOwner,
        string file,
        SourceTreeFile source)
    {
        foreach (string line in source.StrippedNewlineLines)
        {
            Match type = TypeDeclaration.Match(line);
            if (type.Success && type.Groups["name"].Value is { Length: > 0 } typeName)
            {
                AddTypeOwner(typeOwner, file, typeName);
            }
        }
    }

    /// <summary>
    ///     The whole text, matched by hand: one match at a time, each starting
    ///     where the last one ended.
    /// </summary>
    /// <remarks>
    ///     The same matches as the whole-file collection, in the same order,
    ///     found without materialising a collection per file.
    /// </remarks>
    /// <param name="typeOwner">The map to add to.</param>
    /// <param name="file">The file that declares the type.</param>
    /// <param name="text">The file's comment-stripped text.</param>
    private static void AddTypesManualLoop(
        Dictionary<string, List<string>> typeOwner,
        string file,
        string text)
    {
        int position = 0;
        while (position <= text.Length)
        {
            Match type = TypeDeclaration.Match(text, position);
            if (!type.Success)
            {
                return;
            }

            if (type.Groups["name"].Value is { Length: > 0 } typeName)
            {
                AddTypeOwner(typeOwner, file, typeName);
            }

            position = type.Index + type.Length;
        }
    }

    /// <summary>
    ///     The whole text through the span-based enumerator, which yields the
    ///     matches without materialising a <c>Match</c> per hit.
    /// </summary>
    /// <param name="typeOwner">The map to add to.</param>
    /// <param name="file">The file that declares the type.</param>
    /// <param name="text">The file's comment-stripped text.</param>
    private static void AddTypesEnumerateMatches(
        Dictionary<string, List<string>> typeOwner,
        string file,
        string text)
    {
        foreach (ValueMatch type in TypeDeclaration.EnumerateMatches(text))
        {
            if (type.Groups["name"].ValueSpan is { Length: > 0 } nameSpan)
            {
                AddTypeOwner(typeOwner, file, new string(nameSpan));
            }
        }
    }

    /// <summary>
    ///     Every product method that calls <paramref name="target" />.
    /// </summary>
    /// <remarks>
    ///     Identity is (file, name) rather than name alone, because two distinct
    ///     methods in this tree are both called <c>RouteKey</c>
    ///     (<c>OverlayStack.RouteKey</c> and <c>CellForgePanelAdapter.RouteKey</c>);
    ///     keying by name would merge them and each would vouch for the other's
    ///     reachability. A call qualified by a type name resolves through the
    ///     type→file map, and if that type's file is not the target's the call
    ///     simply is not a call of the target. A call the scan cannot attribute is
    ///     reported as unattributed, which the rule treats as reachable — failing
    ///     open is the only safe direction for a gate.
    /// </remarks>
    /// <param name="sources">Product sources.</param>
    /// <param name="target">The method whose callers are wanted.</param>
    /// <param name="typeOwner">Type simple name → the files declaring it.</param>
    /// <param name="counters">The work counters, for the measurement.</param>
    /// <returns>The identified callers, and whether any call went unattributed.</returns>
    private static CallerSet Callers(
        IReadOnlyList<SourceTreeFile> sources,
        ProductMethod target,
        IReadOnlyDictionary<string, List<string>> typeOwner,
        ScanCounters counters)
    {
        var found = new HashSet<ProductMethod>();
        bool unattributed = false;
        counters.CallersPasses++;

        foreach (SourceTreeFile source in sources)
        {
            string file = SourceScan.Relative(source.Path);

            // The snapshot's line view, not a fresh split. This is the hot
            // loop of the whole rule: it runs once per hand-off, over every
            // file in the product trees, and it used to re-split every file
            // on every one of those passes.
            ImmutableArray<string> lines = source.StrippedNewlineLines;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains(target.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                bool isDeclaration = MemberDeclaration.Match(line) is { Success: true } declaration
                    && declaration.Groups["name"].Value == target.Name;
                counters.MemberMatches++;

                foreach (Match call in CallSite.Matches(line))
                {
                    counters.CallSiteMatches++;

                    if (call.Groups["name"].Value != target.Name || isDeclaration)
                    {
                        continue;
                    }

                    string? qualified = QualifiedOwner(line, target.Name, typeOwner);
                    string owner;
                    if (qualified is not null)
                    {
                        // Qualified by a type: the call names that type's member, so
                        // it is our target only if that type's file is the target's.
                        if (!string.Equals(qualified, target.File, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        owner = qualified;
                    }
                    else if (string.Equals(file, target.File, StringComparison.Ordinal))
                    {
                        owner = file;
                    }
                    else
                    {
                        // A call on an expression, of a name this tree uses twice.
                        unattributed = true;
                        continue;
                    }

                    if (EnclosingMethod(lines, i, counters) is not { } enclosing
                        || string.Equals(enclosing.Name, target.Name, StringComparison.Ordinal))
                    {
                        // A recursive self-call, or a call this scan cannot place.
                        unattributed = true;
                        continue;
                    }

                    found.Add(new ProductMethod(owner, enclosing.Name));
                }
            }
        }

        return new CallerSet(
            found.OrderBy(c => c.ToString(), StringComparer.Ordinal).ToList(),
            unattributed);
    }

    /// <summary>
    ///     The file declaring the type a call is qualified by, or <c>null</c> when
    ///     the receiver is not a type this scan knows.
    /// </summary>
    /// <param name="line">The source line holding the call.</param>
    /// <param name="memberName">The member being called.</param>
    /// <param name="typeOwner">Type simple name → the files declaring it.</param>
    /// <returns>The declaring file, or <c>null</c>.</returns>
    private static string? QualifiedOwner(
        string line,
        string memberName,
        IReadOnlyDictionary<string, List<string>> typeOwner)
    {
        foreach (Match qualified in TypeQualifiedCall.Matches(line))
        {
            if (qualified.Groups["name"].Value != memberName)
            {
                continue;
            }

            if (qualified.Groups["type"].Value is { Length: > 0 } typeName
                && typeOwner.TryGetValue(typeName, out List<string>? owners)
                && owners.Count > 0)
            {
                return owners[0];
            }
        }

        return null;
    }

    /// <summary>
    ///     The nearest member declaration at or above a line.
    /// </summary>
    /// <param name="lines">The comment-stripped lines of one file.</param>
    /// <param name="index">Zero-based line index to start from.</param>
    /// <param name="counters">The work counters, for the measurement.</param>
    /// <returns>The declared name and its one-based line, or <c>null</c>.</returns>
    private static (string Name, int Line)? EnclosingMethod(
        IReadOnlyList<string> lines,
        int index,
        ScanCounters counters)
    {
        int floor = Math.Max(0, index - MaxEnclosingWalk);
        counters.EnclosingWalks++;

        for (int i = index; i >= floor; i--)
        {
            Match declaration = MemberDeclaration.Match(lines[i]);
            counters.MemberMatches++;

            if (declaration.Success)
            {
                return (declaration.Groups["name"].Value, i + 1);
            }
        }

        return null;
    }

    /// <summary>
    ///     Test methods that call a method the report found unreachable. This is
    ///     the measurement the issue asks for, kept as code rather than as a
    ///     paragraph: a test that is the SOLE caller of a method nothing in the
    ///     product calls cannot fail when the product path is absent, so its green
    ///     is not evidence.
    /// </summary>
    /// <remarks>
    ///     A dead name is skipped when the report also found a LIVE hand-off
    ///     carrying it, because then a bare name cannot tell the two apart in a
    ///     test. The skip is reported, never silent.
    /// </remarks>
    /// <param name="report">What the product scan found.</param>
    /// <param name="testSources">Test sources as the shared snapshot's file views.</param>
    /// <returns>One entry per test method that drives a dead router, plus any skip, sorted.</returns>
    internal static IReadOnlyList<string> TestsThatDriveOnlyDeadRoutes(
        KeyRouteReport report,
        IReadOnlyList<SourceTreeFile> testSources)
    {
        var dead = new SortedSet<string>(StringComparer.Ordinal);
        foreach (OrphanHandOff orphan in report.Orphans)
        {
            foreach (string name in orphan.Chain)
            {
                dead.Add(name);
            }
        }

        var orphanedNames = new HashSet<string>(
            report.Orphans.Select(o => o.HandOff.Performer), StringComparer.Ordinal);
        var sharedWithALiveHandOff = new HashSet<string>(
            report.HandOffs
                .Where(h => !orphanedNames.Contains(h.Performer))
                .Select(h => h.Performer),
            StringComparer.Ordinal);

        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (SourceTreeFile source in testSources)
        {
            string file = SourceScan.Relative(source.Path);
            ImmutableArray<string> lines = source.StrippedNewlineLines;
            string? current = null;
            bool armed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith('['))
                {
                    armed = true;
                    continue;
                }

                Match declaration = MemberDeclaration.Match(lines[i]);
                if (declaration.Success)
                {
                    current = armed ? declaration.Groups["name"].Value : null;
                    armed = false;
                    continue;
                }

                if (current is null)
                {
                    continue;
                }

                foreach (string name in dead)
                {
                    if (sharedWithALiveHandOff.Contains(name) || !Invokes(lines[i], name))
                    {
                        continue;
                    }

                    hits.Add($"{file}::{current} → {name}()");
                }
            }
        }

        foreach (string name in dead)
        {
            if (sharedWithALiveHandOff.Contains(name))
            {
                hits.Add($"(not attributed: '{name}' is also carried by a hand-off the product CAN "
                    + "reach, so a test calling it by that name cannot be told apart)");
            }
        }

        return hits.ToList();
    }

    /// <summary>Whether a line actually invokes a given name, rather than merely mentioning it.</summary>
    /// <param name="line">The line to inspect.</param>
    /// <param name="name">The name that must be invoked.</param>
    /// <returns><c>true</c> when the line calls a member of that name.</returns>
    private static bool Invokes(string line, string name)
    {
        if (!line.Contains(name, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Match call in CallSite.Matches(line))
        {
            if (string.Equals(call.Groups["name"].Value, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     What each PATTERN of the scan costs, measured on its own over the
    ///     product tree.
    /// </summary>
    /// <remarks>
    ///     The scan's phases say WHERE the time went; these say WHAT it went
    ///     into. Each pattern is run over the whole tree on its own, so the
    ///     numbers are the cost of that pattern at tree scale, and the call
    ///     counts say how often the scan actually pays it.
    /// </remarks>
    /// <param name="sources">Product sources as the shared snapshot's file views.</param>
    /// <returns>The per-pattern costs, the call counts and the cold read cost.</returns>
    internal static ScanComponentCosts MeasureComponents(IReadOnlyList<SourceTreeFile> sources)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        long memberCalls = 0;
        foreach (SourceTreeFile source in sources)
        {
            foreach (string line in source.StrippedNewlineLines)
            {
                _ = MemberDeclaration.Match(line);
                memberCalls++;
            }
        }

        long memberMs = watch.ElapsedMilliseconds;

        watch.Restart();
        long handOffCalls = 0;
        foreach (SourceTreeFile source in sources)
        {
            foreach (string line in source.StrippedNewlineLines)
            {
                _ = HandOffSite.Matches(line);
                handOffCalls++;
            }
        }

        long handOffMs = watch.ElapsedMilliseconds;

        watch.Restart();
        long callSiteCalls = 0;
        foreach (SourceTreeFile source in sources)
        {
            foreach (string line in source.StrippedNewlineLines)
            {
                _ = CallSite.Matches(line);
                callSiteCalls++;
            }
        }

        long callSiteMs = watch.ElapsedMilliseconds;

        watch.Restart();
        long typeCalls = 0;
        foreach (SourceTreeFile source in sources)
        {
            _ = TypeDeclaration.Matches(source.StrippedText);
            typeCalls++;
        }

        long typeMs = watch.ElapsedMilliseconds;

        // The cold read: the same bytes the shared snapshot holds, read from
        // the disk again, because the snapshot's own read is already paid for
        // by the time this measurement runs and would report zero. The
        // blanker is called directly, not through `SourceScan.StripComments`,
        // so the measurement does not move the stripper meter the other
        // measurement prints.
        watch.Restart();
        long readBytes = 0;
        foreach (SourceTreeFile source in sources)
        {
            string raw = File.ReadAllText(source.Path);
            readBytes += raw.Length;
            _ = SourceCommentBlanker.Blank(raw);
        }

        long coldReadMs = watch.ElapsedMilliseconds;

        // The cold WALK: the directory enumeration the rule's tree read starts
        // with. It is measured over the same directories the rule walks, and
        // it counts every file the walk SEES — including the build output the
        // filter rejects afterwards, because the walk pays for those too.
        watch.Restart();
        long walked = 0;
        if (RepoPaths.RepoRoot is { } root)
        {
            foreach (string tree in SourceScan.ProductTrees)
            {
                string dir = Path.Combine(root, tree);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string _ in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    walked++;
                }
            }
        }

        long coldWalkMs = watch.ElapsedMilliseconds;

        int lines = 0;
        foreach (SourceTreeFile source in sources)
        {
            lines += source.StrippedNewlineLines.Length;
        }

        return new ScanComponentCosts(
            MemberPatternMs: memberMs,
            MemberPatternCalls: memberCalls,
            HandOffPatternMs: handOffMs,
            HandOffPatternCalls: handOffCalls,
            CallSitePatternMs: callSiteMs,
            CallSitePatternCalls: callSiteCalls,
            TypePatternMs: typeMs,
            TypePatternCalls: typeCalls,
            ColdReadMs: coldReadMs,
            ColdReadChars: readBytes,
            ColdWalkMs: coldWalkMs,
            ColdWalkFiles: walked,
            Files: sources.Count,
            Lines: lines);
    }
}

/// <summary>
///     What each pattern of the key-route scan costs at tree scale, and how
///     often the scan pays it.
/// </summary>
/// <param name="MemberPatternMs">The member-declaration pattern over every line of the tree.</param>
/// <param name="MemberPatternCalls">How many lines it ran over.</param>
/// <param name="HandOffPatternMs">The hand-off pattern over every line of the tree.</param>
/// <param name="HandOffPatternCalls">How many lines it ran over.</param>
/// <param name="CallSitePatternMs">The call-site pattern over every line of the tree.</param>
/// <param name="CallSitePatternCalls">How many lines it ran over.</param>
/// <param name="TypePatternMs">The type-declaration pattern over every file of the tree.</param>
/// <param name="TypePatternCalls">How many files it ran over.</param>
/// <param name="ColdReadMs">Reading the tree from the disk again, and stripping it.</param>
/// <param name="ColdReadChars">How many characters that read was.</param>
/// <param name="ColdWalkMs">Walking the product directories again.</param>
/// <param name="ColdWalkFiles">How many files that walk saw, build output included.</param>
/// <param name="Files">How many files the tree holds.</param>
/// <param name="Lines">How many lines the tree holds.</param>
internal sealed record ScanComponentCosts(
    long MemberPatternMs,
    long MemberPatternCalls,
    long HandOffPatternMs,
    long HandOffPatternCalls,
    long CallSitePatternMs,
    long CallSitePatternCalls,
    long TypePatternMs,
    long TypePatternCalls,
    long ColdReadMs,
    long ColdReadChars,
    long ColdWalkMs,
    long ColdWalkFiles,
    int Files,
    int Lines);

/// <summary>
///     Guard for issue #857: a key hand-off into the panel/overlay input seam must
///     be reachable from product code, and a test that is the only caller of an
///     unreachable hand-off is not evidence that the input works.
/// </summary>
public sealed class PanelKeyRouteReachabilityRule
{
    private static readonly Lazy<KeyRouteReport> Report = new(
        () =>
        {
            // The read is timed apart from the scan: the read is the shared
            // snapshot's work (every rule in this assembly reads the same
            // bytes once), and the scan is this rule's own. A measurement that
            // does not separate them cannot say which of the two to work on.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<SourceTreeFile> sources = PanelKeyRouteProbe.ReadProductTrees();
            long readMs = watch.ElapsedMilliseconds;

            KeyRouteReport report = PanelKeyRouteProbe.Scan(sources);

            PanelKeyRouteProbe.RecordReadMs(readMs);

            return report;
        });

    private static readonly Lazy<IReadOnlyList<SourceTreeFile>> Tests = new(
        () => PanelKeyRouteProbe.ReadTestTrees());

    /// <summary>
    ///     The memoised scan of the product trees, for the measurement that
    ///     reports what this rule finds (#1791).
    /// </summary>
    internal static KeyRouteReport ProductReport => Report.Value;

    /// <summary>
    ///     The memoised read of the test tree, for the measurement that
    ///     reports what this rule finds (#1791).
    /// </summary>
    internal static IReadOnlyList<SourceTreeFile> TestSources => Tests.Value;

    /// <summary>
    ///     No hand-off in the product trees may be unreachable unless the ledger says
    ///     this one is known. The ledger holds exactly the two routers #857 and #812
    ///     are about, so a THIRD unreachable router fails here.
    /// </summary>
    /// <remarks>
    ///     This is the rule that was red on the first run of this guard, and the
    ///     failure message named both offenders with their chains — which is the
    ///     whole point: the defect was assertable, not a reading of the code.
    /// </remarks>
    [Test]
    public async Task EveryKeyHandOffIsReachableFromProductCode()
    {
        PanelKeyRouteProbe.PartitionDebt(Report.Value, out IReadOnlyList<OrphanHandOff> undeclared, out _);

        List<string> offenders = undeclared
            .Select(o => $"{o.HandOff.File}:{o.HandOff.Line} — {o.HandOff.Receiver}."
                + $"{PanelKeyRouteProbe.SeamMember}() inside {o.HandOff.Performer}: {o.Verdict}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(offenders).IsEmpty().Because(
            "a hand-off into the panel/overlay input seam that product code cannot enter is a "
            + "feature whose input does not work, and the tests over it are green only because "
            + "they are the sole callers. The ledger already carries the two routers #857 and "
            + "#812 are about, so anything reported here is a THIRD one, or a renamed "
            + "version of one the ledger names. Fix by WIRING a host to the router or by "
            + "DELETING it — the gate is built to accept either and to be unsatisfiable by a "
            + "rename, because it reads the hand-off out of the source rather than from a "
            + "list. Unreachable hand-offs with no ledger entry: "
            + (offenders.Count == 0
                ? "(none — the scan graded no hand-off at all, which is its own failure; see "
                  + "NonVacuity_TheRealTreeStillExposesTheHandOffSeam)"
                : string.Join(" | ", offenders)));
    }

    /// <summary>
    ///     The ledger is the debt, and a debt that is not re-checked is a comment
    ///     that rots. Every entry must STILL be an orphan — so wiring or deleting a
    ///     router turns this red until its entry is removed, and the debt can only be
    ///     retired by an explicit edit.
    /// </summary>
    [Test]
    public async Task TheAcknowledgedDebtIsExactlyWhatTheScanStillFinds()
    {
        PanelKeyRouteProbe.PartitionDebt(Report.Value, out IReadOnlyList<OrphanHandOff> undeclared, out IReadOnlyList<AcknowledgedDebt> stale);

        await Assert.That(undeclared.Count).IsEqualTo(0).Because(
            "an unreachable router the ledger does not name is new debt, and the whole value of "
            + "carrying the two known ones is that a third cannot hide among them. Add it to the "
            + "ledger with the issue that owns it, or fix it. Undeclared: "
            + (undeclared.Count == 0
                ? "(none)"
                : string.Join(" | ", undeclared.Select(o => $"{o.HandOff.File}:{o.HandOff.Performer}"))));

        await Assert.That(stale.Count).IsEqualTo(0).Because(
            "a ledger entry whose router is no longer unreachable is retired debt that was never "
            + "removed, which is how an allowance outlives the thing it allowed. If the router was "
            + "wired or deleted, drop its entry here — that edit is the record that #857 or #812 "
            + "was resolved. Stale entries: "
            + (stale.Count == 0
                ? "(none)"
                : string.Join(" | ", stale.Select(s => $"{s.File}:{s.Name} (owned by {s.OwnedBy})"))));
    }

    /// <summary>
    ///     The scan must still be able to see the seam in the real tree. A floor, not
    ///     an equality, so wiring or deleting the routers does not have to update
    ///     it — but a moved file, a renamed method or a regex that stopped matching
    ///     is red here instead of silently turning the gate vacuous.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheRealTreeStillExposesTheHandOffSeam()
    {
        int files = Report.Value.FilesScanned;
        int handOffs = Report.Value.HandOffs.Count;

        await Assert.That(files).IsGreaterThan(500).Because(
            "a source scan that read almost nothing has found no hand-off because it looked in "
            + "the wrong place, not because the tree is clean. RepoPaths.RepoRoot was "
            + (RepoPaths.RepoRoot is null ? "null" : "found") + " and " + files
            + " product files were read.");

        await Assert.That(handOffs).IsGreaterThanOrEqualTo(3).Because(
            "the whole product tree contains three hand-offs into the input seam — "
            + "`OverlayStack.RouteKey`, `CellForgePanelAdapter.RouteKey` and "
            + "`CellForgeJumpPaletteOverlayLayer.OnKey` — and the scan is what makes that number "
            + "checkable rather than a claim. Finding fewer means the seam is no longer being "
            + "read (the consumers were reworked) or the scan regressed, and either way the gate "
            + "above is no longer measuring what it says. Found: " + handOffs + ".");
    }

    /// <summary>
    ///     The positive and the negative control, on SYNTHETIC sources carrying
    ///     types that appear nowhere in this tree. A wired hand-off must come back
    ///     reachable; an orphan one must come back orphaned. Both run through the
    ///     SAME scanner the rule uses, so the control cannot drift from the gate,
    ///     and a scanner that answered a constant would fail here.
    /// </summary>
    [Test]
    public async Task NonVacuity_AWiredHandOffIsReachableAndAnOrphanIsNot()
    {
        // Two hops, because two hops is what the rule asks for: the entry calls
        // the host, the host calls the router, the router performs the hand-off.
        const string Wired = """
            namespace Synthetic.Control;

            public sealed class SyntheticRouter
            {
                public bool RouteKey(in SyntheticKey key)
                {
                    return _consumer.OnKey(in key);
                }
            }

            public sealed class SyntheticHost
            {
                public void Pump()
                {
                    _ = new SyntheticRouter().RouteKey(default);
                }
            }

            public sealed class SyntheticEntry
            {
                public void Start()
                {
                    new SyntheticHost().Pump();
                }
            }
            """;

        // The same hand-off shape, with nothing calling the method that performs it.
        const string Orphan = """
            namespace Synthetic.Control;

            public sealed class SyntheticRouter
            {
                public bool RouteKey(in SyntheticKey key)
                {
                    return _consumer.OnKey(in key);
                }
            }
            """;

        // The synthetic sources go through the same file view the tree does
        // (#1791), so the control exercises the production path and not a
        // second, parallel one. `FromStrippedText` takes the source as it
        // already is: these fixtures carry no comments, and the tree's files
        // arrive comment-stripped from the snapshot.
        var wired = PanelKeyRouteProbe.Scan([SourceTreeFile.FromStrippedText("SyntheticRouter.cs", Wired)]);
        var orphan = PanelKeyRouteProbe.Scan([SourceTreeFile.FromStrippedText("SyntheticRouter.cs", Orphan)]);

        await Assert.That(wired.HandOffs.Count).IsEqualTo(1).Because(
            "the control source declares one hand-off (`_consumer.OnKey(in key)`), and the scan "
            + "has to find it by FORM. Finding zero means the seam regex no longer matches the "
            + "shape it is meant to match, which would make every other test in this file "
            + "vacuous. Found: " + wired.HandOffs.Count + ".");

        await Assert.That(wired.Orphans.Count).IsEqualTo(0).Because(
            "in the control, `SyntheticEntry.Start` calls `Pump`, which calls `RouteKey`, so the "
            + "hand-off is entered by called product code and the gate must let it pass. A "
            + "scanner that called this an orphan would reject the legitimate wiring this rule "
            + "exists to permit, and #857's fix would be unmergeable. Orphans: "
            + wired.Orphans.Count + ".");

        await Assert.That(orphan.Orphans.Count).IsEqualTo(1).Because(
            "in the control, nothing calls `RouteKey`, so the hand-off is an orphan and the gate "
            + "must fail. A scanner that passed this one would wave through the real defect. "
            + "Orphans: " + orphan.Orphans.Count + ".");
    }

    /// <summary>
    ///     The measurement #857 asks for, kept as a test so it cannot rot: which
    ///     test methods call a router that product code cannot enter. Each of those
    ///     is green today and would stay green with the product path deleted, which
    ///     is the shape the issue reports.
    /// </summary>
    /// <remarks>
    ///     A ratchet, set at the MEASURED value (13: six test methods drive
    ///     <c>RoutePanelKey</c>, seven drive <c>OverlayStack.RouteKey</c>). It can
    ///     only go down by an explicit edit, which is what happens when #857 or #812
    ///     is resolved — and that edit is the record. It cannot go down by accident,
    ///     which is the failure this guard exists to prevent: on its first run this
    ///     very test reported <c>0</c> because the shared <c>SourceScan</c> walker
    ///     cannot see <c>tests/</c> at all, and a count of zero reads exactly like a
    ///     clean bill of health.
    /// </remarks>
    [Test]
    public async Task TestsDrivingAnUnreachableRouterAreNamedAndCounted()
    {
        IReadOnlyList<string> hits = PanelKeyRouteProbe.TestsThatDriveOnlyDeadRoutes(
            Report.Value, Tests.Value);

        await Assert.That(hits.Count).IsGreaterThanOrEqualTo(13).Because(
            "a test that is the SOLE caller of a method no product code calls cannot fail when "
            + "the product path is absent — that is the test #857 reports, in the shape "
            + "`PanelWiringTests.RouteKey_Logs_F12_Toggles_Panel` has: it drives "
            + "`RoutePanelKey` directly AND dispatches the `AppMsg.FocusPanel` that the "
            + "product's F12 path never dispatches, so it asserts a precondition it "
            + "manufactured. Measured 13. If a router is wired or deleted this count drops "
            + "and the floor is lowered HERE, on purpose, so the drop is a recorded decision "
            + "rather than a silent one. Named: "
            + (hits.Count == 0 ? "(the scan attributed no test at all)" : string.Join(" | ", hits)));
    }

    /// <summary>
    ///     The measurement has to be able to SEE the test tree, and the shared
    ///     walker cannot: <c>SourceScan.IsBuildOutput</c> rejects any path containing
    ///     <c>/tests/</c>, so <c>SourceScan.EnumerateCsFiles("tests")</c> is empty.
    ///     That is why the walker in this file is local, and this floor is what keeps
    ///     it working — a test-tree scan that silently returns nothing reports a
    ///     healthy zero, which is the same shape as the defect it is measuring.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheTestTreeIsActuallyVisibleToTheMeasurement()
    {
        int files = Tests.Value.Count;

        await Assert.That(files).IsGreaterThan(500).Because(
            "the measurement reads the test tree with a walker local to this file, because "
            + "SourceScan.IsBuildOutput rejects `/tests/` and its EnumerateCsFiles(\"tests\") "
            + "returns an empty list. A test-tree scan that finds nothing would report zero "
            + "vacuous tests, which is indistinguishable from there being none. Read: "
            + files + " files.");

        await Assert.That(SourceScan.EnumerateCsFiles("tests").Count).IsEqualTo(0).Because(
            "this is the fact the local walker exists for, pinned so it is not 'fixed' by "
            + "changing shared SourceScan behaviour underneath every other guard in this "
            + "project. If a future SourceScan change makes this non-empty, the local "
            + "walker and this expectation must be reconciled deliberately rather than by "
            + "one of the two silently changing.");
    }

    /// <summary>
    ///     The gate needs the tree to look at. Without this, a broken
    ///     <see cref="RepoPaths.RepoRoot" /> reports zero hand-offs, which the rule
    ///     above would read as clean.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheRepoRootWasFound()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull().Because(
            "every rule in this project reads the tree from RepoPaths.RepoRoot, and a null root "
            + "makes a source scan find nothing — indistinguishable from a clean tree. See the "
            + "failure mode in NonVacuity_TheRealTreeStillExposesTheHandOffSeam.");
    }
}
