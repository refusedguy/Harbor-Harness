// ExtensionAxisRegistrationRule.cs — the successor to #620's ExtensionAxisFreezeRule.
//
// WHY THIS FILE REPLACED THAT ONE, AND WHY IT DID NOT DELETE IT
// -------------------------------------------------------------
// #555 froze the axis of extension: a plugin could add a tool, a provider, an
// agent, a TUI plugin, a panel, a session store and a TUI backend, and it may
// not add an EIGHTH kind. The freeze was LIFTED by owner decision on
// 2026-10-20 — its exit condition was met (the audit issues it named are
// closed: #557, #560, #564, #568, #580-#584) and the seams they fenced became
// additive. So the prohibition this file used to enforce is gone, and the
// closed set it used to compare against is gone with it.
//
// Deleting the file would have thrown away the one thing it got right. Its
// central idea was NOT "seven axes, no more" — it was that an axis has TWO
// HALVES and both have to be connected:
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
// #620 found two such lies: `RegisterSessionStore` and `RegisterTuiBackend`
// were declared on the public interface, with the registries, the composition
// plumbing and the module wiring all built around them, and no way for a
// plugin to reach them, because nothing dispatched them and a plugin is never
// handed the host. That class of defect is worth keeping whatever the freeze
// does, and it is what this file grades now.
//
// WHAT THE RULE GRADES, INSTEAD OF "HOW MANY AXES"
// --------------------------------------------------
// Three things, and none of them is a count:
//
//   1. REGISTERED. Every axis is visible in ONE list: `PluginAxisRegistry`
//      (src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs), the product's
//      single registry of axes, which this file READS and does not copy. A
//      door on IPluginLoadHost with no registry entry is an axis that was
//      opened and never declared; a public IPlugin-deriving interface with no
//      registry entry is an axis a plugin author can implement and never
//      reach; a registry entry naming a door or a marker the product no
//      longer has is a stale row. All three fail.
//
//   2. WHOLE. Every registered axis has both halves CONNECTED: its marker is
//      dispatched, and its door is invoked on a receiver. This is #620's
//      half-open detection, and it is now the load-bearing half of the rule.
//
//   3. HONEST. A door that answers `Result.Success()` has to have REGISTERED
//      something — "accepted" and "dropped" must not be the same answer, or the
//      axis set is a lie in a second way (#1758). That half lives in its own
//      file, `tests/Harbor.Architecture.Tests/ExtensionAxisDoorHonestyRule.cs`,
//      because it grades the IMPLEMENTATION of a door rather than the axis set,
//      and the two have different perimeters: this file grades the declaration
//      and the wiring, that one grades what the door answers.
//
// The first two are separate on purpose. "Registered" is the declaration and
// "whole" is the wiring, and a registry that listed every axis the product
// has — including one nobody calls — would satisfy the first and fail the
// second. A rule that graded only the first would be satisfied by a table.
//
// A NEW AXIS IS ADDITIVE, NOT FORBIDDEN
// -------------------------------------
// Opening an eighth kind is allowed. What it is not allowed to be is
// UNDECLARED and UNWIRED: add the marker, add the door, dispatch the marker,
// invoke the door, declare the pair in `PluginAxisRegistry` — and the axis
// exists, and this rule is green. Skip the last two and it is red, which is
// the point. The freeze's price was that opening an axis needed a decision
// against a table; the successor's price is that it needs a line in the
// registry and working wiring, and that is a much smaller price for a much
// smaller mistake.
//
// HOW EACH HALF IS DETECTED, AND WHY
// ----------------------------------
//   * Doors are read by REFLECTION off IPluginLoadHost. Exact, and it catches
//     a new door whether or not anything dispatches it — a door nobody calls
//     is precisely the shape #620 found.
//   * Markers are read by REFLECTION over every product assembly, keeping
//     public interfaces assignable to IPlugin.
//
// The marker half is deliberately NOT a name scan. A name scan for
// `I…Plugin` also matches `IAppReducerPlugin`
// (src/Harbor.Ui.Framework.State/State/AppReducer.cs:48) — a UI-domain
// reducer hook where "plugin" means "reducer plugin", not "Harbor plugin". It
// is not an extension axis, it is not IPlugin-deriving, and a rule that
// flagged it would be wrong about code that has nothing to do with extension.
// Keying on the interface's TYPE rather than its spelling is what keeps this
// rule from blocking an honest change, which is the whole reason it has to be
// narrow.
//
// The registry's marker names are the one place a NAME is load-bearing, and
// the reason is a layer decision rather than a preference: `ITuiPlugin` lives
// in Harbor.Terminal.Abstractions, which Harbor.Plugins.Abstractions may
// reach only from the sites FullLayerMatrixTests documents, so the registry
// spells that marker as a name and this rule resolves it against the
// product's public interfaces. A name that resolves to zero types — or to
// two — fails the gate, so the name cannot drift silently.
//
// BOUNDARY OF THE "NOTHING CALLS IT" CLAIM
// -----------------------------------------
// InMemoryEventBus's ring (src/Harbor.Registries/Events/InMemoryEventBus.cs:48)
// records the opposite lesson: "'no production callers' is not a provable claim
// here", because a live bus is handed to out-of-tree plugin code through
// `IPluginLoadHost.EventBus`. That caution does not transfer here, and the
// difference is why this rule is allowed to make the claim: EventBus is a
// PROPERTY plugins are given, while the doors are METHODS on a host object no
// plugin is ever given. The test below is therefore "is this method INVOKED
// ON A RECEIVER somewhere under src/", which a property hand-off cannot fake
// and a bare declaration cannot satisfy. A door reached reflectively or
// through a delegate would be missed; that boundary is stated here rather
// than hidden, and it is the one shape this rule does not cover.
//
// PERIMETER
// ---------
// src + apps, mirroring SessionStatusTableRule. `contrib/` is outside CI and
// unsupported by owner decision. `tests/` and `samples/` CONSUME the axes
// rather than declare them — a sample plugin is the reason the axis exists,
// and grading consumers would forbid it. The reflection sweep applies the same
// exclusion to test and benchmark assemblies by name.
//
// KNOWN ASYMMETRY, RECORDED NOT ENFORCED
// -------------------------------------
// `ITuiPlugin` does not derive from `IPlugin`; the other six markers do. That
// is a real inconsistency, but unifying it is a layering change
// (Harbor.Terminal.Abstractions must not be pulled into Harbor.Abstractions)
// and not this issue's business. It has one consequence for this rule: a
// marker that does not derive from IPlugin is invisible to the type sweep, so
// an UNDECLARED one cannot be seen here. What can be seen is an undeclared
// one that is DISPATCHED — the dispatch scan finds it by shape, not by type —
// and the registry is where a marker of that kind is declared in the first
// place.
//
// NON-VACUITY
// -----------
//   1. TheScanAndTheRegistryAreLive — the scan really walked a checkout, the
//      reflection sweep really covered the product assemblies, the registry is
//      not empty, and every marker it names resolves to EXACTLY ONE public
//      product interface. Without this, a broken path reports zero axes and
//      every rule below is satisfied by having nothing to look at.
//   2. NegativeControl_TheRuleCatchesAnInjectedViolation — THE PROOF THAT THE
//      RULE CAN FAIL. A synthetic eighth axis (a real public IPlugin-deriving
//      marker plus a real door on a stand-in host) is injected into the real
//      inventory WITHOUT being registered, and the rule must report both
//      halves; then a registered axis has its door removed from the invoked
//      set, and the rule must report the half-open axis. A grader that cannot
//      say no is a grader that enforces nothing — which is the state #620
//      found the freeze in.
//
// The third half (HONEST) has its own pair of controls in
// `ExtensionAxisDoorHonestyRule.cs` — `TheDoorScanIsLive` and
// `NegativeControl_TheDoorGraderCatchesAnInjectedViolation` — because it
// grades a different inventory (the implementations of the load host) with a
// different perimeter.

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One axis as the product registry declares it: the marker a plugin
///     implements and the door that receives it.
/// </summary>
/// <param name="Marker">Simple name of the plugin-implementable interface.</param>
/// <param name="Door">Name of the <c>Register*</c> member on the load host.</param>
internal readonly record struct RegisteredAxis(string Marker, string Door);

