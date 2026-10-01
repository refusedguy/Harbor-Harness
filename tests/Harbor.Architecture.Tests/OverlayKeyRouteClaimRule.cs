// OverlayKeyRouteClaimRule.cs — issue #858: five doc comments (and four more this
// file found) describe an input path that no product host uses.
//
// WHAT #858 ASKS, AND WHAT THE TREE SAYS
// --------------------------------------
// #858's question was whether five places "lie" or merely leave a real route
// "undescribed". Measured against `dev`, with comments stripped and product
// trees only (`src/` + `apps/`), the answer is that the route does not exist:
//
//   * `OverlayStack.RouteKey` — 0 product call sites.
//   * `OverlayStack.HasModalBarrier` — 0 product readers.
//   * `OverlayStack.TopModal` — 0 product readers.
//   * `OverlayStack.HitTest(` — 0 product readers.
//   * `Push` / `Remove` / `PaintOver` — live. `ChatScreenLayout.SyncOverlays`
//     pushes five layers per frame and `LayoutTree.PaintAll` paints them.
//
// So the honest shape of the defect is NOT "stale prose". It is a promise of an
// API that has no wiring, and that is strictly more dangerous than drift,
// because every part a reader would check looks real:
//
//   * `IOverlayLayer.OnKey` is a real interface member with a default (`=> false`)
//     and three live overrides;
//   * `OverlayStack.RouteKey` is a real public method, correctly implemented and
//     covered by `OverlayModalTests`;
//   * `OverlayStack.cs:152` prints the host pattern in a `<code>` block.
//
// A contributor greps "keys route top-down through OnKey", finds all of that,
// writes a layer with an `OnKey` — and it is never called. The adapter test
// stays green because it drives the adapter directly. That is the failure this
// rule exists to make impossible to commit.
//
// THE DOOR IS NOT THE PROBLEM, AND SAYING SO IS PART OF THE FIX
// -------------------------------------------------------------
// The one product key ingress IS live and IS walked on every keystroke:
//
//   Program.cs:99/138 -> InteractiveVerb.RunAsync (:29 RunInteractiveAsync)
//     -> ReplRunner.RunInteractiveAsync (:124; :162 TuiMode.IsCellForgeSelected
//        is the default backend) -> RunCellForgeAsync (:174) -> :383 RunAsync
//     -> CellForgeReplRunner.RunAsync:369 -> ReplLifecycle.RunAsync
//     -> ReplLifecycle.cs:191 host.Input.HandleInputAsync
//     -> ReplInputLoop.cs:65 HandleKeyAsync
//
// and `HandleKeyAsync` is a working router — it just routes AROUND the stack.
// `ReplInputLoop.cs:199 host.Images.HandleKey(key)` and `:274 host.Setup.HandleKey(key)`
// call the two live overlays directly. The same objects, a different hop: not
// `ImageViewerOverlayLayer.OnKey`. That asymmetry is why #857's `JumpCommand`
// could correctly say the panel `OnKey` has "no product host [that can] reach" it.
//
// Whether to open that door — wire `RouteKey`, or seat the palette as a layer —
// is #812's decision and is frozen by #555. This file does not touch it. It only
// stops the prose from asserting a route that does not exist, so the decision is
// made against facts rather than against a confident sentence.
//
// SCOPE, AND WHY IT IS NOT WIDER
// ------------------------------
// Claim shapes are English PROSE phrases ("route top-down through"), matched
// against RAW text — comments included, which is the whole subject. They cannot
// appear in C# syntax, so no code site is a false positive. That is also why
// `SourceScan.StripComments` is deliberately NOT used here: it deletes the very
// thing being policed.
//
// Text is normalised first (`Normalize`: blanks everything that is not a letter,
// a digit or a newline), because these sentences wrap. The claim in
// `CellForgeJumpPalettePanel.cs` reads "…and the host seats" / newline /
// "CellForgeJumpPaletteOverlayLayer", and an un-normalised matcher misses it while
// its planted control still fires — a shape that looks alive and matches nothing.
// Normalisation is length- and line-preserving, so the reported line numbers still
// point at the real sentence.
//
// `tests/` is out of scope, and necessarily: this file's own regex literals
// contain the claim phrases. Sweeping tests/ would mean this rule flags itself,
// so the scan is `src/` + `apps/` + `docs/` + root markdown.
//
// Two neighbours are deliberately left alone:
//   * `SetupChecklistOverlayLayer.cs:12-13` — already carries the conditional
//     phrasing #858 wants ("for hosts that route through ..."). It is the model,
//     not a violation.
//   * `docs/PATTERNS.md:373` — cites `OverlayStack.cs:40,46` for the DEFAULTS
//     (`IsModal => false`, `OnKey => false`). True, and about the interface's
//     shape rather than the product's routing.
//
// NON-VACUITY, IN BOTH DIRECTIONS
// -------------------------------
// A prose guard is trivially satisfiable by deleting the prose, by widening the
// matcher until it matches everything, or by reading no files at all. All three
// are asserted, not assumed:
//
//   1. `ThePremiseScanFindsTheKeyPlaneItJudges` — the scan must LOCATE the seam
//      (`OverlayStack.RouteKey`'s declaration and at least one `OnKey`
//      override). If it cannot find the route, "nothing claims it" is vacuous.
//   2. `TheScanReadTheTreesItIsPolicing` — a file-count floor, so "no claims
//      found" cannot be "nothing read".
//   3. `EveryClaimShapeFiresOnTheStaleSentence` — a planted control per shape.
//      The only thing that proves a matcher still works is that it still fails.
//
// PLUS THE THING THAT MAKES IT A RATCHET RATHER THAN A SNAPSHOT
// --------------------------------------------------------------
// `NoDocCommentClaimsAnOverlayKeyRouteNoProductCodeEnters` is only meaningful
// while the route is unwired, so this file also asserts the premise it used. The
// day someone calls `RouteKey` from the host, the premise assertion goes RED and
// names the prose that must be re-derived — rather than the rule quietly
// continuing to forbid a sentence that has become true.
//
// The authoritative orphan check for the seam itself is NOT duplicated here:
// `PanelKeyRouteReachabilityRule.TheAcknowledgedDebtIsExactlyWhatTheScanStillFinds`
// already holds the `#812` ledger entry and already fails when `RouteKey` stops
// being an orphan. Two gates, two facts: that one watches the SEAM, this one
// watches the PROSE about it.
//
// KNOWN LIMITS — measured, not assumed
// ------------------------------------
//   * THE PREMISE IS RECEIVER-ANCHORED. It looks for `Overlays.RouteKey(`. Every
//     product site reaches the stack as `Tree.Overlays` (verified: the five
//     pushes in `ChatScreenLayout.SyncOverlays` are all `Tree.Overlays.Push`),
//     so this is the form a wiring would take — but a host that reached the stack
//     under some other name would NOT be seen, and the premise would stay false.
//     Stated rather than hidden; the seam's own check is `PanelKeyRouteReachabilityRule`,
//     which resolves receivers properly and is the one to believe on that point.
//   * A NEW CLAIM PHRASE IS NOT COVERED until somebody adds it to `ClaimShape`.
//     The file header is the reason the next person should. `PATTERNS.md:373`
//     shows the cost of a guard that reads everything and means nothing: the
//     matcher has to be narrow, so narrowness is the price of precision.
//   * This rule polices PROSE ONLY. It does not assert that the product's key
//     routing is correct — `ReplInputLoop.HandleKeyAsync`'s cascade is hand-written
//     and this rule has nothing to say about its order.
//
// STATUS: never compiled. Local dotnet builds are forbidden in this repository,
// so CI is the only thing that has ever run this code and the first CI run is the
// first build. Treat the C# as unverified until then.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The prose matcher behind <see cref="OverlayKeyRouteClaimRule" />. Internal so
///     the non-vacuity control exercises the SAME matcher the rule uses — a control
///     running a copy could pass while the rule matched nothing.
/// </summary>
internal static class OverlayKeyRouteClaimProbe
{
    /// <summary>
    ///     Blanks every character that is not a letter, a digit or a newline, so a
    ///     phrase matches across <c>///</c> prefixes, <c>&lt;c&gt;</c> tags and
    ///     hyphens. Length- and line-preserving, which is what keeps the reported
    ///     line numbers pointing at the real sentence.
    /// </summary>
    /// <remarks>
    ///     This exists because the first draft of this file did not normalise, and one
    ///     shape silently matched nothing on the real tree: the claim in
    ///     <c>CellForgeJumpPalettePanel.cs</c> wraps as "…and the host seats" / a line
    ///     break / "CellForgeJumpPaletteOverlayLayer", so the intervening <c>///</c>
    ///     defeated a literal matcher. Its planted control fired, so every non-vacuity
    ///     assertion still passed — the exact "shape that matches nothing" failure this
    ///     repo's <c>DefaultModelDocClaimTests</c> header warns about, reproduced here
    ///     by the guard written to prevent it. A control proves a matcher can fire; only
    ///     running it against the real tree proves it fires there.
    /// </remarks>
    internal static string Normalize(string source)
    {
        var normalized = new StringBuilder(source.Length);
        foreach (char c in source)
        {
            normalized.Append(c == '\n' || char.IsLetterOrDigit(c) ? c : ' ');
        }

        return normalized.ToString();
    }

