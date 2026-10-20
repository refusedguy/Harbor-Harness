// ExtensionAxisRouteCarriageRule.cs — the transport half of the extension-axis
// contract (issue #1723).
//
// WHAT THE OTHER TWO RULES GRADE, AND WHAT IS LEFT
// -------------------------------------------------
// Two rules already grade the axis surface:
//
//   * `ExtensionAxisRegistrationRule.cs` (#1719) grades the DECLARATION and
//     the WIRING: every axis is in `PluginAxisRegistry`, its marker is
//     dispatched, its door is invoked on a receiver.
//   * `ExtensionAxisDoorHonestyRule.cs` (#1758) grades the ANSWER: a door that
//     says `Result.Success()` has to have registered something.
//
// Both look at the axis set. Neither looks at a ROUTE, and the difference is
// the whole of #1723.
//
// THE DEFECT THIS FILE EXISTS FOR
// -------------------------------
// The product has one route for CS-source plugins: the out-of-process MCP
// stdio host (`harbor-plugins-host`, `PluginHostLocator`). That route serves
// TOOLS and refuses the other six axes. The refusals are honest (#1758: a
// `Result.Failure` with a reason, and the plugin's registration fails with
// it) — but until now the STATEMENT of which axes the route carries, and why
// the others do not cross, lived in prose: a README table and a paragraph in
// `docs/PLUGIN_SYSTEM.md`.
//
// Prose is not graded, so it drifts, and it is invisible to the two rules
// above. The consequences were:
//
//   1. A NEW axis could be added to `PluginAxisRegistry` (the set is additive
//      since #1719) and the route's fate for it would be unstated — the exact
//      "half-open axis at the transport level" shape #1723 reports. The
//      compiler forces somebody to write a door implementation; nothing forced
//      them to say whether that axis is carried or refused.
//   2. The verdict per axis existed in one prose copy and in the code, and
//      the two could disagree without either failing a gate.
//
// So the verdict is now a DECLARATION in the product —
// `PluginRouteAxisRegistry` (src/Harbor.Plugins.Hosting/) — and this file
// grades it against the code.
//
// WHAT IT GRADES
// --------------
//   1. STATED. Every registered axis has a row for the out-of-process route:
//      the route says what it does with each axis. An axis with no row is a
//      defect, not a default.
//   2. REASONED. Every row has a reason. "Refused" without a reason is the
//      defect in a different shape: a refusal the plugin author cannot act on.
//   3. HONEST. The route's implementation matches the row: a CARRIED axis
//      really registers something and can answer Success; a REFUSED or
//      CLOSED-SEAM axis really answers `Result.Failure` and never answers
//      Success.
//
// WHY THE REASONS ARE ALSO IN THIS FILE, IN PROSE
// -----------------------------------------------
// The declaration carries one sentence per axis, and this file repeats WHY
// each axis has the verdict it has — the technical reason, not a restatement.
// A rule that says "a refused axis must have a reason" without saying why
// THAT axis is refused teaches nobody anything, and the reader who has to
// decide the next verdict is the reader of the rule. The per-axis verdicts
// and their reasons:
//
//   * IToolPlugin — CARRIED. The tool's name, description and input schema
//     travel on `tools/list` and its result on `tools/call`, so the core can
//     call the tool. The tool is not a first-class entry in the core's tool
//     registry — the core reaches it through the `mcp` bridge tool — and the
//     tool's own safety profile stays with the host that executes it.
//   * IProviderPlugin — REFUSED. An `ILlmClient` is a live streaming client:
//     the wire would have to carry per-turn token deltas and the provider's
//     credentials, and the MCP tool protocol carries neither.
//   * IAgentPlugin — REFUSED. An `AgentDefinition` is data and could be
//     carried, but the core evaluates its `PermissionRuleset` per call and
//     freezes its agent registry at startup, before the lazily spawned plugin
//     host is asked anything. Carrying it needs a startup handshake this
//     route deliberately does not have.
//   * ITuiPlugin — CLOSED SEAM (#564). The plugin's TUI views are collected
//     and never rendered, in-process or out-of-process; this route does not
//     reopen that seam.
//   * ITuiPanelPlugin — REFUSED. A panel is live UI-process state — it builds
//     a view object and handles keys — so it is not a registration that can
//     be serialized across a process boundary. Carrying it needs a panel
//     protocol, and the plugin showcase (#1434) is the right place to design
//     one.
//   * ISessionStorePlugin — REFUSED. The store is resolved in the core at
//     startup through `HARBOR_STORAGE`; a store behind the pipe would put
//     every session read and write on IPC, and the core's store contract has
//     no remote form.
//   * ITuiBackendPlugin — REFUSED. A renderer is constructed in the UI process
//     and paints frames; a backend factory cannot be expressed as an MCP
//     tool, so the renderer axis has no wire form.
//
// WHAT IS NOT GRADED HERE, AND WHY
// --------------------------------
// Whether an axis SHOULD cross a process boundary is a design decision, not a
// property of the code — this rule grades that the decision is STATED and
// HONEST, not that it is the one a reader would make. Changing a verdict is
// a one-line edit in `PluginRouteAxisRegistry` plus the matching door, and
// the gate then says so.
//
// PERIMETER
// ---------
// The declaration is read from the product by reference; the route's
// implementation is read from the SOURCE, because
// `src/Harbor.Plugins.Host/` is an executable (an app/composition root, like
// `apps/*`) that this test project deliberately does not reference. The
// source scan is the same one the door-honesty rule uses
// (`ExtensionAxisDoorProbe`), so the two rules see the same perimeter.
//
// NON-VACUITY
// -----------
//   1. TheScanAndTheDeclarationAreLive — the scan really walked a checkout,
//     really found the route's load host, and really found the doors the
//     interface declares; the declaration is not empty; the axis registry is
//     not empty. Without it, a broken perimeter reports zero rows and every
//     rule below is satisfied by having nothing to look at.
//   2. NegativeControl_TheGraderCatchesAnInjectedViolation — THE PROOF THAT
//     THE GRADER CAN FAIL. It starts from the REAL inventory and asserts it
//     is clean, then injects an axis with no declared fate and a row whose
//     verdict the implementation contradicts, and requires the grader to
//     report both; then it removes the contradiction and requires the grader
//     to stay silent. A grader that reports everything is as useless as one
//     that reports nothing.

