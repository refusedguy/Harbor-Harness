// OverlayKeyPlaneCensusRule.cs — MEASUREMENT + ratchet for issue #812.
//
// WHAT #812 CLAIMS, AND WHAT THE TREE SAYS
// -----------------------------------------
// The issue says `OverlayStack.RouteKey` has "5 implementations of
// `IOverlayLayer` in product builds" behind it and no product call site. Both
// halves were re-measured against the tree, and neither survives as written:
//
//   * There are EIGHT `IOverlayLayer` implementations under `src/` + `apps/`
//     (#400 seated `MarkupOverlayLayer` as the sixth registered layer; the two
//     the issue missed are `ToastOverlayLayer` (which IS registered and pushed
//     every frame) and `WhichKeyHelpOverlayLayer`.
//
//   * "Five layers behind it" conflates three different states that this
//     project keeps apart, and the difference decides what any fix costs.
//     Measured, the eight fall into three buckets:
//
//       REGISTERED + READS KEYS + CAN BE SHOWN (2)
//         ImageViewerOverlayLayer     field _imageLayer, pushed at
//                                    ChatScreenLayout.cs:875; opened at
//                                    ReplInputLoop.cs:429; IsModal = true.
//         SetupChecklistOverlayLayer  field _setupLayer, pushed at :863;
//                                    opened at SetupCommand.cs:27;
// check-doc-cites: record-drift SetupCommand.cs:27 now="ambiguous:2" [2 tracked files share this basename, so the citation does not identify one] -->
//                                    IsModal = false.
//
//       REGISTERED BUT PARKED (3)
//         DialogOverlayLayer, ToastOverlayLayer — pushed every frame, but
//           no product code calls `.Show*` on the overlay they wrap, so
//           `Visible` is permanently false and they never enter the stack.
//           Both also take the `IOverlayLayer.OnKey` default (`=> false`).
//         DiffViewerOverlayLayer — pushed every frame, overrides `OnKey`,
//           declares `IsModal = true`, and no product code calls
//           `DiffViewer.Show(`. It is the one parked layer that LOOKS armed.
//
//       NEVER CONSTRUCTED IN PRODUCT (2)
//         WhichKeyHelpOverlayLayer, CellForgeJumpPaletteOverlayLayer —
//         `new <Layer>(` appears only under `tests/`.
//
// So the honest count is "two layers whose keys the host routes by hand, three
// that only ever paint-never, two that no product code builds". That is why
// #812's own proposal (a) and (b) are not symmetric bills, and it is measured
// here so the next reader does not have to re-derive it.
//
// WHY THIS FILE IS NOT A FIX
// --------------------------
// The stack is ALIVE — `ChatScreenLayout.SyncOverlays` pushes six layers and
// `LayoutTree.PaintAll` calls `Overlays.PaintOver` on every frame. The half
// that is dead is the key half: `RouteKey`, `HasModalBarrier`, `TopModal` and
// `HitTest` have no product reader at all, and `PanelKeyRouteProbe.Ledger`
// already carries `OverlayStack.RouteKey` as acknowledged debt under this
// issue. Wiring or deleting it is a product decision bounded by the feature
// freeze #555, and neither resolution is this file's to make.
//
// So this file does the only thing that is neither: it pins the census, so
// the decision cannot be taken against stale prose, and it pins the fact that
// the PAINT half is live — because the single most likely wrong move here is
// to read "RouteKey has no caller" as "the overlay stack is dead" and delete a
// z-order that six layers are painted through every frame.
//
// WHERE THE DOOR IS, AND WHY IT IS NOT FREE
// ----------------------------------------
// The door is `ReplInputLoop.HandleKeyAsync`
// (`apps/Harbor.App.Cli/Repl/ReplInputLoop.cs:272`) — the one product key
// ingress in the CLI. #857's agent stood at it and declined to open it, and
// the reason is structural rather than cautious: the stack cannot express the
// live precedence order, because one of the modals the host routes is not a
// layer at all. Measured:
//
//   live cascade      1.Images  2.ctrl+P/ctrl+J  3.palette  4.Setup  5..10 gates
//   stack top-down    toast, image, setup, diff, dialog
//
//   `CommandPaletteView` is NOT an `IOverlayLayer` and is not pushed by
//   `SyncOverlays`, so there is no position at which a single `RouteKey` call
//   reproduces the cascade: calling it at 1 moves the palette below the setup
//   checklist; calling it at 3 moves the image viewer below the palette, which
//   #387 makes deliberate ("a zoomed screenshot must never be typed into").
//   Seating the palette as a layer is a NEW cellforge widget primitive, which
//   #555 freezes by name.
//
// That is the price, and it is a product decision. The ratchet below fails the
// day either resolution starts, so the decision is made once, explicitly.
//
// WHAT THIS GATE CANNOT SEE — measured, not assumed
// -------------------------------------------------
// Four blind spots, each verified by mutating the tree and re-running the matcher
// rather than by reasoning about it:
//
//   1. IT DOES NOT RE-DERIVE VISIBILITY. "Can this layer become visible?" needs a
//      receiver-to-type resolver, because the product reaches the same instance
//      under two names — `host.Images.HandleKey` and
//      `new ImageViewerOverlayLayer(ImageViewer)` are one object — and under
//      `SetupChecklistController.Overlay => _host.Screen.SetupChecklist` for the
//      other live layer. A matcher loose enough to catch a new `.Show()`
//      producer for `DialogOverlay` also matches `_toasts.Show(..., ToastKind.X)`
//      in the Avalonia host, which is a different type on a different stack.
//      So this gate CANNOT see a parked layer being given a product `.Show()`
//      producer — which is precisely the moment the two routing copies would
//      begin to diverge, and the single most important thing #812 predicts.
//      Stated here rather than hidden: when you give `DiffViewerOverlay.Show`,
//      `DialogOverlay.ShowAlert` or `ToastOverlay.Show` its first product caller,
//      this gate stays green and the divergence is yours to catch. The
//      `DiffViewerOverlayLayer` row is the one to look at — it overrides `OnKey`
//      and declares `IsModal = true` while parked, so it is the layer that would
//      start claiming keys the moment something showed it.
//
//   2. "ARMS KEYS" MEANS "DECLARES `OnKey`", not "consumes a key". A layer whose
//      body is `=> false` still counts as armed. That is deliberate — the
//      declaration is the stable shape, and a body that starts returning false is
//      a behavioural edit, not a change of census.
//
//   3. WIRING `RouteKey` LEAVES THIS GATE GREEN, on purpose. This file watches
//      the census; `PanelKeyRouteReachabilityRule` watches the orphan router, and
//      its `TheAcknowledgedDebtIsExactlyWhatTheScanStillFinds` fails the moment
//      its `#812` ledger entry stops being an orphan. Two gates, one debt, each
//      watching its own half — so opening the door cannot go unnoticed, but it
//      will not be noticed HERE.
//
//   4. `LAZY_LAYER_FIELD` matches any `_x ??= new Y(`, so `fieldToLayer` also
//      collects unrelated fields (`_setup` -> `SetupChecklistController`, and so
//      on). It is filtered by the pushed-field set and by the layer name before
//      it is used, so the noise cannot reach a verdict — but the map is wider
//      than the thing it is used for, and that is a fact about it, not a defect.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One measured fact about one overlay layer, as a source scan can see it.</summary>
/// <param name="Name">The layer class name.</param>
/// <param name="File">Repo-relative file declaring it.</param>
/// <param name="ConstructedInProduct">Whether `new <c>Name</c>(` appears outside the test tree.</param>
/// <param name="Registered">
///     Whether product code pushes it onto an <c>OverlayStack</c>: a field assigned
///     `??= new <c>Name</c>(` that is also the argument of an <c>Overlays.Push(</c>.
/// </param>
/// <param name="ReadsKeys">Whether it overrides <c>public bool OnKey(in KeyEvent key)</c>.</param>
internal readonly record struct OverlayLayerCensus(
    string Name,
    string File,
    bool ConstructedInProduct,
    bool Registered,
    bool ReadsKeys)
{
    /// <summary>The readable form used in failure messages.</summary>
    /// <returns>The layer name.</returns>
    public override string ToString() => Name;
}