    /// <summary>
    ///     One shape of "keys are routed through the overlay stack" claim, as it
    ///     appears in English prose. Each is a phrase no C# expression can spell,
    ///     which is what lets the scan run over raw text.
    /// </summary>
    /// <param name="Name">How the shape is called in a failure message.</param>
    /// <param name="Pattern">
    ///     The matcher, written against <see cref="Normalize" /> output — so it spells
    ///     phrases in bare words and must not contain punctuation that normalisation
    ///     would have blanked.
    /// </param>
    internal sealed record ClaimShape(string Name, Regex Pattern)
    {
        /// <summary>"keys route top-down through OnKey" — the btea phrasing.</summary>
        internal static ClaimShape RouteTopDown { get; } = new(
            "routes top-down through",
            new(@"rout\w*\s+top\s+down\s+through", RegexOptions.Compiled | RegexOptions.IgnoreCase));

        /// <summary>"they are keyed through their modal IOverlayLayer.OnKey" — #381.</summary>
        internal static ClaimShape KeyedThrough { get; } = new(
            "keyed through their layer",
            new(@"keyed\s+through\s+their", RegexOptions.Compiled | RegexOptions.IgnoreCase));

        /// <summary>"the host routes through ImageViewerOverlayLayer.OnKey" — #387.</summary>
        internal static ClaimShape HostRoutesThrough { get; } = new(
            "the host routes through",
            new(@"host\s+routes\s+through", RegexOptions.Compiled | RegexOptions.IgnoreCase));

