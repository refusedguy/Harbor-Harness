// ExtensionAxisFreezeRule.cs — GUARD for issue #620.
//
// WHAT #555 FROZE
// ---------------
// #555 froze the axis of extension: a plugin may add a tool, a provider, an
// agent, a TUI plugin, a panel, a session store and a TUI backend, and it may
// not add an EIGHTH kind. A closed set is only closed if something checks it.
// Until this file, nothing did: the freeze was held by discipline, and
// discipline is not a gate. Opening a new axis cost one line on a public
// interface and no test noticed.
//
// WHAT AN AXIS IS, MECHANICALLY
// -----------------------------
// An axis has two halves and BOTH must exist for it to be real:
//
//   * the MARKER — a public interface a plugin implements to say "I contribute
//     a <kind>": IToolPlugin, IProviderPlugin, … This is what a plugin author
//     writes `class MyPlugin : IToolPlugin` against.
//   * the DOOR — a `Register*` method on IPluginLoadHost, the sink the host
//     offers: RegisterTool, RegisterProvider, …
//
// A marker with no door is an axis a plugin can announce and never deliver. A
// door with no marker is a capability the interface advertises that no plugin
// can ever reach. Either way the axis set is a lie, and a lie inside a sealed
// set is worse than an honest gap, because the next reader trusts it.
//
// HOW EACH HALF IS DETECTED, AND WHY
// ----------------------------------
//   * Doors are read by REFLECTION off IPluginLoadHost. Exact, and it catches a
//     new door whether or not anything dispatches it — a door nobody calls is
//     precisely the shape #620 found.
//   * Markers are read by REFLECTION over every product assembly, keeping
//     public interfaces assignable to IPlugin.
//
// The marker half is deliberately NOT a name scan. A name scan for
// `I…Plugin` also matches `IAppReducerPlugin`
// (src/Harbor.Ui.Framework.State/State/AppReducer.cs:48) — a UI-domain
// reducer hook where "plugin" means "reducer plugin", not "Harbor plugin". It
// is not an extension axis, it is not IPlugin-deriving, and a rule that
// flagged it would be wrong about code that has nothing to do with the freeze.
// Keying on the interface's TYPE rather than its spelling is what keeps this
// rule from blocking an honest change, which is the whole reason it has to be
// narrow.
//
// WHAT #620 ACTUALLY FOUND
// ------------------------
// Two of the seven axes are HALF-OPEN — a door with no marker.
// `IPluginLoadHost` declares `RegisterSessionStore` and `RegisterTuiBackend`
// (src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs). Both were opened
// deliberately (#581, #584) and everything AROUND them is
// built and wired: `PluginSessionStoreFactory` wraps a plugin store, the
// storage registry folds plugin ids in and `StorageModule` reads them;
// `PluginTuiBackend` does the same for renderers and `TuiModule` folds those
// in. The registries are live, the module wiring is live, and both methods
// carry XML documentation describing the capability in detail.
//
// What is missing is the third line. `PluginRegistrar.Register` — the only
// place a plugin instance is ever dispatched — has five branches, for the
// five markers that exist. Nothing anywhere calls the other two doors:
//
//   * the only `is I…Plugin` dispatches in the tree are the five in
//     PluginRegistrar.cs:75, :85, :89, :93 and :99;
//   * a plugin is never handed the host. `PluginContext` carries Services,
//     Configuration, LoggerFactory, EventBus, PluginDirectory and
//     DataDirectory — not IPluginLoadHost. The host instance goes only to
//     IPluginRegistrar.Register and the loaders, which are host internals.
//
// So the storage and renderer axes #581/#584 built in full are unreachable
// from a plugin today: the interface promises a plugin can add a session
// store, and no plugin can. That is the freeze's own failure mode — an axis
// half-opened — and it survived because nothing was counting axes.
//
// BOUNDARY OF THE "NOTHING CALLS IT" CLAIM
// -----------------------------------------
// InMemoryEventBus's ring (src/Harbor.Registries/Events/InMemoryEventBus.cs:48)
// records the opposite lesson: "'no production callers' is not a provable claim
// here", because a live bus is handed to out-of-tree plugin code through
// `IPluginLoadHost.EventBus`. That caution does not transfer here, and the
// difference is why this rule is allowed to make the claim: EventBus is a
// PROPERTY plugins are given, while the two doors are METHODS on a host object
// no plugin is ever given. The test below is therefore "is this method
// INVOKED ON A RECEIVER somewhere under src/", which a property hand-off cannot
// fake and a bare declaration cannot satisfy. A door reached reflectively or
// through a delegate would be missed; that boundary is stated here rather than
// hidden, and it is the one shape this rule does not cover.
//
// WHAT THIS RULE DOES **NOT** FORBID
// ----------------------------------
// The axis is frozen; development is not. This rule grades exactly two
// things: whether the marker/door set is still the frozen set, and whether
// each axis's two halves are present and connected.
//
// It does NOT forbid new files, new classes, new tools, new tests, new views,
// or new IMPLEMENTATIONS of an existing interface. A new `MyTool : ITool`, a
// fourth `ITuiRendererFactory`, a second `IPluginLoadHost` implementation and
// a new test file are all additions WITHIN an existing axis and are none of
// this rule's business. A rule that blocked those would be blocked by the
// first honest change and then switched off — the failure mode #620 exists to
// avoid. `PositiveControl_DetectsARealNewAxis_AndIgnoresARealNonAxis` is
// where that promise is kept honest.
//
// PERIMETER
// ---------
// src + apps, mirroring SessionStatusTableRule. `contrib/` is outside CI and
// unsupported by owner decision. `tests/` and `samples/` CONSUME the axes
// rather than declare them — a sample plugin is the reason the axis exists, and
// grading consumers would forbid it. The reflection sweep applies the same
// exclusion to test and benchmark assemblies by name.
//
// KNOWN ASYMMETRY, RECORDED NOT ENFORCED
// -------------------------------------
// `ITuiPlugin` does not derive from `IPlugin`; the other four markers do. That
// is a real inconsistency, but unifying it is a layering change
// (Harbor.Terminal.Abstractions must not be pulled into Harbor.Abstractions)
// and not this issue's business. It is the reason `StandaloneMarkers` exists:
// the one marker the type test cannot find has to be named, and naming it is
// the deliberate single-place edit a new axis now requires.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan really walked a checkout, and every declared
//      marker was found in it, so a broken path reports zero axes rather than
//      a correct answer.
//   2. PositiveControl_DetectsARealNewAxis_AndIgnoresARealNonAxis — THE PROOF
//      OF DISCRIMINATION. The probe is handed a real new axis and must flag
//      both of its halves; then it is handed five real NON-axes — a new tool
//      class, an extra factory implementation, an internal plugin-shaped
//      interface, a door call site, a doc comment naming a hypothetical axis —
//      and must flag none. A probe whose matchers stopped matching reports
//      nothing and this rule goes green while enforcing nothing, which is the
//      state #620 found the freeze in.

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One axis of extension: the plugin-implementable marker and the host door
///     that receives it. The PAIR is the unit — a half is not an axis.
/// </summary>
/// <param name="Marker">The public interface a plugin implements.</param>
/// <param name="Door">The <c>Register*</c> member on <see cref="IPluginLoadHost" />.</param>
internal sealed record SealedAxis(string Marker, string Door);