/// <summary>What kind of defect the grader found.</summary>
internal enum AxisDefectKind
{
    /// <summary>An axis the product has, which the registry does not declare.</summary>
    Unregistered,

    /// <summary>A registry row naming something the product no longer has.</summary>
    Stale,

    /// <summary>A declared axis whose two halves are not connected.</summary>
    HalfOpen,

    /// <summary>Two registry rows claiming the same door or the same marker.</summary>
    Duplicate,

    /// <summary>
    ///     A door that answers <c>Result.Success()</c> while registering nothing —
    ///     the "accepted" that changed no state (#1758).
    /// </summary>
    SilentSuccess,
}

/// <summary>One defect, with the kind that decides which rule reports it.</summary>
/// <param name="Kind">Which contract the defect breaks.</param>
/// <param name="Detail">The sentence the failure message shows.</param>
internal sealed record AxisDefect(AxisDefectKind Kind, string Detail);

/// <summary>
///     The grading function. Pure, over names, so the negative control can
///     hand it an injected violation and watch it fail.
/// </summary>
/// <remarks>
///     Every input is a LIST, and none of them is this rule's own opinion
///     about what an axis is: the registry comes from the product, the doors
///     and markers come from reflection, the dispatch and the invocations come
///     from the source scan. The rule file therefore holds no second copy of
///     the axis list — which was the whole price of the freeze.
/// </remarks>
internal static class ExtensionAxisGrader
{
    /// <summary>
    ///     Grades the extension surface. Returns every defect it finds; an
    ///     empty list is the healthy state.
    /// </summary>
    /// <param name="registry">The axes the product registry declares.</param>
    /// <param name="hostDoors">Doors declared on the load host, by reflection.</param>
    /// <param name="markerResolution">
    ///     How many public product interfaces each registry marker name
    ///     resolves to. Zero means the name is stale; more than one means it
    ///     is ambiguous.
    /// </param>
    /// <param name="pluginMarkers">
    ///     Public <c>IPlugin</c>-deriving interfaces found in the product.
    /// </param>
    /// <param name="dispatchedMarkers">
    ///     Markers named by an <c>is I…Plugin</c> dispatch branch.
    /// </param>
    /// <param name="openedDoors">Doors found INVOKED on a receiver.</param>
    internal static IReadOnlyList<AxisDefect> Grade(
        IReadOnlyList<RegisteredAxis> registry,
        IReadOnlyList<string> hostDoors,
        IReadOnlyDictionary<string, int> markerResolution,
        IReadOnlyList<string> pluginMarkers,
        IReadOnlyList<string> dispatchedMarkers,
        IReadOnlyList<string> openedDoors)
    {
        var defects = new List<AxisDefect>();

        List<string> registryDoors = [.. registry.Select(a => a.Door)];
        List<string> registryMarkers = [.. registry.Select(a => a.Marker)];

        // (1) A door on the host that no registered axis claims. The axis was
        //     opened and never declared: a plugin cannot reach it, and nobody
        //     reading the registry can see that it exists.
        foreach (string door in hostDoors
                     .Where(d => !registryDoors.Contains(d, StringComparer.Ordinal))
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Unregistered,
                $"{door}: IPluginLoadHost declares this door, and PluginAxisRegistry has no axis for it"));
        }

        // (2) A registry row whose door the host no longer declares. The axis
        //     was closed and the row outlived it — a row that survives its
        //     subject is a blanket permission for the next person.
        foreach (RegisteredAxis axis in registry
                     .Where(a => !hostDoors.Contains(a.Door, StringComparer.Ordinal))
                     .OrderBy(a => a.Door, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Stale,
                $"{axis.Marker}→{axis.Door}: the registry names a door IPluginLoadHost does not declare"));
        }

        // (3) A registry row whose marker is not a public product interface —
        //     renamed, moved, or never existed. A marker name that resolves to
        //     TWO interfaces is just as broken: the row no longer names one
        //     thing.
        foreach (RegisteredAxis axis in registry
                     .Where(a => MatchCount(markerResolution, a.Marker) != 1)
                     .OrderBy(a => a.Door, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Stale,
                $"{axis.Marker}→{axis.Door}: the registry names a marker that resolves to "
                + $"{MatchCount(markerResolution, axis.Marker)} public product interface(s) — it must resolve to exactly one"));
        }

        // (4) A plugin-implementable marker that no registered axis claims.
        //     This is the "marker without a door" half of #620's finding: an
        //     interface a plugin author can implement and never reach.
        foreach (string marker in pluginMarkers
                     .Where(m => !registryMarkers.Contains(m, StringComparer.Ordinal))
                     .OrderBy(m => m, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Unregistered,
                $"{marker}: a public IPlugin-deriving interface, and PluginAxisRegistry has no axis for it"));
        }

        // (5) A marker the product DISPATCHES that no registered axis claims.
        //     This is the only way to see an undeclared marker that does not
        //     derive from IPlugin: the dispatch scan finds it by shape.
        foreach (string marker in dispatchedMarkers
                     .Where(m => !registryMarkers.Contains(m, StringComparer.Ordinal))
                     .OrderBy(m => m, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Unregistered,
                $"{marker}: dispatched by the product, and PluginAxisRegistry has no axis for it"));
        }

        // (6) A registered axis whose halves are not connected. This is
        //     #620's half-open detection, unchanged in what it catches and
        //     now applied to EVERY registered axis rather than to a frozen set.
        foreach (RegisteredAxis axis in registry.OrderBy(a => a.Door, StringComparer.Ordinal))
        {
            if (!dispatchedMarkers.Contains(axis.Marker, StringComparer.Ordinal))
            {
                defects.Add(new AxisDefect(
                    AxisDefectKind.HalfOpen,
                    $"{axis.Marker}→{axis.Door}: the marker is never dispatched"));
            }

            if (!openedDoors.Contains(axis.Door, StringComparer.Ordinal))
            {
                defects.Add(new AxisDefect(
                    AxisDefectKind.HalfOpen,
                    $"{axis.Marker}→{axis.Door}: the door is never invoked on a receiver"));
            }
        }

        // (7) Two axes claiming one door or one marker. A registry where the
        //     same door is listed twice is a list that cannot be read: which
        //     axis a reader believes it names depends on which row they read.
        foreach (IGrouping<string, RegisteredAxis> group in registry
                     .GroupBy(a => a.Door, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Duplicate,
                $"two axes claim the door {group.Key}"));
        }

        foreach (IGrouping<string, RegisteredAxis> group in registry
                     .GroupBy(a => a.Marker, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            defects.Add(new AxisDefect(
                AxisDefectKind.Duplicate,
                $"two axes claim the marker {group.Key}"));
        }

        return defects;
    }

    /// <summary>How many public product interfaces a marker name resolves to.</summary>
    internal static int MatchCount(IReadOnlyDictionary<string, int> resolution, string marker)
        => resolution.TryGetValue(marker, out int matches) ? matches : 0;
}

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
    ///         wrong about code that has nothing to do with extension. Keying on
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

    /// <summary>
    ///     How many public product interfaces each marker NAME resolves to.
    /// </summary>
    /// <remarks>
    ///     The registry names its markers (see
    ///     <c>src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs</c>), and a
    ///     name that resolves to zero interfaces is a stale row while a name
    ///     that resolves to two is a row that no longer names one thing. Both
    ///     are reported, and both are the reason this rule can grade a
    ///     NAME-keyed registry without a name-keyed false positive.
    /// </remarks>
    internal static IReadOnlyDictionary<string, int> ResolveMarkerNames(
        IEnumerable<string> names,
        IReadOnlyList<Type> productTypes)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(productTypes);

        var resolution = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string name in names)
        {
            resolution[name] = productTypes.Count(t => t.IsInterface
                                                    && (t.IsPublic || t.IsNestedPublic)
                                                    && string.Equals(t.Name, name, StringComparison.Ordinal));
        }

        return resolution;
    }

    /// <summary>Every product-assembly type the reflection sweep should consider, and how many assemblies it covered.</summary>
    internal static (IReadOnlyList<Type> Types, int Assemblies) ProductTypes() => ProductTypesCache.Value;

    /// <summary>
    ///     The reflection sweep is expensive and its answer cannot change while
    ///     the gate runs, so it is read once and shared by the scan and by the
    ///     marker resolution.
    /// </summary>
    private static readonly Lazy<(IReadOnlyList<Type> Types, int Assemblies)> ProductTypesCache =
        new(static () =>
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

            return ((IReadOnlyList<Type>)types, assemblies);
        });

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

        (IReadOnlyList<Type> types, int assemblies) = ProductTypes();

        return new ExtensionAxisScan(
            ReadMarkers(types),
            [.. dispatched],
            [.. opened],
            scanned,
            assemblies);
    }

    /// <summary>
    ///     Scans already-read lines. Exposed so the negative control drives the
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

    /// <summary>
    ///     Every product <c>.cs</c> file the source scan reads. Internal so the
    ///     door-honesty scan (#1758) walks the SAME perimeter as this one — a
    ///     second file list would be a second answer to "what is product code".
    /// </summary>
    internal static IEnumerable<string> EnumerateSources(string repoRoot)
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
///     Guard for issue #1719: every extension axis is REGISTERED in the
///     product's single registry and WHOLE — its marker dispatched, its door
///     invoked. A closed set is no longer enforced, because the freeze that
///     needed one was lifted on 2026-10-20; what replaced it is a registry
///     plus the wiring check, and neither of those is a count.
/// </summary>
public sealed class ExtensionAxisRegistrationRule
{
    private static readonly Lazy<ExtensionAxisScan> Report = new(
        () => ExtensionAxisProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     The axes the product registry declares. Read from
    ///     <c>PluginAxisRegistry</c> — the ONE list — so this file keeps no
    ///     copy of the axis set that could drift from it.
    /// </summary>
    private static IReadOnlyList<RegisteredAxis> Registry =>
        [.. PluginAxisRegistry.All.Select(a => new RegisteredAxis(a.Marker, a.Door))];

    /// <summary>The doors the load host declares, as found.</summary>
    private static IReadOnlyList<string> HostDoors =>
        ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost));

    /// <summary>
    ///     How many public product interfaces each registry marker name
    ///     resolves to.
    /// </summary>
    private static IReadOnlyDictionary<string, int> MarkerResolution =>
        ExtensionAxisProbe.ResolveMarkerNames(
            Registry.Select(a => a.Marker),
            ExtensionAxisProbe.ProductTypes().Types);

    /// <summary>Every defect the grader finds against the real inventory.</summary>
    private static IReadOnlyList<AxisDefect> Defects =>
        ExtensionAxisGrader.Grade(
            Registry,
            HostDoors,
            MarkerResolution,
            Report.Value.Markers,
            Report.Value.DispatchedMarkers,
            Report.Value.OpenedDoors);

    /// <summary>
    ///     Every axis the product has is DECLARED in the single registry, and
    ///     every registry row still names something real.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the successor to the closed-set check, and it is the
    ///         opposite of it. The closed set asked "is the door set still the
    ///         frozen seven?" and failed a door nobody had asked for. This asks
    ///         "is every door the product has DECLARED, and does every
    ///         declaration still name something real?" — so an eighth axis is
    ///         legal the moment it is registered, and a door that was opened
    ///         and never declared is not.
    ///     </para>
    ///     <para>
    ///         The registry is read from the product
    ///         (<c>src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs</c>),
    ///         so there is exactly one list. The price the freeze used to
    ///         charge — "open an axis, then edit the table in the test file" —
    ///         is now "open an axis, then declare it in the registry", and the
    ///         declaration is what the rule grades, not a permission to ask
    ///         for.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task EveryAxis_IsRegisteredInTheSingleRegistry()
    {
        var defects = Defects
            .Where(d => d.Kind is AxisDefectKind.Unregistered
                or AxisDefectKind.Stale
                or AxisDefectKind.Duplicate)
            .ToList();

        await Assert.That(string.Join(" | ", defects.Select(d => d.Detail)))
            .IsEqualTo(string.Empty)
            .Because(
                "the #555 freeze was lifted on 2026-10-20, so the axis set is no longer closed and a new "
                + "axis is additive rather than forbidden. What replaces the closed set is ONE registry — "
                + "PluginAxisRegistry (src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs) — and every "
                + "axis has to be in it: a door on IPluginLoadHost that no registered axis claims is a "
                + "capability the interface advertises and no plugin can reach, a public IPlugin-deriving "
                + "interface that no registered axis claims is a marker a plugin author can implement and "
                + "never deliver, and a registry row naming a door or a marker the product no longer has is "
                + "a stale permission. Register the axis (one line) or close the half you opened. "
                + "Unregistered/stale/duplicate: "
                + (defects.Count == 0 ? "(none)" : string.Join(" | ", defects.Select(d => d.Detail))));
    }

    /// <summary>
    ///     Every registered axis is WHOLE: its marker is dispatched and its door
    ///     is invoked on a receiver.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is #620's finding, kept. It found two of the seven axes
    ///         half-open — <c>RegisterSessionStore</c> and
    ///         <c>RegisterTuiBackend</c> on the public interface, with the
    ///         registries, the composition plumbing, the module wiring and the
    ///         XML documentation all built around them, and no way for a
    ///         plugin to reach them, because nothing called them and a plugin
    ///         is never handed the host.
    ///     </para>
    ///     <para>
    ///         It is now the load-bearing half of the rule, and it applies to
    ///         EVERY registered axis rather than to a frozen set: a new axis
    ///         that is declared but never dispatched is exactly the defect
    ///         #620 found, and a rule that only graded the declaration would
    ///         have called that axis healthy.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task EveryAxis_IsWhole()
    {
        var defects = Defects
            .Where(d => d.Kind is AxisDefectKind.HalfOpen)
            .ToList();

        await Assert.That(string.Join(" | ", defects.Select(d => d.Detail)))
            .IsEqualTo(string.Empty)
            .Because(
                "an axis is a marker AND a door, and both halves have to be connected. A door with no "
                + "dispatch is a capability IPluginLoadHost advertises that no plugin can ever reach: the "
                + "interface promises it in full XML documentation while the only dispatch in the tree never "
                + "calls it, and a plugin is never handed the host (PluginContext carries Services, "
                + "Configuration, LoggerFactory, EventBus and two paths — not IPluginLoadHost), so it is "
                + "unreachable rather than merely unused. #581 and #584 built the registries, the composition "
                + "plumbing and the module wiring for both and stopped one line short. Either finish the axis "
                + "(dispatch the marker, invoke the door) or close it (delete the door and its plumbing) — "
                + "but do not leave a public interface making a promise the product does not keep. Half-open: "
                + (defects.Count == 0 ? "(none)" : string.Join(" | ", defects.Select(d => d.Detail))));
    }

    /// <summary>
    ///     The scan, the reflection sweep and the registry really ran, and the
    ///     registry's marker names resolve to exactly one public product
    ///     interface each.
    /// </summary>
    /// <remarks>
    ///     Without this, a wrong perimeter or a broken matcher reports zero
    ///     axes and every rule above passes for the wrong reason — which is
    ///     the state #620 found the freeze in. The marker resolution is the
    ///     half the old rule got wrong: it asserted that a declared standalone
    ///     marker was "found", by unioning the declared set into the found set
    ///     and then asking whether the union contains it. That question has
    ///     the answer "yes" in every universe, including the broken one.
    /// </remarks>
    [Test]
    public async Task TheScanAndTheRegistryAreLive()
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

        await Assert.That(Registry.Count).IsGreaterThan(0)
            .Because("PluginAxisRegistry is the single list this rule grades against, and an empty one "
                   + "would make every axis check pass for the wrong reason");

        var unresolved = Registry
            .Where(a => ExtensionAxisGrader.MatchCount(MarkerResolution, a.Marker) != 1)
            .Select(a => $"{a.Marker}→{a.Door} (resolves to "
                         + $"{ExtensionAxisGrader.MatchCount(MarkerResolution, a.Marker)} public product interfaces)")
            .ToList();
        await Assert.That(string.Join(" | ", unresolved)).IsEqualTo(string.Empty)
            .Because(
                "the registry names its markers, and a name that resolves to zero public product interfaces "
                + "is a stale row while a name that resolves to two is a row that no longer names one thing. "
                + "Both are checked here as well as by the grader, because the grader works on the same "
                + "resolution and a broken resolution would make the grader's stale-row check vacuous. "
                + "Unresolved: " + (unresolved.Count == 0 ? "(none)" : string.Join(" | ", unresolved)));
    }

    /// <summary>
    ///     THE PROOF THAT THE RULE CAN FAIL. A synthetic eighth axis — a real
    ///     public IPlugin-deriving marker and a real door on a stand-in load
    ///     host — is injected into the real inventory WITHOUT being registered,
    ///     and the rule must report BOTH halves. Then a registered axis has
    ///     its door taken away, and the rule must report the half-open axis.
    /// </summary>
    /// <remarks>
    ///     A grader that cannot say no is a grader that enforces nothing, and
    ///     a rule that reports nothing while the product is broken is the state
    ///     #620 found the freeze in: a guard that is green because it read
    ///     nothing. The control therefore starts from the REAL inventory, and
    ///     the first assertion is that the real inventory is clean — otherwise
    ///     "the injected violation was reported" would prove nothing about
    ///     which of the two states the report came from.
    /// </remarks>
    [Test]
    public async Task NegativeControl_TheRuleCatchesAnInjectedViolation()
    {
        IReadOnlyList<RegisteredAxis> registry = Registry;
        IReadOnlyList<string> hostDoors = HostDoors;
        IReadOnlyDictionary<string, int> resolution = MarkerResolution;

        // ---- The real inventory is clean. ----
        var clean = ExtensionAxisGrader.Grade(
            registry,
            hostDoors,
            resolution,
            Report.Value.Markers,
            Report.Value.DispatchedMarkers,
            Report.Value.OpenedDoors);

        await Assert.That(string.Join(" | ", clean.Select(d => d.Detail))).IsEqualTo(string.Empty)
            .Because(
                "the negative control below proves the grader reports an INJECTED violation; that is only "
                + "evidence if the real inventory reports nothing, otherwise the report could have come from "
                + "a pre-existing defect. Real defects: "
                + (clean.Count == 0 ? "(none)" : string.Join(" | ", clean.Select(d => d.Detail))));

        // ---- The synthetic axis, built from REAL types so it drives the REAL filters. ----
        // It lives in this test assembly, which the reflection sweep skips by
        // name, so the control cannot perturb the inventory the rules grade.
        string[] syntheticMarkers = [.. ExtensionAxisProbe.ReadMarkers([typeof(SyntheticAxisFixture.ITelemetryPlugin)])];

        await Assert.That(string.Join(" | ", syntheticMarkers)).IsEqualTo("ITelemetryPlugin")
            .Because("a new public IPlugin-deriving interface is the plugin-author half of a new axis, and it "
                   + "is the half the load host cannot show. If the type filter stops recognising one, the "
                   + "unregistered-marker check goes green the moment somebody opens a kind of extension "
                   + "without declaring it. Read: "
                   + (syntheticMarkers.Length == 0 ? "(none)" : string.Join(" | ", syntheticMarkers)));

        string syntheticDoors = string.Join(" | ", ExtensionAxisProbe.ReadDoors(typeof(SyntheticAxisFixture.IFakeLoadHost)));

        await Assert.That(syntheticDoors).IsEqualTo("RegisterTelemetry | RegisterTool")
            .Because("the door half of the same new axis, read by REFLECTION off a stand-in load host: a new "
                   + "Register* member is the cheapest possible axis opening, and the one the unregistered-door "
                   + "check has to catch. A miss means ReadDoors filters the new door out and the check "
                   + "enforces nothing. Read: " + syntheticDoors);

        // ---- INJECTION 1: the axis is opened and never declared. ----
        var injectedDoors = new List<string>(hostDoors) { "RegisterTelemetry" };
        var injectedMarkers = new List<string>(Report.Value.Markers);
        injectedMarkers.AddRange(syntheticMarkers);

        var injectedResolution = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> pair in resolution)
        {
            injectedResolution[pair.Key] = pair.Value;
        }

        injectedResolution["ITelemetryPlugin"] = 1;

        var injected = ExtensionAxisGrader.Grade(
            registry,
            injectedDoors,
            injectedResolution,
            injectedMarkers,
            Report.Value.DispatchedMarkers,
            Report.Value.OpenedDoors);

        await Assert.That(injected.Any(d => d.Kind is AxisDefectKind.Unregistered
                                            && d.Detail.Contains("RegisterTelemetry", StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "a door on IPluginLoadHost with no registered axis is the shape this rule exists to catch: the "
                + "interface advertises a capability that no plugin can reach and that no declaration records. "
                + "The injected door is not reported, so the check cannot fail. Reported: "
                + (injected.Count == 0 ? "(none)" : string.Join(" | ", injected.Select(d => d.Detail))));

        await Assert.That(injected.Any(d => d.Kind is AxisDefectKind.Unregistered
                                            && d.Detail.Contains("ITelemetryPlugin", StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "the other half of the same violation: a public IPlugin-deriving interface with no registered "
                + "axis is a marker a plugin author can implement and never deliver. The injected marker is not "
                + "reported, so the check cannot fail. Reported: "
                + (injected.Count == 0 ? "(none)" : string.Join(" | ", injected.Select(d => d.Detail))));

        // ---- INJECTION 2: a DECLARED axis whose door nobody calls. ----
        // The axis is registered, so the registration checks pass; only the
        // wiring is broken. This is #620's defect exactly, and it is the one
        // a registry-only rule would call healthy.
        const string HalfOpenedDoor = "RegisterSessionStore";

        var halfOpened = ExtensionAxisGrader.Grade(
            registry,
            hostDoors,
            resolution,
            Report.Value.Markers,
            Report.Value.DispatchedMarkers,
            Report.Value.OpenedDoors.Where(d => !string.Equals(d, HalfOpenedDoor, StringComparison.Ordinal)).ToList());

        await Assert.That(halfOpened.Any(d => d.Kind is AxisDefectKind.HalfOpen
                                             && d.Detail.Contains(HalfOpenedDoor, StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "a registered axis whose door nobody invokes is the defect #620 found twice, and a rule that "
                + "graded only the declaration would call it healthy. The half-open axis is not reported, so "
                + "the check cannot fail. Reported: "
                + (halfOpened.Count == 0 ? "(none)" : string.Join(" | ", halfOpened.Select(d => d.Detail))));
    }
}

/// <summary>
///     Types used only by the negative control. They live in THIS assembly,
///     which <see cref="ExtensionAxisProbe" />'s reflection sweep skips by name
///     (it covers product assemblies only) — so the control can exercise the real
///     marker filter over real types without perturbing the very inventory the
///     rules grade.
/// </summary>
internal sealed class SyntheticAxisFixture
{
    /// <summary>A REAL new axis: a public, IPlugin-deriving marker. Must be flagged.</summary>
    public interface ITelemetryPlugin : IPlugin
    {
        public void RegisterTelemetry(object sink);
    }

    /// <summary>
    ///     A stand-in load host whose door set is known exactly, so the negative
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