/// <summary>
///     The scanner behind <see cref="OverlayKeyPlaneCensusRule" />. Internal rather than
///     private because the non-vacuity controls must exercise the SAME matcher the rule
///     uses, and a control that ran a copy could pass while the rule matched nothing.
/// </summary>
internal static class OverlayKeyPlaneProbe
{
    /// <summary>The layer declaration form: a sealed class implementing the overlay seam.</summary>
    internal static readonly Regex LayerDeclaration =
        new(@"public\s+sealed\s+class\s+(?<name>\w+)\s*:\s*IOverlayLayer\b", RegexOptions.Compiled);

    /// <summary>The key-reading override. Its absence means the interface default `=&gt; false`.</summary>
    internal static readonly Regex ReadsKeys =
        new(@"public\s+bool\s+OnKey\s*\(\s*in\s+KeyEvent\s+key\s*\)", RegexOptions.Compiled);

    /// <summary>A lazily-constructed layer field, as <c>SyncOverlays</c> declares them.</summary>
    internal static readonly Regex LazyLayerField =
        new(@"(?<field>_\w+)\s*\?\?=\s*new\s+(?<layer>\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>A push onto an overlay stack, capturing the field being pushed.</summary>
    internal static readonly Regex PushOntoStack =
        new(@"Overlays\.Push\s*\(\s*(?<field>_\w+)\s*\)", RegexOptions.Compiled);

    /// <summary>
    ///     Measures every overlay layer in the product trees.
    /// </summary>
    /// <param name="sources">Product sources as repo-relative path / comment-stripped text.</param>
    /// <returns>One row per layer, sorted by name.</returns>
    internal static IReadOnlyList<OverlayLayerCensus> Scan(
        IReadOnlyList<(string Path, string Text)> sources)
    {
        // field -> layer type, for every `_xLayer ??= new SomeLayer(` in product.
        var fieldToLayer = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string _, string text) in sources)
        {
            foreach (Match match in LazyLayerField.Matches(text))
            {
                fieldToLayer[match.Groups["field"].Value] = match.Groups["layer"].Value;
            }
        }