/// <summary>What one repository sweep and scan found about the axis surface.</summary>
/// <param name="Markers">Public <c>IPlugin</c>-deriving interfaces found in the product assemblies.</param>
/// <param name="DispatchedMarkers">Markers named by an <c>is I…Plugin</c> dispatch branch.</param>
/// <param name="OpenedDoors">Doors found INVOKED on a receiver, i.e. actually reachable.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
/// <param name="AssembliesSwept">How many product assemblies the reflection sweep covered.</param>
internal sealed record ExtensionAxisScan(
    IReadOnlyList<string> Markers,
    IReadOnlyList<string> DispatchedMarkers,
    IReadOnlyList<string> OpenedDoors,
    int FilesScanned,
    int AssembliesSwept);

/// <summary>
///     Finds the axis surface: which markers exist, which are dispatched, and
///     which doors are actually invoked.
/// </summary>
internal static partial class ExtensionAxisProbe
{
    /// <summary>
    ///     The root every axis marker extends — the base contract, not an axis
    ///     in itself, so it is excluded from the counted set.
    /// </summary>
    internal const string BaseMarker = nameof(IPlugin);

    /// <summary>Repository roots the source scan walks. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] ScanRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the source scan.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", "external", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    ///     Assemblies whose names carry one of these are test/benchmark
    ///     assemblies. They CONSUME axes — a test plugin implements
    ///     <c>IToolPlugin</c>, it does not declare a new one — so they are
    ///     outside the perimeter for the reflection sweep too. Applied by name
    ///     rather than by directory so it holds for any referenced test project.
    /// </summary>
    private static readonly string[] TestAssemblyMarkers = [".Tests", ".Test", ".Benchmarks", ".E2E"];