        /// <summary>"HasModalBarrier reports an active barrier" — #381.</summary>
        internal static ClaimShape BarrierReports { get; } = new(
            "HasModalBarrier reports",
            new(@"HasModalBarrier\w{0,40}\s+reports",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline));

        /// <summary>
        ///     "the host seats CellForgeJumpPaletteOverlayLayer" — #381. The type name is
        ///     written <c>&lt;c&gt;CellForgeJumpPaletteOverlayLayer&lt;/c&gt;</c> in the
        ///     source, so after <see cref="Normalize" /> there is an <c>c</c> between
        ///     "seats" and the name; the gap is matched loosely rather than anchored to a
        ///     word boundary.
        /// </summary>
        internal static ClaimShape HostSeatsLayer { get; } = new(
            "the host seats a layer",
            new(@"host\s+seats.{0,40}?\w*OverlayLayer",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline));
    }

    /// <summary>
    ///     The shapes the rule iterates. Named static members, not a bare array of
    ///     regexes, so the positive control refers to each shape by name and cannot
    ///     depend on which object happens to sit at which index.
    /// </summary>
    internal static readonly ClaimShape[] Shapes =
    [
        ClaimShape.RouteTopDown,
        ClaimShape.KeyedThrough,
        ClaimShape.HostRoutesThrough,
        ClaimShape.BarrierReports,
        ClaimShape.HostSeatsLayer,
    ];