        // Every field that is the argument of an `Overlays.Push(` is registered.
        var pushedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string _, string text) in sources)
        {
            foreach (Match match in PushOntoStack.Matches(text))
            {
                pushedFields.Add(match.Groups["field"].Value);
            }
        }

        var rows = new List<OverlayLayerCensus>();
        foreach ((string path, string text) in sources)
        {
            foreach (Match match in LayerDeclaration.Matches(text))
            {
                string name = match.Groups["name"].Value;
                bool readsKeys = ReadsKeys.IsMatch(text);

                bool constructed = sources.Any(other =>
                    !other.Path.Equals(path, StringComparison.Ordinal)
                    && other.Text.Contains("new " + name + "(", StringComparison.Ordinal));

                bool registered = fieldToLayer.Any(pair =>
                    pair.Value == name && pushedFields.Contains(pair.Key));

                rows.Add(new OverlayLayerCensus(
                    name,
                    SourceScan.Relative(path),
                    constructed,
                    registered,
                    readsKeys));
            }
        }

        rows.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return rows;
    }
}

/// <summary>
///     The measurement #812's own numbers got wrong, pinned as a ratchet — plus the
///     one fact that decides whether the stack may be deleted at all.
/// </summary>
public sealed class OverlayKeyPlaneCensusRule
{
    /// <summary>
    ///     The measured baseline, declared so that a change in either direction is an
    ///     explicit edit. See the file header for how each row was measured.
    /// </summary>
    internal static readonly string[] RegisteredLayers =
    [
        "DialogOverlayLayer",
        "DiffViewerOverlayLayer",
        "ImageViewerOverlayLayer",
        "MarkupOverlayLayer",
        "SetupChecklistOverlayLayer",
        "ToastOverlayLayer",
    ];

    /// <summary>Registered layers that take the <c>OnKey</c> default, so they cannot read a key.</summary>
    internal static readonly string[] RegisteredButCannotReadKeys =
    [
        "DialogOverlayLayer",
        "MarkupOverlayLayer",
        "ToastOverlayLayer",
    ];

    /// <summary>Layers no product code constructs — <c>new Layer(</c> appears only under <c>tests/</c>.</summary>
    internal static readonly string[] NeverConstructedInProduct =
    [
        "CellForgeJumpPaletteOverlayLayer",
        "WhichKeyHelpOverlayLayer",
    ];

    /// <summary>
    ///     The two registered layers whose keys <c>ReplInputLoop</c> reaches directly, at
    ///     <c>ReplInputLoop.cs:339</c> (<c>host.Images.HandleKey</c>) and
    ///     <c>ReplInputLoop.cs:414</c> (<c>host.Setup.HandleKey</c>) — to the same overlay
    ///     instances their layers wrap, so nothing about today's behaviour depends on the
    ///     stack being the router. Declared rather than derived because detecting the
    ///     hand-off needs the same receiver-to-type resolution limit 1 in the header names.
    /// </summary>
    internal static readonly string[] HostRoutedLayers =
    [
        "ImageViewerOverlayLayer",
        "SetupChecklistOverlayLayer",
    ];