    /// <summary>
    ///     The doors of a load host: public instance methods declared on the
    ///     interface itself, returning <see cref="Result" />. Properties and
    ///     inherited members are excluded, so the count is the axis count and not
    ///     a count of everything the type happens to expose.
    /// </summary>
    internal static IReadOnlyList<string> ReadDoors(Type hostInterface)
    {
        ArgumentNullException.ThrowIfNull(hostInterface);

        return
        [
            .. hostInterface
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => typeof(Result).IsAssignableFrom(m.ReturnType))
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The axis markers among <paramref name="candidates" />: public
    ///     interfaces a plugin can implement, i.e. assignable to
    ///     <see cref="IPlugin" />, minus the base itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A type counts as a marker when it is a public INTERFACE. The
    ///         <c>internal interface I…Plugin</c> is deliberately not one:
    ///         no plugin can implement it, so it is not an axis, and grading it
    ///         would forbid a private helper that happens to be named like one.
    ///     </para>
    ///     <para>
    ///         The type filter is what keeps <c>IAppReducerPlugin</c> out. It ends
    ///         in "Plugin", it is public, and it is not <c>IPlugin</c>-deriving —
    ///         it is a UI-domain reducer hook that never reaches a load host. A
    ///         name-based matcher would flag it, and this rule would then be
    ///         wrong about code that has nothing to do with the freeze. Keying on
    ///         the type rather than the spelling is what keeps the rule from
    ///         blocking an honest change, which is the whole reason it has to be
    ///         narrow.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<string> ReadMarkers(IEnumerable<Type> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return
        [
            .. candidates
                .Where(t => t.IsInterface && (t.IsPublic || t.IsNestedPublic))
                .Where(t => typeof(IPlugin).IsAssignableFrom(t))
                .Where(t => t.Name != BaseMarker)
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
        ];
    }