using System.Text.RegularExpressions;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Hosting;

namespace Harbor.Architecture.Tests;

/// <summary>What kind of defect the route-carriage grader found.</summary>
internal enum RouteCarriageDefectKind
{
    /// <summary>An axis whose fate on the route is not stated.</summary>
    Undeclared,

    /// <summary>A row naming a door the product no longer has.</summary>
    Stale,

    /// <summary>A row with no reason — a refusal the plugin author cannot act on.</summary>
    MissingReason,

    /// <summary>The route's implementation does not do what the row declares.</summary>
    CarriageMismatch,

    /// <summary>The route's load host was not found in the product.</summary>
    RouteHostMissing,
}

/// <summary>One defect, with the kind that decides which rule reports it.</summary>
/// <param name="Kind">Which contract the defect breaks.</param>
/// <param name="Detail">The sentence the failure message shows.</param>
internal sealed record RouteCarriageDefect(RouteCarriageDefectKind Kind, string Detail);

/// <summary>
///     The grading function for the route's carriage of the axes. Pure, over
///     declarations and door bodies, so the negative control can hand it an
///     injected violation and watch it fail.
/// </summary>
internal static class ExtensionAxisRouteCarriageGrader
{
    /// <summary>
    ///     The door answering <c>Result.Failure</c>. The name is required: a bare
    ///     <c>Failure</c> would also match a local helper and a message.
    /// </summary>
    private static readonly Regex FailurePattern = new(@"Result\s*\.\s*Failure\b", RegexOptions.Compiled);