    /// <summary>
    ///     Registered, declares <c>OnKey</c>, and is NOT one of <see cref="HostRoutedLayers" /> —
    ///     i.e. a layer that would claim keys if the stack were the router, and claims none
    ///     today. It is one layer, and it is parked: nothing in product calls
    ///     <c>DiffViewer.Show</c>, so it is never visible and the set is empty in practice.
    ///     This row exists so that disarming it, or arming a second one, is an explicit edit.
    /// </summary>
    internal static readonly string[] ArmedButNotHostRouted =
    [
        "DiffViewerOverlayLayer",
    ];

    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> Sources = new(Read);

    private static readonly Lazy<IReadOnlyList<OverlayLayerCensus>> Census = new(
        () => OverlayKeyPlaneProbe.Scan(Sources.Value));

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
    ///     The layers the tree actually has. Every other assertion in this file is
    ///     about a subset of this, so an empty or shrunken set would make them
    ///     vacuously true.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheScanFindsTheOverlayLayersItIsAboutToJudge()
    {
        IReadOnlyList<OverlayLayerCensus> rows = Census.Value;
        int files = Sources.Value.Count;

        await Assert.That(files).IsGreaterThan(500).Because(
            "a source scan that read almost nothing measures an empty tree rather than a "
            + "clean one. RepoPaths.RepoRoot was "
            + (RepoPaths.RepoRoot is null ? "null" : "found") + " and " + files
            + " product files were read.");

        await Assert.That(rows.Count).IsGreaterThanOrEqualTo(7).Because(
            "#812 named five and the tree has seven; the floor is what makes a rename, a "
            + "moved file or a broken regex red here instead of silently emptying the "
            + "census. Measured layers: " + Describe(rows));

        await Assert.That(rows.Count(r => r.Registered)).IsGreaterThanOrEqualTo(5).Because(
            "five layers are pushed onto the overlay stack by ChatScreenLayout.SyncOverlays; "
            + "if the push form stopped matching, the registered set would empty and every "
            + "comparison below would pass for the wrong reason. Measured: "
            + Describe(rows.Where(static r => r.Registered)));

        foreach (string expected in new[] { "ImageViewerOverlayLayer", "SetupChecklistOverlayLayer" })
        {
            bool seen = rows.Any(r => r.Name == expected);
            await Assert.That(seen).IsTrue().Because(
                expected + " is one of the two layers whose keys the host routes by hand, so it "
                + "is the floor for 'the scan can still see a layer that both registers and reads "
                + "keys'. If it cannot be found, the row-matching assertions below compare "
                + "against nothing. Measured layers: " + Describe(rows));

            bool armedAndSeated = rows.Any(r =>
                r.Name == expected && r.Registered && r.ReadsKeys);
            await Assert.That(armedAndSeated).IsTrue().Because(
                expected + " is pushed by SyncOverlays and forwards a decoded key; if either half "
                + "stopped being true the routing picture #812 is about has changed, and the "
                + "baseline below needs re-measuring rather than silently re-baselining. Measured "
                + "row: registered=" + rows.Any(r => r.Name == expected && r.Registered)
                + " readsKeys=" + rows.Any(r => r.Name == expected && r.ReadsKeys));
        }
    }

    /// <summary>
    ///     The stack is not dead. This is the assertion that exists to stop the most
    ///     likely wrong move: reading "RouteKey has no product caller" as "the overlay
    ///     stack is unused" and deleting a z-order that paints on every frame.
    /// </summary>
    [Test]
    public async Task ThePaintHalfOfTheOverlayStackIsLive_SoAnUnreferencedRouteKeyIsNotADeadStack()
    {
        IReadOnlyList<(string Path, string Text)> sources = Sources.Value;

        await Assert.That(sources.Any(s => s.Text.Contains("Overlays.PaintOver(", StringComparison.Ordinal)))
            .IsTrue().Because(
            "LayoutTree.PaintAll calls Overlays.PaintOver(buffer) on every frame; that single "
            + "call is what makes OverlayStack live infrastructure rather than dead code, and "
            + "it is the fact the #812 deletion option silently throws away.");

        await Assert.That(sources.Count(s => s.Text.Contains("SyncOverlays(", StringComparison.Ordinal)))
            .IsGreaterThanOrEqualTo(2).Because(
            "SyncOverlays is the product entry point that registers layers; ReplLifecycle "
            + "calls it once per frame before PaintAll. If it stopped being called the layers "
            + "would never enter the stack and this whole census would be about nothing.");

        int pushes = sources.Sum(s => OverlayKeyPlaneProbe.PushOntoStack.Matches(s.Text).Count);
        await Assert.That(pushes).IsGreaterThanOrEqualTo(5).Because(
            "ChatScreenLayout.SyncOverlays pushes one layer per overlay it seats. The floor is "
            + "here so that emptying the registration path is red here rather than quietly "
            + "satisfying the baseline comparison below. Measured pushes: " + pushes);
    }