    /// <summary>Every product-assembly type the reflection sweep should consider, and how many assemblies it covered.</summary>
    internal static (IReadOnlyList<Type> Types, int Assemblies) ProductTypes()
    {
        var types = new List<Type>();
        int assemblies = 0;

        foreach (Assembly assembly in ArchitectureTestHelpers.LoadHarborAssemblies().Values)
        {
            string name = assembly.GetName().Name ?? string.Empty;
            if (TestAssemblyMarkers.Any(m => name.Contains(m, StringComparison.Ordinal)))
            {
                continue;
            }

            assemblies++;
            try
            {
                types.AddRange(assembly.GetExportedTypes());
            }
            catch (ReflectionTypeLoadException ex)
            {
                types.AddRange(ex.Types.Where(t => t is not null).Select(t => t!));
            }
            catch (Exception ex)
            {
                // An assembly that will not enumerate is covered by its own
                // layering tests; skipping it here must not crash this rule.
                _ = ex;
            }
        }

        return (types, assemblies);
    }

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static ExtensionAxisScan Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new ExtensionAxisScan([], [], [], 0, 0);
        }

        var dispatched = new SortedSet<string>(StringComparer.Ordinal);
        var opened = new SortedSet<string>(StringComparer.Ordinal);
        int scanned = 0;

        foreach (string file in EnumerateSources(repoRoot))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            scanned++;
            ScanSource(lines, dispatched, opened);
        }

        IReadOnlyList<Type> types;
        int assemblies;
        (types, assemblies) = ProductTypes();

        return new ExtensionAxisScan(
            ReadMarkers(types),
            [.. dispatched],
            [.. opened],
            scanned,
            assemblies);
    }

    /// <summary>
    ///     Scans already-read lines. Exposed so the positive control drives the
    ///     REAL matchers — comment stripping included — rather than a second
    ///     implementation of them, which is the only way "it can fail" means
    ///     anything.
    /// </summary>
    internal static void ScanSource(
        string[] lines,
        SortedSet<string> dispatchedMarkers,
        SortedSet<string> openedDoors)
    {
        // Comments are stripped first: every axis in this repo is justified in
        // prose, and `/// public interface ITelemetryPlugin` in a doc comment
        // would otherwise read as a dispatch of a marker that does not exist.
        string[] clean = SourceCommentStripper.StripAll(lines);

        foreach (string line in clean)
        {
            foreach (Match match in DispatchedMarker().Matches(line))
            {
                dispatchedMarkers.Add(match.Groups["name"].Value);
            }

            foreach (Match match in OpenedDoor().Matches(line))
            {
                openedDoors.Add("Register" + match.Groups["name"].Value);
            }
        }
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (string root in ScanRoots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                if (SkippedDirectories.Any(dir =>
                        path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    /// <summary>
    ///     A DISPATCHED marker: <c>if (plugin.Instance is ITelemetryPlugin p)</c>.
    /// </summary>
    /// <remarks>
    ///     The name is anchored so it must END in <c>Plugin</c>. The
    ///     plugin-adjacent host types — <c>IPluginHost</c>, <c>IPluginRegistrar</c>,
    ///     <c>IPluginLoadHost</c>, <c>IPluginInstantiator</c>, <c>IPluginAuditLog</c>
    ///     — all start with <c>IPlugin</c> and none of them is an axis; only
    ///     the trailing <c>\b</c> separates the two families.
    /// </remarks>
    [GeneratedRegex(@"\bis\s+(?<name>I\w*Plugin)\b")]
    private static partial Regex DispatchedMarker();

    /// <summary>
    ///     An OPENED door: a <c>Register…</c> call ON A RECEIVER, e.g.
    ///     <c>host.RegisterTool(tool)</c>.
    /// </summary>
    /// <remarks>
    ///     The leading dot is the whole point. It separates an invocation from a
    ///     declaration, so <c>public Result RegisterTool(…)</c> — which has no
    ///     dot — cannot satisfy the test, and a half-open axis cannot be made to
    ///     look wired by writing the method again. The captured name must be
    ///     non-empty after <c>Register</c> so an unrelated
    ///     <c>_tools.Register(tool)</c> on some registry is not mistaken for a
    ///     door. Unrelated <c>.Register…</c> calls elsewhere
    ///     (<c>JsonProviderDiscovery.RegisterJsonProviders</c>) are collected but
    ///     ignored: the rule asks whether every DECLARED door is opened, never
    ///     the reverse.
    /// </remarks>
    [GeneratedRegex(@"\.Register(?<name>\w+)\s*\(")]
    private static partial Regex OpenedDoor();
}

/// <summary>
///     Guard for issue #620: the extension axis sealed by #555 is still the
///     frozen set, and every axis in it is a WHOLE axis — a plugin-implementable
///     marker and a host door, the two actually connected — rather than a
///     promise on an interface that nothing can keep.
/// </summary>
public sealed class ExtensionAxisFreezeRule
{
    /// <summary>
    ///     THE FROZEN SET. #555: these seven axes, no more.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The last two pairs are what #620 added, and adding them here
    ///         rather than deleting the doors they pair with is deliberate. The
    ///         doors, the registries (<c>PluginSessionStoreFactory</c>,
    ///         <c>PluginTuiBackend</c>), the composition-context plumbing and the
    ///         module wiring for both axes all ship today and are exercised by
    ///         <c>PluginBackendDoorTests</c>. Only the marker and the dispatch
    ///         branch were never written, so the axis was opened at the host end
    ///         and left unreachable at the plugin end. Completing an axis #581
    ///         and #584 already sanctioned is not opening a new one; the
    ///         alternative was deleting shipped work to satisfy a guard, which
    ///         would have made this PR a refactor wearing a guard's clothes.
    ///     </para>
    ///     <para>
    ///         A pair REMOVED from this list is an axis deliberately closed, and
    ///         must be removed in the same commit that closes it:
    ///         <c>EveryDeclaredAxis_IsStillReal</c> fails the moment a listed
    ///         half stops existing, so an entry cannot outlive the thing it
    ///         excuses.
    ///     </para>
    /// </remarks>
    internal static readonly FrozenSet<SealedAxis> SealedAxes =
        new[]
        {
            new SealedAxis("IToolPlugin", "RegisterTool"),
            new SealedAxis("IProviderPlugin", "RegisterProvider"),
            new SealedAxis("IAgentPlugin", "RegisterAgent"),
            new SealedAxis("ITuiPlugin", "RegisterTuiPlugin"),
            new SealedAxis("ITuiPanelPlugin", "RegisterPanelProvider"),
            new SealedAxis("ISessionStorePlugin", "RegisterSessionStore"),
            new SealedAxis("ITuiBackendPlugin", "RegisterTuiBackend"),
        }.ToFrozenSet();

    /// <summary>
    ///     Markers that do NOT derive from <see cref="IPlugin" /> and so cannot
    ///     be found by the type test. There is exactly one, and it is a
    ///     documented layering asymmetry rather than an oversight:
    ///     <c>ITuiPlugin</c> lives in Harbor.Terminal.Abstractions, which must
    ///     not be referenced by Harbor.Abstractions, so it cannot extend
    ///     IPlugin without inverting the layer order.
    /// </summary>
    /// <remarks>
    ///     A new axis that does not derive from IPlugin has to be added HERE.
    ///     That is the point: it becomes a single, reviewable, deliberate edit
    ///     rather than an interface that appears in a file nobody diffs against
    ///     the freeze.
    /// </remarks>
    internal static readonly FrozenSet<string> StandaloneMarkers =
        new[] { "ITuiPlugin" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly Lazy<ExtensionAxisScan> Report = new(
        () => ExtensionAxisProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>The frozen door set, as declared.</summary>
    private static string DeclaredDoors =>
        string.Join(" | ", SealedAxes.Select(a => a.Door).OrderBy(d => d, StringComparer.Ordinal));

    /// <summary>The frozen marker set, as declared.</summary>
    private static string DeclaredMarkers =>
        string.Join(" | ", SealedAxes.Select(a => a.Marker).OrderBy(m => m, StringComparer.Ordinal));

    /// <summary>The load host's door set, as found.</summary>
    private static string ActualDoors =>
        string.Join(" | ", ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost)));

    /// <summary>The marker set, as found — reflection plus the declared standalone ones.</summary>
    private static string ActualMarkers
    {
        get
        {
            var found = new SortedSet<string>(Report.Value.Markers, StringComparer.Ordinal);
            found.UnionWith(StandaloneMarkers);
            return string.Join(" | ", found);
        }
    }

    /// <summary>
    ///     The load host's door set is still exactly the frozen one. A new
    ///     <c>Register*</c> on <see cref="IPluginLoadHost" /> IS a registration
    ///     point — the sink through which a plugin contributes a new kind of
    ///     Harbor thing — and adding one is the cheapest, quietest way to open a
    ///     new axis. Read by reflection, so it is caught whether or not anything
    ///     dispatches it.
    /// </summary>
    [Test]
    public async Task SealedAxis_DoorsAreStillTheFrozenSet()
    {
        await Assert.That(ActualDoors).IsEqualTo(DeclaredDoors)
            .Because(
                "#555 froze the axis of extension. A door on IPluginLoadHost is that freeze's "
                + "cheapest violation and used to cost a single line with nothing to stop it. If a new "
                + "door is genuinely wanted, that is a decision ABOUT the freeze, not an accident — "
                + "raise it against #555 and add the axis to SealedAxes in the same commit. Found: "
                + (ActualDoors.Length == 0 ? "(none — reflection found no doors at all)" : ActualDoors));
    }

    /// <summary>
    ///     The declared marker set is still exactly the frozen one. A new public
    ///     <see cref="IPlugin" />-deriving interface is a new axis on the
    ///     plugin-author side, and it is the half of the pair invisible from the
    ///     load host.
    /// </summary>
    [Test]
    public async Task SealedAxis_MarkersAreStillTheFrozenSet()
    {
        await Assert.That(ActualMarkers).IsEqualTo(DeclaredMarkers)
            .Because(
                "a public IPlugin-deriving interface is what a plugin author writes "
                + "`class MyPlugin : …` against, so declaring one IS opening an axis, and it is the "
                + "half that the load host cannot show. The base IPlugin is excluded — it is the root "
                + "every axis extends, not an axis. A new marker needs a decision against #555 and an "
                + "entry in SealedAxes in the same commit. Found: "
                + (ActualMarkers.Length == 0 ? "(none — reflection found no markers at all)" : ActualMarkers));
    }

    /// <summary>
    ///     Every axis in the frozen set is WHOLE: its marker is dispatched, and
    ///     its door is invoked on a receiver.
    /// </summary>
    /// <remarks>
    ///     This is the test that found #620's two half-open axes — a
    ///     <c>RegisterSessionStore</c> and a <c>RegisterTuiBackend</c> on the
    ///     public interface, with the registries, the composition plumbing, the
    ///     module wiring and the XML documentation all built around them, and no
    ///     way for a plugin to reach them, because nothing calls them and a
    ///     plugin is never handed the host.
    /// </remarks>
    [Test]
    public async Task EverySealedAxis_IsDispatchedAndOpened()
    {
        var halfOpen = SealedAxes
            .Where(a =>
                !Report.Value.DispatchedMarkers.Contains(a.Marker, StringComparer.Ordinal)
                || !Report.Value.OpenedDoors.Contains(a.Door, StringComparer.Ordinal))
            .OrderBy(a => a.Door, StringComparer.Ordinal)
            .Select(a => $"{a.Marker}→{a.Door}"
                         + (!Report.Value.DispatchedMarkers.Contains(a.Marker, StringComparer.Ordinal)
                             ? " (marker never dispatched)" : string.Empty)
                         + (!Report.Value.OpenedDoors.Contains(a.Door, StringComparer.Ordinal)
                             ? " (door never invoked)" : string.Empty))
            .ToList();

        await Assert.That(halfOpen).IsEmpty()
            .Because(
                "an axis is a marker AND a door, and both halves have to be connected. A door with no "
                + "marker is a capability IPluginLoadHost advertises that no plugin can ever reach: the "
                + "interface promises it in full XML documentation while the only dispatch in the tree "
                + "never calls it, and a plugin is never handed the host (PluginContext carries Services, "
                + "Configuration, LoggerFactory, EventBus and two paths — not IPluginLoadHost), so it is "
                + "unreachable rather than merely unused. #581 and #584 built the registries, the "
                + "composition plumbing and the module wiring for both and stopped one line short. Either "
                + "finish the axis (add the marker and the dispatch branch) or close it (delete the door "
                + "and its plumbing) — but do not leave a sealed interface making a promise the product "
                + "does not keep. Half-open: " + (halfOpen.Count == 0 ? "(none)" : string.Join(", ", halfOpen)));
    }

    /// <summary>
    ///     Every axis listed in <see cref="SealedAxes" /> is still real. A list
    ///     entry that outlives the thing it names becomes a blanket permission:
    ///     the next person opens an eighth axis and points at this list, which
    ///     now appears to bless it. The entry goes in the same commit that
    ///     removes the axis.
    /// </summary>
    [Test]
    public async Task EveryDeclaredAxis_IsStillReal()
    {
        string[] doors = [.. ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost))];
        var markers = new SortedSet<string>(Report.Value.Markers, StringComparer.Ordinal);
        markers.UnionWith(StandaloneMarkers);

        string[] missing =
        [
            .. SealedAxes
                .Where(a =>
                    !doors.Contains(a.Door, StringComparer.Ordinal)
                    || !markers.Contains(a.Marker, StringComparer.Ordinal))
                .OrderBy(a => a.Door, StringComparer.Ordinal)
                .Select(a => $"{a.Marker}→{a.Door}")
        ];

        await Assert.That(string.Join(" | ", missing)).IsEmpty()
            .Because(
                "SealedAxes is the frozen set, so every entry must still correspond to an axis that "
                + "exists. If a door or a marker listed here is gone, the axis was closed: delete the pair "
                + "in the same commit. A stale entry is how a freeze quietly becomes a suggestion. "
                + "Missing: " + (missing.Length == 0 ? "(none)" : string.Join(", ", missing)));
    }

    /// <summary>
    ///     The scan and the reflection sweep really ran. Without this, a wrong
    ///     perimeter or a broken matcher reports zero axes and every rule above
    ///     passes for the wrong reason — which is the state #620 found the freeze
    ///     in.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the source scan needs a repository checkout; without one it reports zero axes and "
                   + "every rule here is satisfied by having nothing to look at");

        ExtensionAxisScan report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter are "
                   + "wrong and every rule here is vacuously green");

        await Assert.That(report.AssembliesSwept).IsGreaterThan(0)
            .Because("the reflection sweep covered no product assembly, so no marker could have been "
                   + "found and the marker rules are vacuously green");

        foreach (string marker in StandaloneMarkers)
        {
            await Assert.That(ActualMarkers.Contains(marker, StringComparison.Ordinal)).IsTrue()
                .Because($"{marker} is a declared standalone marker (it cannot derive from IPlugin "
                       + "without inverting the layer order) but the sweep did not find the type at "
                       + "all. If it was renamed or moved, fix StandaloneMarkers in the same commit.");
        }
    }

    /// <summary>
    ///     THE PROOF OF DISCRIMINATION. The probe is handed a real NEW AXIS and
    ///     must flag both of its halves; then it is handed five real NON-AXES —
    ///     the changes an honest contributor makes every week — and must flag
    ///     none. A guard that cannot tell those apart is not a guard, it is a
    ///     tripwire, and it gets switched off by the first person it
    ///     inconveniences.
    /// </summary>
    [Test]
    public async Task PositiveControl_DetectsARealNewAxis_AndIgnoresARealNonAxis()
    {
        // ---- A REAL NEW AXIS: the #555 violation this rule exists to catch. ----
        // The plugin-author half, built as a real type so it drives the REAL
        // reflection filter rather than a copy of it. It lives in this test
        // assembly, which the reflection sweep skips by name, so the control
        // cannot perturb the inventory the marker rules grade.
        string[] newAxisMarkers = [.. ExtensionAxisProbe.ReadMarkers([typeof(SyntheticAxisFixture.ITelemetryPlugin)])];

        await Assert.That(string.Join(" | ", newAxisMarkers)).IsEqualTo("ITelemetryPlugin")
            .Because("a new public IPlugin-deriving interface is the plugin-author half of a #555 "
                   + "violation, and it is the half the load host cannot show. If the type filter stops "
                   + "recognising one, this rule goes green the moment somebody opens an eighth kind of "
                   + "extension. Read: " + (newAxisMarkers.Length == 0 ? "(none)" : string.Join(" | ", newAxisMarkers)));

        // ...and the host half, graded by the same reflection filter, over a
        // stand-in load host with a known door set.
        string fakeHostDoors = string.Join(" | ", ExtensionAxisProbe.ReadDoors(typeof(SyntheticAxisFixture.IFakeLoadHost)));

        await Assert.That(fakeHostDoors).IsEqualTo("RegisterTelemetry | RegisterTool")
            .Because("the door half of the same violation, read by REFLECTION off the host interface: a "
                   + "new Register* member is the cheapest possible axis opening and the one #555 most "
                   + "needs a gate on. A miss means ReadDoors filters the new door out and "
                   + "SealedAxis_DoorsAreStillTheFrozenSet enforces nothing. Read: " + fakeHostDoors);

        // A real new axis is also WIRED — the two halves connected, which is the
        // state #620 found two shipped axes missing.
        var wiredDispatched = new SortedSet<string>(StringComparer.Ordinal);
        var wiredOpened = new SortedSet<string>(StringComparer.Ordinal);
        ExtensionAxisProbe.ScanSource(
            """
                if (plugin.Instance is ITelemetryPlugin telemetryPlugin)
                {
                    host.RegisterTelemetry("otlp", () => new OtlpSink());
                }
                """.Split('\n'),
            wiredDispatched,
            wiredOpened);

        await Assert.That(string.Join(" | ", wiredDispatched)).IsEqualTo("ITelemetryPlugin")
            .Because("a wired new axis names its marker in a dispatch branch — the shape "
                   + "EverySealedAxis_IsDispatchedAndOpened grades. Read: "
                   + (wiredDispatched.Count == 0 ? "(none)" : string.Join(" | ", wiredDispatched)));

        await Assert.That(string.Join(" | ", wiredOpened)).IsEqualTo("RegisterTelemetry")
            .Because("a wired new axis invokes its door ON A RECEIVER, which is what makes the axis "
                   + "reachable. The matcher requires a leading dot, so a bare declaration cannot "
                   + "satisfy it and a half-open axis cannot be dressed up as wired. Read: "
                   + (wiredOpened.Count == 0 ? "(none)" : string.Join(" | ", wiredOpened)));

        // ---- FIVE REAL NON-AXES: the changes that must stay legal. ----
        // Each is the honest change a contributor makes often, and none of them
        // may be reported by ANY half of the detector.
        var dispatched = new SortedSet<string>(StringComparer.Ordinal);
        var opened = new SortedSet<string>(StringComparer.Ordinal);

        // 1. A new TOOL — a whole new class, inside an existing axis. By a wide
        //    margin the most common change in this repo. It declares a method
        //    literally named RegisterTool, which must NOT read as opening a
        //    door: a declaration has no receiver.
        ExtensionAxisProbe.ScanSource(
            """
                public sealed class NotebookTool : ITool
                {
                    public ToolName Name => ToolName.Create("notebook");
                    public Result RegisterTool() => Result.Success();
                }
                """.Split('\n'),
            dispatched, opened);

        // 2. A new IMPLEMENTATION of an existing interface — a fourth renderer
        //    factory. Adding implementations is how a frozen axis is meant to be
        //    used, and its Register* method is a declaration too.
        ExtensionAxisProbe.ScanSource(
            """
                internal sealed class DesktopNotificationFactory : ITuiRendererFactory
                {
                    public Result RegisterNotifications(string id) => Result.Success();
                }
                """.Split('\n'),
            dispatched, opened);

        // 3. An `internal` plugin-shaped interface. Not plugin-implementable, so
        //    not an axis; grading it would forbid a private helper.
        ExtensionAxisProbe.ScanSource(
            """
                internal interface IPluginDispatchScratch
                {
                    void Dispatch();
                }
                """.Split('\n'),
            dispatched, opened);

        // 4. A CALL SITE of an existing door. This is what wiring a frozen axis
        //    looks like, and it must not read as declaring a new one.
        ExtensionAxisProbe.ScanSource(
            """
                internal sealed class PanelRegistryPluginAdapter
                {
                    public Result Register(IPanelProvider panel) => _host.RegisterPanelProvider(panel);
                }
                """.Split('\n'),
            dispatched, opened);

        // 5. Prose that NAMES a hypothetical new axis. Comment stripping has to
        //    hold, or this rule would forbid documenting the freeze itself.
        ExtensionAxisProbe.ScanSource(
            """
                /// <remarks>
                ///     We deliberately do not add a RegisterTelemetry door: a new axis needs
                ///     a decision against #555 first. See ITelemetryPlugin.
                /// </remarks>
                public interface IPluginDocsAnchor { }
                """.Split('\n'),
            dispatched, opened);

        await Assert.That(string.Join(" | ", dispatched)).IsEmpty()
            .Because("the five non-axis snippets add a tool class, an extra factory implementation, an "
                   + "INTERNAL plugin-shaped interface, a door call site and prose. None of them adds a "
                   + "dispatch branch, and a rule that reported one would be blocked by the first honest "
                   + "change and switched off — which is how the freeze went unchecked in the first "
                   + "place. Read: " + (dispatched.Count == 0 ? "(none)" : string.Join(" | ", dispatched)));

        await Assert.That(string.Join(" | ", opened)).IsEqualTo("RegisterPanelProvider")
            .Because("exactly ONE of the five snippets calls a door on a receiver; the other four only "
                   + "DECLARE methods named RegisterSomething. The leading-dot rule is what separates "
                   + "them, and it is load-bearing: if declarations leaked in, a dead door would look "
                   + "wired and EverySealedAxis_IsDispatchedAndOpened would be satisfiable without a "
                   + "single call anywhere. Read: " + (opened.Count == 0 ? "(none)" : string.Join(" | ", opened)));

        // The marker half must equally ignore the non-axes, INCLUDING the
        // plugin-shaped `internal` interface — and must equally ignore
        // IAppReducerPlugin, which is the real near-miss this rule was written
        // to avoid flagging.
        string[] nonAxisMarkers =
        [
            .. ExtensionAxisProbe.ReadMarkers(
            [
                typeof(SyntheticAxisFixture.IPluginDispatchScratch),
                typeof(SyntheticAxisFixture.IAppReducerShaped),
                typeof(IPlugin),
            ])
        ];

        await Assert.That(string.Join(" | ", nonAxisMarkers)).IsEmpty()
            .Because("none of these is an axis. `IPluginDispatchScratch` is internal, so no plugin can "
                   + "implement it. `IAppReducerShaped` stands in for the real "
                   + "Harbor.Ui.Framework.State.IAppReducerPlugin, which ends in \"Plugin\" but is a "
                   + "UI-domain reducer hook that never reaches a load host — a name-based matcher would "
                   + "flag it and be wrong about code unrelated to the freeze. `IPlugin` is the base "
                   + "every axis extends, not an axis itself. Read: "
                   + (nonAxisMarkers.Length == 0 ? "(none)" : string.Join(" | ", nonAxisMarkers)));
    }
}