    /// <summary>
    ///     Grades the route's carriage of the axes.
    /// </summary>
    /// <param name="declared">Every row the product declares, for every route.</param>
    /// <param name="registered">The axes the product registers.</param>
    /// <param name="routeDoors">Every door implementation the product answers.</param>
    /// <param name="routeId">The route being graded.</param>
    /// <param name="hostType">The name of the load host that serves the route.</param>
    internal static IReadOnlyList<RouteCarriageDefect> Grade(
        IReadOnlyList<PluginRouteAxis> declared,
        IReadOnlyList<RegisteredAxis> registered,
        IReadOnlyList<DoorImplementation> routeDoors,
        string routeId,
        string hostType)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(routeDoors);
        ArgumentNullException.ThrowIfNull(routeId);
        ArgumentNullException.ThrowIfNull(hostType);

        var defects = new List<RouteCarriageDefect>();

        List<PluginRouteAxis> rows = [.. declared.Where(r => string.Equals(r.Route, routeId, StringComparison.Ordinal))];
        HashSet<string> registeredDoors = registered.Select(a => a.Door).ToHashSet(StringComparer.Ordinal);

        // (1) An axis the route says nothing about. This is the defect #1723
        //     reports: the axis set grew, the route's verdict for the new axis
        //     was never written down, and the axis was therefore dropped in
        //     silence — the half-open axis at the transport level.
        foreach (RegisteredAxis axis in registered
                     .Where(a => !rows.Any(r => string.Equals(r.Door, a.Door, StringComparison.Ordinal)))
                     .OrderBy(a => a.Door, StringComparer.Ordinal))
        {
            defects.Add(new RouteCarriageDefect(
                RouteCarriageDefectKind.Undeclared,
                $"{axis.Marker}→{axis.Door}: the {routeId} route does not say what it does with this axis"));
        }

        // (2) A row naming a door the product no longer has. A row that
        //     outlives its subject is a permission for the next person: it
        //     says the route has thought about an axis that is gone.
        foreach (PluginRouteAxis row in rows
                     .Where(r => !registeredDoors.Contains(r.Door))
                     .OrderBy(r => r.Door, StringComparer.Ordinal))
        {
            defects.Add(new RouteCarriageDefect(
                RouteCarriageDefectKind.Stale,
                $"{row.Door}: the {routeId} route declares a verdict for a door no axis owns"));
        }

        // (3) A verdict with no reason. The reason is what a plugin author acts
        //     on; a bare "refused" is the defect in a different shape.
        foreach (PluginRouteAxis row in rows
                     .Where(r => string.IsNullOrWhiteSpace(r.Reason))
                     .OrderBy(r => r.Door, StringComparer.Ordinal))
        {
            defects.Add(new RouteCarriageDefect(
                RouteCarriageDefectKind.MissingReason,
                $"{row.Door}: the {routeId} route declares a verdict with no reason for it"));
        }

        // (4) The route's load host. The declaration names it; the source scan
        //     has to find it, or the verdicts below are graded against nothing.
        List<DoorImplementation> hostDoors = [.. routeDoors.Where(d => string.Equals(d.Owner, hostType, StringComparison.Ordinal))];
        if (hostDoors.Count == 0)
        {
            defects.Add(new RouteCarriageDefect(
                RouteCarriageDefectKind.RouteHostMissing,
                $"{hostType}: the {routeId} route names this load host, and the product has no such "
                + "implementation of IPluginLoadHost"));
        }