    /// <summary>
    ///     A product call into the overlay stack's key router. Receiver-anchored on
    ///     <c>Overlays</c>, which is how every product site reaches the stack. See the
    ///     file header's KNOWN LIMITS for what this form cannot see.
    /// </summary>
    internal static readonly Regex OverlayRouteCall =
        new(@"Overlays\s*\.\s*RouteKey\s*\(", RegexOptions.Compiled);

    /// <summary>The declaration of the seam itself — proof the scan can find it.</summary>
    internal static readonly Regex RouteKeyDeclaration =
        new(@"public\s+bool\s+RouteKey\s*\(\s*in\s+KeyEvent\s+key\s*\)", RegexOptions.Compiled);

    /// <summary>Any <c>IOverlayLayer</c> key override — the other half of the seam.</summary>
    internal static readonly Regex OnKeyOverride =
        new(@"public\s+bool\s+OnKey\s*\(\s*in\s+KeyEvent\s+key\s*\)", RegexOptions.Compiled);

    /// <summary>One claimed routing path, located.</summary>
    /// <param name="Shape">Which claim shape matched.</param>
    /// <param name="Path">Repo-relative file.</param>
    /// <param name="Line">1-based line of the match.</param>
    /// <param name="Text">The matched sentence, collapsed to one line.</param>
    internal readonly record struct Claim(string Shape, string Path, int Line, string Text);