/// <summary>
///     Types used only by the positive control. They live in THIS assembly,
///     which <see cref="ExtensionAxisProbe" />'s reflection sweep skips by name
///     (it covers product assemblies only) — so the control can exercise the real
///     marker filter over real types without perturbing the very inventory the
///     marker rules grade.
/// </summary>
internal sealed class SyntheticAxisFixture
{
    /// <summary>A REAL new axis: a public, IPlugin-deriving marker. Must be flagged.</summary>
    public interface ITelemetryPlugin : IPlugin
    {
        public void RegisterTelemetry(object sink);
    }

    /// <summary>
    ///     A stand-in load host whose door set is known exactly, so the positive
    ///     control can assert against the real <c>ReadDoors</c> filter rather
    ///     than a hand-written copy of it.
    /// </summary>
    public interface IFakeLoadHost
    {
        public Result RegisterTool(object tool);

        public Result RegisterTelemetry(string sinkId, Func<object> factory);
    }

    /// <summary>Not plugin-implementable, so not an axis. Must be ignored.</summary>
    internal interface IPluginDispatchScratch
    {
        void Dispatch();
    }

    /// <summary>
    ///     Stands in for <c>Harbor.Ui.Framework.State.IAppReducerPlugin</c>: the
    ///     name ends in "Plugin", it is not IPlugin-deriving, and it never
    ///     reaches a load host. Must be ignored.
    /// </summary>
    public interface IAppReducerShaped
    {
        object? Reduce(object state, object message);
    }
}