        // (5) The implementation against the verdict.
        foreach (PluginRouteAxis row in rows.OrderBy(r => r.Door, StringComparer.Ordinal))
        {
            // The door is a RECORD STRUCT, so `FirstOrDefault` would hand back
            // `default(DoorImplementation)` — a value, not a miss. The index
            // is what says "not found".
            int index = hostDoors.FindIndex(d => string.Equals(d.Door, row.Door, StringComparison.Ordinal));
            if (index < 0)
            {
                if (hostDoors.Count > 0)
                {
                    defects.Add(new RouteCarriageDefect(
                        RouteCarriageDefectKind.CarriageMismatch,
                        $"{row.Door}: the {routeId} route declares a verdict for it, but {hostType} has no such door"));
                }

                continue;
            }

            DoorImplementation door = hostDoors[index];
            bool claimsSuccess = ExtensionAxisDoorGrader.ClaimsSuccess(door.Body);
            bool claimsFailure = FailurePattern.IsMatch(door.Body);

            switch (row.Carriage)
            {
                case PluginAxisCarriage.Carried:
                    if (!claimsSuccess)
                    {
                        defects.Add(new RouteCarriageDefect(
                            RouteCarriageDefectKind.CarriageMismatch,
                            $"{door.Owner}.{door.Door} ({door.File}): the {routeId} route declares this axis "
                            + "CARRIED, but the door never answers Result.Success() — it refuses the "
                            + "contribution the declaration promises to deliver"));
                    }
                    else if (!ExtensionAxisDoorGrader.HasEffect(door.Body))
                    {
                        defects.Add(new RouteCarriageDefect(
                            RouteCarriageDefectKind.CarriageMismatch,
                            $"{door.Owner}.{door.Door} ({door.File}): the {routeId} route declares this axis "
                            + "CARRIED, but the door answers Result.Success() without registering anything "
                            + "(#1758)"));
                    }

                    break;

                case PluginAxisCarriage.Refused:
                case PluginAxisCarriage.ClosedSeam:
                    if (!claimsFailure)
                    {
                        defects.Add(new RouteCarriageDefect(
                            RouteCarriageDefectKind.CarriageMismatch,
                            $"{door.Owner}.{door.Door} ({door.File}): the {routeId} route declares this axis "
                            + "REFUSED, but the door never answers Result.Failure() — a refusal that is not "
                            + "reported is a silent drop"));
                    }
                    else if (claimsSuccess)
                    {
                        defects.Add(new RouteCarriageDefect(
                            RouteCarriageDefectKind.CarriageMismatch,
                            $"{door.Owner}.{door.Door} ({door.File}): the {routeId} route declares this axis "
                            + "REFUSED, but the door also answers Result.Success() — a contribution the route "
                            + "says it cannot carry must never be reported as registered"));
                    }

                    break;
            }
        }

        return defects;
    }
}

