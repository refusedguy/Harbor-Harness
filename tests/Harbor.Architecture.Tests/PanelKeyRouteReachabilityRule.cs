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
//   src/Harbor.Tui.CellForge.Engine/Rendering/OverlayStack.cs:180
//       `if (layer.OnKey(in key))` — inside `OverlayStack.RouteKey`, which no
//       product method calls. (#812)
//   src/Harbor.Tui.CellForge/Chat/Panels/CellForgePanelAdapter.cs:63
//       `return provider.OnKey(key, ctx);` — inside
//       `CellForgePanelAdapter.RouteKey`, whose only caller in the whole tree is
//       `ChatScreenLayout.cs:1449`.
// check-doc-cites: record-drift ChatScreenLayout.cs:1449 now="if (view.GetState(id) != TuiPanelState.Focused)" [#947: written over `return CellForgePanelAdapter.RouteKey(pr`; repair deferred to the owner's symbol-rename decision] -->
//   src/Harbor.Tui.CellForge/Chat/Panels/CellForgeJumpPaletteOverlayLayer.cs:170
// check-doc-cites: record-drift src/Harbor.Tui.CellForge/Chat/Panels/CellForgeJumpPaletteOverlayLayer.cs:170 now="/// sees it." [#947: written over `return _panel.OnKey(`; repair deferred to the owner's symbol-rename decision] -->
//       `return _panel.OnKey(` — inside that layer's own `OnKey`, and the layer
//       is never constructed outside tests.
//
// And `ChatScreenLayout.RoutePanelKey` (`:1405`), the caller at `:1449`, has NO
// caller either. So the chain is two links long and both ends are in the air.
//
// WHY THE USER-VISIBLE PART IS SMALLER THAN "7 BROKEN PANELS"
// -----------------------------------------------------------
// The blast radius reads as seven because `IPanelProvider.OnKey` is a REQUIRED
// interface member (`IPanelProvider.cs:76`) and seven providers implement it.
// Measured by what the key actually does, it is smaller and stranger:
//
//   * `CellForgeLogsPanel.OnKey` handles F12 by dispatching
//     `AppMsg.TogglePanel("logs")` — and F12 is ALREADY bound globally to
//     `ChatAction.ToggleLogsPanel` (`ChatKeyMap.cs:81`), which the reducer
//     applies as the same `TogglePanel(state, "logs")` (`AppReducer.cs:445`).
//     The dead override is a byte-for-byte duplicate of a live hotkey.
//   * `CellForgeHelpPanel.OnKey` handles '?' the same way, and '?' is already
//     bound to `ChatAction.HelpPanel` → `TogglePanel(state, "help")`
//     (`ChatKeyMap.cs:45`, `AppReducer.cs:443`).
//   * `CellForgeSkillFreshnessPanel.OnKey` is `=> false` — it wants nothing.
//   * That leaves `CellForgeFileTreePanel` (j/k/h/l + Enter navigation),
//     `CellForgeDiagnosticsPanel` (j/k cursor) and `CellForgeSubagentsPanel`
//     (cursor / transcript / refresh) as the only three whose `OnKey` carries
//     capability the global keymap does not already have — plus
//     `CellForgeJumpPalettePanel`, which is keyed AND unpainted, because
//     `DefaultPlacement => Center` sends the dock home early
//     (`ChatScreenLayout.cs:1265-1271`) and the `IOverlayLayer` meant to paint it
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
//     (`ChatScreenLayout.cs:1434-1437`) and the product's F12 path only ever
// check-doc-cites: record-drift ChatScreenLayout.cs:1434 now="if (provider is null)" [#947: written over ``; repair deferred to the owner's symbol-rename decision] -->
//     reaches `Visible` — `AppReducer.TogglePanel` (`:172-187`) toggles
//     Hidden↔Visible and never sets `FocusedPanelId`. NO product host dispatches
//     `AppMsg.FocusPanel` with a non-null id; the one non-test dispatch is
//     `contrib/tui/Harbor.Tui.SpectreTui/SpectreTuiRenderer.cs:323`, and
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
// `OverlayStack` host-routing API, and both are bounded by the feature freeze
// #555 — the two known-dead routers sit in `PanelKeyRouteProbe.Ledger` with the
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
    ///     The debt ledger: unreachable routers this project is carrying on purpose,
    ///     each with the issue that owns the decision.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is NOT a fix and NOT a decision. #857 asks whether
    ///         <c>IPanelProvider.OnKey</c> should be wired or deleted, and #812 asks
    ///         the same of the <c>OverlayStack</c> host-routing API; both are bounded
    ///         by the feature freeze <c>#555</c> and both are product calls, so neither
    ///         is the guard's to make. What the ledger does is stop the two known
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
            "src/Harbor.Tui.CellForge.Engine/Rendering/OverlayStack.cs",
            "RouteKey",
            "#812 — the engine's host-routing API; the two live layers are reached directly by ReplInputLoop"),
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
    /// <returns>Absolute-path / text pairs; unreadable files are skipped.</returns>
    internal static List<(string Path, string Text)> ReadProductTrees() =>
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
    /// <returns>Absolute-path / text pairs; unreadable files are skipped.</returns>
    internal static List<(string Path, string Text)> ReadTestTrees() => Read(EnumerateTestFiles());

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

        return Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string normalised = path.Replace('\\', '/');
                return !normalised.Contains("/obj/", StringComparison.Ordinal)
                    && !normalised.Contains("/bin/", StringComparison.Ordinal)
                    && !normalised.Contains("/.worktrees/", StringComparison.Ordinal);
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads and comment-strips the given files.</summary>
    /// <param name="paths">Absolute file paths.</param>
    /// <returns>Absolute-path / comment-stripped text pairs.</returns>
    private static List<(string Path, string Text)> Read(IReadOnlyList<string> paths)
    {
        var sources = new List<(string Path, string Text)>();
        foreach (string path in paths)
        {
            if (SourceScan.TryReadAllText(path) is { } text)
            {
                sources.Add((path, SourceScan.StripComments(text)));
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
    /// <param name="sources">Product sources as absolute path / comment-stripped text.</param>
    /// <returns>What the scan found, including the orphans it could not place.</returns>
    internal static KeyRouteReport Scan(IReadOnlyList<(string Path, string Text)> sources)
    {
        var typeOwner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var handOffs = new List<KeyHandOff>();

        foreach ((string path, string text) in sources)
        {
            string file = SourceScan.Relative(path);
            string[] lines = text.Split('\n');

            foreach (Match type in TypeDeclaration.Matches(text))
            {
                if (type.Groups["name"].Value is not { Length: > 0 } typeName)
                {
                    continue;
                }

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

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match site in HandOffSite.Matches(lines[i]))
                {
                    if (EnclosingMethod(lines, i) is not { } enclosing)
                    {
                        continue;
                    }

                    handOffs.Add(new KeyHandOff(file, i + 1, site.Groups["recv"].Value, enclosing.Name));
                }
            }
        }

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
            CallerSet callers = Callers(sources, performer, typeOwner);

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
                .Select(c => Callers(sources, c, typeOwner))
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

        return new KeyRouteReport(sources.Count, handOffs, orphans);
    }

    /// <summary>What a call search found, and whether any of it could not be attributed.</summary>
    /// <param name="Candidates">Callers identified with a file and a name.</param>
    /// <param name="Unattributed">A call existed but the scan could not place it in a method.</param>
    private readonly record struct CallerSet(IReadOnlyList<ProductMethod> Candidates, bool Unattributed);

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
    /// <returns>The identified callers, and whether any call went unattributed.</returns>
    private static CallerSet Callers(
        IReadOnlyList<(string Path, string Text)> sources,
        ProductMethod target,
        IReadOnlyDictionary<string, List<string>> typeOwner)
    {
        var found = new HashSet<ProductMethod>();
        bool unattributed = false;

        foreach ((string path, string text) in sources)
        {
            string file = SourceScan.Relative(path);
            string[] lines = text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains(target.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                bool isDeclaration = MemberDeclaration.Match(line) is { Success: true } declaration
                    && declaration.Groups["name"].Value == target.Name;

                foreach (Match call in CallSite.Matches(line))
                {
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

                    if (EnclosingMethod(lines, i) is not { } enclosing
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
    /// <returns>The declared name and its one-based line, or <c>null</c>.</returns>
    private static (string Name, int Line)? EnclosingMethod(string[] lines, int index)
    {
        int floor = Math.Max(0, index - MaxEnclosingWalk);
        for (int i = index; i >= floor; i--)
        {
            Match declaration = MemberDeclaration.Match(lines[i]);
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
    /// <param name="testSources">Test sources as absolute path / comment-stripped text.</param>
    /// <returns>One entry per test method that drives a dead router, plus any skip, sorted.</returns>
    internal static IReadOnlyList<string> TestsThatDriveOnlyDeadRoutes(
        KeyRouteReport report,
        IReadOnlyList<(string Path, string Text)> testSources)
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
        foreach ((string path, string text) in testSources)
        {
            string file = SourceScan.Relative(path);
            string[] lines = text.Split('\n');
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
}

/// <summary>
///     Guard for issue #857: a key hand-off into the panel/overlay input seam must
///     be reachable from product code, and a test that is the only caller of an
///     unreachable hand-off is not evidence that the input works.
/// </summary>
public sealed class PanelKeyRouteReachabilityRule
{
    private static readonly Lazy<KeyRouteReport> Report = new(
        () => PanelKeyRouteProbe.Scan(PanelKeyRouteProbe.ReadProductTrees()));

    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> Tests = new(
        () => PanelKeyRouteProbe.ReadTestTrees());

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

        var wired = PanelKeyRouteProbe.Scan([("SyntheticRouter.cs", Wired)]);
        var orphan = PanelKeyRouteProbe.Scan([("SyntheticRouter.cs", Orphan)]);

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