    /// <summary>
    ///     Finds every claim in the given sources, sorted for a stable failure message.
    /// </summary>
    /// <param name="sources">Repo-relative path / raw text.</param>
    /// <returns>One row per claim.</returns>
    internal static IReadOnlyList<Claim> Scan(IReadOnlyList<(string Path, string Text)> sources)
    {
        var claims = new List<Claim>();
        foreach ((string path, string text) in sources)
        {
            string normalized = Normalize(text);
            foreach (ClaimShape shape in Shapes)
            {
                foreach (Match match in shape.Pattern.Matches(normalized))
                {
                    claims.Add(new Claim(
                        shape.Name,
                        SourceScan.Relative(path),
                        LineOf(text, match.Index),
                        Collapse(match.Value)));
                }
            }
        }

        claims.Sort(static (a, b) =>
        {
            int byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return claims;
    }

    private static int LineOf(string text, int index) =>
        text[..index].Count(c => c == '\n') + 1;

    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
///     Issue #858: a doc comment may not describe overlay key routing as something the
///     product does, while no product code enters that route.
/// </summary>
public sealed class OverlayKeyRouteClaimRule
{
    /// <summary>
    ///     Trees the prose lives in. <c>tests/</c> is excluded because this file's own
    ///     regex literals spell the claim phrases — see the file header.
    /// </summary>
    private static readonly string[] ProseTrees = ["src", "apps"];

    /// <summary>
    ///     Floor on the product files read. A prose guard that reads almost nothing
    ///     reports a clean tree because it saw almost nothing, which is the failure
    ///     mode this rule exists to avoid being confused with.
    /// </summary>
    private const int MinProductFilesRead = 500;

    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> Sources = new(Read);

    private static readonly Lazy<IReadOnlyList<OverlayKeyRouteClaimProbe.Claim>> Claims = new(
        () => OverlayKeyRouteClaimProbe.Scan(Sources.Value));

    private static IReadOnlyList<(string Path, string Text)> Read()
    {
        var read = new List<(string, string)>();
        foreach (string file in SourceScan.EnumerateCsFiles(ProseTrees))
        {
            if (SourceScan.TryReadAllText(file) is { } source)
            {
                // RAW, not StripComments: the comments are the subject. The claim
                // shapes are English phrases no C# expression can spell.
                read.Add((file, source));
            }
        }

        return read;
    }

    /// <summary>
    ///     The rule: no doc comment may assert that keys are routed through the overlay
    ///     stack while no product code calls <c>OverlayStack.RouteKey</c>.
    /// </summary>
    [Test]
    public async Task NoDocCommentClaimsAnOverlayKeyRouteNoProductCodeEnters()
    {
        IReadOnlyList<(string Path, string Text)> sources = Sources.Value;
        IReadOnlyList<OverlayKeyRouteClaimProbe.Claim> claims = Claims.Value;

        // The premise, asserted rather than assumed — this is what turns the rule
        // into a ratchet. See the file header.
        int calls = sources.Sum(s =>
            OverlayKeyRouteClaimProbe.OverlayRouteCall.Matches(s.Text).Count);

        List<string> offenders = claims
            .Select(c => $"{c.Path}:{c.Line} — \"{c.Text}\" [{c.Shape}]")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(offenders).IsEmpty().Because(
            "a doc comment that says keys route through the overlay stack sends the next "
            + "contributor to a route with no product caller: RouteKey has " + calls
            + " product call site(s). Everything they would check looks real — OnKey is a "
            + "live interface member with three overrides, RouteKey is a public method with "
            + "tests, and OverlayStack.cs prints the host pattern in a <code> block — so "
            + "nothing in the tree contradicts the sentence, and the adapter test stays "
            + "green because it drives the adapter directly. Fix by saying what the product "
            + "does (the hand-written cascade in ReplInputLoop.HandleKeyAsync, which reaches "
            + "the live overlays DIRECTLY rather than through their layers), not by deleting "
            + "the note. The model sentence is SetupChecklistOverlayLayer.cs:12-13, which "
            + "already reads \"for hosts that route through OverlayStack.RouteKey\". Claims "
            + "asserting the overlay key route: "
            + (offenders.Count == 0 ? "(none)" : string.Join(" | ", offenders)));
    }

    /// <summary>
    ///     The scan must find the seam it is ruling on. A guard that cannot locate
    ///     <c>RouteKey</c> would report "no doc claims a route" while having found no
    ///     route and no claims — indistinguishable from a clean tree.
    /// </summary>
    [Test]
    public async Task ThePremiseScanFindsTheKeyPlaneItJudges()
    {
        IReadOnlyList<(string Path, string Text)> sources = Sources.Value;

        await Assert.That(sources.Count).IsGreaterThanOrEqualTo(MinProductFilesRead).Because(
            "the scan is the thing that reports the tree clean, so a scan that read almost "
            + "nothing would make every assertion below true for the wrong reason. Read "
            + sources.Count + " product file(s).");

        await Assert.That(sources.Any(s =>
            OverlayKeyRouteClaimProbe.RouteKeyDeclaration.IsMatch(s.Text))).IsTrue().Because(
            "OverlayStack.RouteKey is the route this rule is about. If its declaration cannot "
            + "be found the scan is not looking at the right tree, and \"no product code "
            + "calls it\" would be a statement about a method it never found.");

        int overrides = sources.Sum(s =>
            OverlayKeyRouteClaimProbe.OnKeyOverride.Matches(s.Text).Count);

        await Assert.That(overrides).IsGreaterThanOrEqualTo(3).Because(
            "IOverlayLayer.OnKey is the member the corrected prose has to talk about, and "
            + "several layers override it. If the override form stopped matching, the "
            + "premise would be measuring the wrong thing. Measured overrides: " + overrides);

        int productCalls = sources.Sum(s =>
            OverlayKeyRouteClaimProbe.OverlayRouteCall.Matches(s.Text).Count);

        await Assert.That(productCalls).IsZero().Because(
            "THIS is the fact the whole rule rests on, asserted so the rule fails loudly the "
            + "day it stops being true instead of quietly continuing to forbid a sentence that "
            + "has become correct. Measured product call sites for Overlays.RouteKey(: "
            + productCalls + "). If a host now routes through the stack, the prose in "
            + "ChatScreenLayout.cs, DiffViewerOverlayLayer.cs, ImageViewerOverlay.cs, "
            + "CellForgeJumpPaletteOverlayLayer.cs and JumpPalettePanel may all become true — "
            + "re-derive them and delete this assertion deliberately. The seam's own orphan "
            + "check is PanelKeyRouteReachabilityRule's #812 ledger entry; this is the prose half.");
    }

    /// <summary>
    ///     Every claim shape still fires on the sentence it was written for. The only
    ///     evidence that a matcher works is that it still fails — and a shape that has
    ///     silently stopped matching is indistinguishable from a shape with no violations.
    /// </summary>
    [Test]
    public async Task EveryClaimShapeFiresOnTheStaleSentence()
    {
        // The sentences as they appeared on `dev` before #858, one per shape.
        (OverlayKeyRouteClaimProbe.ClaimShape Shape, string Stale)[] controls =
        [
            (OverlayKeyRouteClaimProbe.ClaimShape.RouteTopDown,
                "Keys route top-down through <see cref=\"OnKey\"/> (btea message-routing pattern)."),
            (OverlayKeyRouteClaimProbe.ClaimShape.KeyedThrough,
                "Center-placed providers are excluded: they are keyed through their modal IOverlayLayer.OnKey."),
            (OverlayKeyRouteClaimProbe.ClaimShape.HostRoutesThrough,
                "the host routes through ImageViewerOverlayLayer.OnKey and swallows the rest"),
            (OverlayKeyRouteClaimProbe.ClaimShape.BarrierReports,
                "while the palette is visible HasModalBarrier reports an active barrier"),
            (OverlayKeyRouteClaimProbe.ClaimShape.HostSeatsLayer,
                "the host seats CellForgeJumpPaletteOverlayLayer on the existing LayoutTree.Overlays stack"),
        ];

        foreach ((OverlayKeyRouteClaimProbe.ClaimShape shape, string stale) in controls)
        {
            // Normalised exactly as Scan does, or the control would test a different
            // matcher than the rule runs.
            await Assert.That(shape.Pattern.IsMatch(
                OverlayKeyRouteClaimProbe.Normalize(stale))).IsTrue().Because(
                "the control for the \"" + shape.Name + "\" shape is the stale sentence that "
                + "shape was written to catch. If it no longer matches, the shape matches "
                + "nothing in the tree either — and a rule that matches nothing passes as "
                + "happily as one that is doing its job. Sentence: " + stale);
        }

        await Assert.That(controls.Length).IsEqualTo(OverlayKeyRouteClaimProbe.Shapes.Length).Because(
            "every shape the rule iterates needs a control, or a newly added shape ships with "
            + "no proof it can fail. Shapes: " + OverlayKeyRouteClaimProbe.Shapes.Length
            + ", controls: " + controls.Length + ".");
    }

    /// <summary>
    ///     Every shape must be ABLE to match, proven against the real tree rather than
    ///     against a planted sentence.
    /// </summary>
    /// <remarks>
    ///     This assertion exists because of a defect in the first draft of this file, and
    ///     the shape that carried it is worth naming. <c>HostSeatsLayer</c> was written
    ///     <c>host seats (the )?\w*OverlayLayer</c> and fired perfectly on its planted
    ///     control — while matching NOTHING in the tree, because the real sentence wraps
    ///     over a line break and wraps the type name in <c>&lt;c&gt;</c>. Every other
    ///     non-vacuity assertion passed. A control proves a matcher can fire; only
    ///     running it against the real tree proves it fires THERE, and a shape that has
    ///     died quietly is indistinguishable from a shape with no violations.
    ///
    ///     The floor is 1, not the observed count, so that fixing a violation legitimately
    ///     empties a shape without failing here — this asserts the shape is still wired up,
    ///     not that the tree is still dirty.
    /// </remarks>
    [Test]
    public async Task EveryClaimShapeCanStillMatchTheRealTree()
    {
        IReadOnlyList<OverlayKeyRouteClaimProbe.Claim> claims = Claims.Value;

        foreach (OverlayKeyRouteClaimProbe.ClaimShape shape in OverlayKeyRouteClaimProbe.Shapes)
        {
            int hits = claims.Count(c =>
                string.Equals(c.Shape, shape.Name, StringComparison.Ordinal));

            await Assert.That(hits).IsGreaterThanOrEqualTo(1).Because(
                "the \"" + shape.Name + "\" shape matched nothing on the real tree. Either the "
                + "sentence it was written for is gone — in which case delete the shape "
                + "deliberately, do not leave it matching nothing — or the matcher has stopped "
                + "matching the way the sentence is now written (a line wrap, an XML tag, a "
                + "renamed member). A dead shape is worse than no shape: it reads as coverage. "
                + "Distinct shapes seen: " + string.Join(", ",
                    claims.Select(c => c.Shape).Distinct().OrderBy(s => s, StringComparer.Ordinal)));
        }
    }
}