/// <summary>
///     Guard for issue #1723: the out-of-process route SAYS what it does with
///     every extension axis, gives a reason for every verdict, and its
///     implementation matches the verdict.
/// </summary>
public sealed class ExtensionAxisRouteCarriageRule
{
    private static readonly Lazy<IReadOnlyList<DoorImplementation>> Doors = new(
        () => ExtensionAxisDoorProbe.ReadDoors(
            RepoPaths.RepoRoot,
            ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost))));

    /// <summary>
    ///     The axes the product registers. Read from <c>PluginAxisRegistry</c> —
    ///     the ONE list — so this file keeps no copy of the axis set.
    /// </summary>
    private static IReadOnlyList<RegisteredAxis> Registry =>
        [.. PluginAxisRegistry.All.Select(a => new RegisteredAxis(a.Marker, a.Door))];

    /// <summary>Every defect the grader finds against the real inventory.</summary>
    private static IReadOnlyList<RouteCarriageDefect> Defects =>
        ExtensionAxisRouteCarriageGrader.Grade(
            PluginRouteAxisRegistry.All,
            Registry,
            Doors.Value,
            PluginRouteAxisRegistry.McpStdioRouteId,
            PluginRouteAxisRegistry.McpStdioHostType);

    /// <summary>
    ///     Every axis has a stated fate on the out-of-process route, and every
    ///     fate has a reason.
    /// </summary>
    [Test]
    public async Task EveryAxis_SaysWhatTheOutOfProcRouteDoesWithIt()
    {
        var defects = Defects
            .Where(d => d.Kind is RouteCarriageDefectKind.Undeclared
                or RouteCarriageDefectKind.Stale
                or RouteCarriageDefectKind.MissingReason)
            .ToList();

        await Assert.That(string.Join(" | ", defects.Select(d => d.Detail)))
            .IsEqualTo(string.Empty)
            .Because(
                "the axis set is additive since #1719, so a new axis can be added — but the route that "
                + "serves the plugins has to say what it does with it. An axis with no declared verdict is "
                + "dropped in silence, and a silent drop looks exactly like an axis nobody thought about: "
                + "that is the defect #1723 reports, and it is why the verdict lives in the product "
                + "(PluginRouteAxisRegistry) instead of in a README table nobody grades. A verdict without a "
                + "reason is the same defect in a different shape: the plugin author gets a refusal and "
                + "nothing to act on. Undeclared/stale/unreasoned: "
                + (defects.Count == 0 ? "(none)" : string.Join(" | ", defects.Select(d => d.Detail))));
    }

    /// <summary>
    ///     The route's implementation does what the declaration says: a carried
    ///     axis registers, a refused axis answers <c>Result.Failure</c>.
    /// </summary>
    [Test]
    public async Task TheRoutesImplementation_MatchesTheDeclaredVerdict()
    {
        var defects = Defects
            .Where(d => d.Kind is RouteCarriageDefectKind.CarriageMismatch
                or RouteCarriageDefectKind.RouteHostMissing)
            .ToList();

        await Assert.That(string.Join(" | ", defects.Select(d => d.Detail)))
            .IsEqualTo(string.Empty)
            .Because(
                "the declaration is only worth something if the code obeys it. A CARRIED axis whose door "
                + "never answers Result.Success() is a promise the route does not keep; a REFUSED axis whose "
                + "door answers Result.Success() is the lie #1758 is about, in the opposite direction. "
                + "Mismatched: "
                + (defects.Count == 0 ? "(none)" : string.Join(" | ", defects.Select(d => d.Detail))));
    }

    /// <summary>
    ///     The scan really walked a checkout, really found the route's load host
    ///     and really found its doors; the declaration and the axis registry are
    ///     not empty.
    /// </summary>
    [Test]
    public async Task TheScanAndTheDeclarationAreLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it finds no doors and every rule "
                   + "above is satisfied by having nothing to look at");

        await Assert.That(PluginRouteAxisRegistry.All.Count).IsGreaterThan(0)
            .Because("the declaration is the route's verdict per axis; an empty one says nothing about "
                   + "anything");

        await Assert.That(Registry.Count).IsGreaterThan(0)
            .Because("the axis registry is the list of axes the route has to have a verdict for");

        var owners = Doors.Value.Select(d => d.Owner).Distinct().ToList();
        await Assert.That(owners.Contains(PluginRouteAxisRegistry.McpStdioHostType)).IsTrue()
            .Because(
                "the route names the load host that serves it, and the source scan has to find that host's "
                + "doors — otherwise the verdicts are graded against nothing. Found load hosts: "
                + string.Join(", ", owners));

        IReadOnlyList<string> interfaceDoors = ExtensionAxisProbe.ReadDoors(typeof(IPluginLoadHost));
        var missing = interfaceDoors
            .Where(d => !Doors.Value.Any(found => string.Equals(found.Door, d, StringComparison.Ordinal)))
            .ToList();

        await Assert.That(string.Join(" | ", missing)).IsEqualTo(string.Empty)
            .Because("the scan has to reach every door the interface declares, or the perimeter is wrong. "
                   + "A door the scan cannot see is a door it cannot grade. Missing: "
                   + (missing.Count == 0 ? "(none)" : string.Join(" | ", missing)));
    }

    /// <summary>
    ///     THE PROOF THAT THE GRADER CAN FAIL. An axis with no declared fate
    ///     and a verdict the implementation contradicts have to be reported; a
    ///     declaration that matches the implementation must not be.
    /// </summary>
    [Test]
    public async Task NegativeControl_TheGraderCatchesAnInjectedViolation()
    {
        // ---- The real inventory is clean. ----
        var clean = Defects;

        await Assert.That(string.Join(" | ", clean.Select(d => d.Detail))).IsEqualTo(string.Empty)
            .Because(
                "the negative control below proves the grader reports an INJECTED violation; that is only "
                + "evidence if the real inventory reports nothing, otherwise the report could have come "
                + "from a pre-existing defect. Real defects: "
                + (clean.Count == 0 ? "(none)" : string.Join(" | ", clean.Select(d => d.Detail))));

        // ---- INJECTION 1: an axis with no declared fate on the route. ----
        var withoutOneRow = PluginRouteAxisRegistry.All
            .Where(r => !string.Equals(r.Door, nameof(IPluginLoadHost.RegisterAgent), StringComparison.Ordinal))
            .ToList();

        var undeclared = ExtensionAxisRouteCarriageGrader.Grade(
            withoutOneRow,
            Registry,
            Doors.Value,
            PluginRouteAxisRegistry.McpStdioRouteId,
            PluginRouteAxisRegistry.McpStdioHostType);

        await Assert.That(undeclared.Any(d => d.Kind is RouteCarriageDefectKind.Undeclared
                                             && d.Detail.Contains(nameof(IPluginLoadHost.RegisterAgent), StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "an axis the route says nothing about is the defect #1723 is about — the shape a new axis "
                + "takes when nobody writes down what the route does with it. If the grader does not report "
                + "it, the gate enforces nothing. Reported: "
                + (undeclared.Count == 0 ? "(none)" : string.Join(" | ", undeclared.Select(d => d.Detail))));

        // ---- INJECTION 2: a verdict the implementation contradicts. ----
        // The tool axis IS carried, and its door does register. Declaring it
        // REFUSED must be reported: the door answers Result.Success().
        var contradicted = PluginRouteAxisRegistry.All
            .Select(r => string.Equals(r.Door, nameof(IPluginLoadHost.RegisterTool), StringComparison.Ordinal)
                ? r with { Carriage = PluginAxisCarriage.Refused }
                : r)
            .ToList();

        var mismatch = ExtensionAxisRouteCarriageGrader.Grade(
            contradicted,
            Registry,
            Doors.Value,
            PluginRouteAxisRegistry.McpStdioRouteId,
            PluginRouteAxisRegistry.McpStdioHostType);

        await Assert.That(mismatch.Any(d => d.Kind is RouteCarriageDefectKind.CarriageMismatch
                                            && d.Detail.Contains(nameof(IPluginLoadHost.RegisterTool), StringComparison.Ordinal)))
            .IsTrue()
            .Because(
                "a declaration the implementation contradicts is the drift this rule exists to catch: the "
                + "route says it refuses the tool axis while its door registers the tool. If the grader does "
                + "not report it, the declaration is prose again. Reported: "
                + (mismatch.Count == 0 ? "(none)" : string.Join(" | ", mismatch.Select(d => d.Detail))));

        // ---- THE OTHER HALF: a declaration that matches must stay silent. ----
        var agreeing = ExtensionAxisRouteCarriageGrader.Grade(
            PluginRouteAxisRegistry.All,
            Registry,
            Doors.Value,
            PluginRouteAxisRegistry.McpStdioRouteId,
            PluginRouteAxisRegistry.McpStdioHostType);

        await Assert.That(string.Join(" | ", agreeing.Select(d => d.Detail))).IsEqualTo(string.Empty)
            .Because(
                "a grader that reports everything is as useless as one that reports none — it would fail the "
                + "product's honest declarations and the rule would be turned off. The real declaration "
                + "matches the real implementation and must not be reported. Reported: "
                + (agreeing.Count == 0 ? "(none)" : string.Join(" | ", agreeing.Select(d => d.Detail))));
    }
}