    /// <summary>
    ///     The census equals the measured baseline, in both directions: a layer that
    ///     becomes registered (or stops being one) fails until this table is edited, so
    ///     the decision #812 is about is taken against facts and cannot be taken twice.
    /// </summary>
    [Test]
    public async Task TheLayerCensusIsExactlyWhatTheBaselineRecords()
    {
        IReadOnlyList<OverlayLayerCensus> rows = Census.Value;

        string[] registered = rows.Where(static r => r.Registered).Select(static r => r.Name).ToArray();
        string[] cannotRead = rows.Where(static r => r.Registered && !r.ReadsKeys)
            .Select(static r => r.Name).ToArray();
        string[] neverBuilt = rows.Where(static r => !r.ConstructedInProduct)
            .Select(static r => r.Name).ToArray();

        await Assert.That(registered).IsEquivalentTo(RegisteredLayers).Because(
            "these six are pushed onto the stack by SyncOverlays. A seventh appearing means a "
            + "new overlay was seated, which changes what 'wiring RouteKey' would cost; one "
            + "disappearing means it was removed and this table is stale. Measured: "
            + Describe(registered));

        await Assert.That(cannotRead).IsEquivalentTo(RegisteredButCannotReadKeys).Because(
            "DialogOverlayLayer, MarkupOverlayLayer and ToastOverlayLayer take the IOverlayLayer.OnKey default, so "
            + "they cannot read a key even once something shows them. DiffViewerOverlayLayer is "
            + "deliberately NOT in this table: it overrides OnKey and declares IsModal = true, "
            + "which is what makes it the parked layer that looks armed. Measured: "
            + Describe(cannotRead));

        await Assert.That(neverBuilt).IsEquivalentTo(NeverConstructedInProduct).Because(
            "WhichKeyHelpOverlayLayer and CellForgeJumpPaletteOverlayLayer are built by tests "
            + "only; a product `new` for either of them is the first step of opening the door, "
            + "and it should be an explicit decision rather than an accident of a new widget. "
            + "Measured: " + Describe(neverBuilt));
    }

    /// <summary>
    ///     The armed-but-unrouted set: registered layers that declare <c>OnKey</c> without
    ///     being one the host routes by hand. On dev that is exactly one layer,
    ///     <c>DiffViewerOverlayLayer</c>, and it is harmless only because nothing in product
    ///     ever shows it. This is the row that goes red the day #812's predicted divergence
    ///     starts: give the diff viewer a product <c>Show</c> caller, or arm another parked
    ///     layer, and the two routing copies stop agreeing.
    /// </summary>
    [Test]
    public async Task TheArmedButUnroutedLayersAreOnlyTheParkedOne()
    {
        IReadOnlyList<OverlayLayerCensus> rows = Census.Value;

        string[] hostRouted = HostRoutedLayers;
        string[] armed = rows
            .Where(static r => r.Registered && r.ReadsKeys)
            .Select(static r => r.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expectedArmed = hostRouted
            .Concat(ArmedButNotHostRouted)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(armed).IsEquivalentTo(expectedArmed).Because(
            "these are the registered layers that declare OnKey. Two of them are the ones "
            + "ReplInputLoop routes by hand; the third, DiffViewerOverlayLayer, is the parked "
            + "layer that looks armed — it declares IsModal = true and forwards a key, and only "
            + "the absence of a product DiffViewer.Show( ) caller stops it from claiming one. "
            + "Measured: " + Describe(armed));

        await Assert.That(ArmedButNotHostRouted.OrderBy(static n => n, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(new[] { "DiffViewerOverlayLayer" }).Because(
            "the ONE layer that would claim keys through the stack without the host's "
            + "knowledge. It is listed by name because it is the row to look at on the day "
            + "something shows it: the layer starts consuming keys that the hand-written "
            + "cascade in ReplInputLoop never offers it, which is the divergence #812 predicts. "
            + "Resolve it by wiring RouteKey (one router) or by giving the diff viewer a "
            + "product Show() caller AND a place in the cascade — not by leaving it armed "
            + "and parked.");
    }

    private static string Describe(IEnumerable<OverlayLayerCensus> rows) =>
        Describe(rows.Select(static r => r.Name));

    private static string Describe(IEnumerable<string> names)
    {
        string[] list = names.OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        return list.Length == 0 ? "(none)" : string.Join(", ", list);
    }
}