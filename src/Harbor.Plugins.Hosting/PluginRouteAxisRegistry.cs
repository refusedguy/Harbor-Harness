// PluginRouteAxisRegistry.cs — what each ROUTE does with each axis of extension.
//
// WHY THIS FILE EXISTS (issue #1723)
// ----------------------------------
// `PluginAxisRegistry` (src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs)
// says WHICH AXES EXIST: seven of them, each a marker plus a door. It says
// nothing about whether an axis REACHES the user of a given route.
//
// The product has exactly one route for CS-source plugins today: the
// out-of-process MCP stdio host (`harbor-plugins-host`, located by
// `PluginHostLocator` and registered by the CLI as the `harbor-csharp-plugins`
// MCP server). That route serves TOOLS over the wire and refuses the other six
// doors — loudly, with a reason, which is the honesty #1758 bought.
//
// "Loudly, with a reason" is not the same as "stated". Until now the reason
// for each refusal lived in PROSE: the parity table in
// `src/Harbor.Plugins.Host/README.md` and a paragraph in
// `docs/PLUGIN_SYSTEM.md`. Prose is not graded, so it drifts, and a reader
// who never opens the README is left with the interface's XML documentation
// and no way to tell that five of the seven axes are not on the default
// route. That is the defect #1723 is about: a route that drops an axis in
// silence looks exactly like an axis nobody ever thought about.
//
// So this file states it, in the product, next to the code that has to obey
// it: for every axis, what the route does with it and WHY. The refusal
// messages are BUILT from this file, so the sentence a plugin author reads is
// the sentence this file declares — there is no second copy to drift.
//
// WHAT THE THREE VERDICTS MEAN
// ----------------------------
//   * CARRIED — the axis crosses the route's process boundary in the form it
//     has in-process, and the route's door really registers the contribution.
//   * REFUSED — the axis works in-process, but THIS route cannot carry it: the
//     contribution is a live in-process object (a streaming client, a view, a
//     store, a renderer) or needs a wire protocol the route does not have. The
//     door answers `Result.Failure` with the reason below, and the plugin's
//     registration fails with it (#1758) — nothing is dropped in silence.
//   * CLOSED SEAM — the axis is closed EVERYWHERE by owner decision (#564 for
//     the TUI-view seam), so no route carries it, not even in-process. The
//     door still answers `Result.Failure`; the verdict only says that this
//     route is not the reason the axis is dark.
//
// WHY THE REASON IS A TECHNICAL ONE
// ---------------------------------
// "not implemented" is not a reason: it describes the route, not the axis, and
// it gives a plugin author nothing to act on. Each reason below names WHAT IS
// MISSING FROM THE WIRE. A verdict can change — that is the point of stating
// it — but it cannot change silently, because the rule below grades the code
// against this file.
//
// WHAT GRADES THIS FILE
// ---------------------
// `tests/Harbor.Architecture.Tests/ExtensionAxisRouteCarriageRule.cs` reads
// this file and grades three things:
//
//   1. every registered axis has a row here — a new axis cannot be added
//      without saying what the route does with it;
//   2. every row has a reason;
//   3. the route's implementation matches the verdict — a carried axis really
//      registers something, a refused axis really answers `Result.Failure`.
//
// The per-axis reasons are repeated, in prose, in the rule file: a rule that
// says "a refused axis must have a reason" without saying WHY THAT AXIS is
// refused teaches nobody anything, and the reader who has to decide the next
// verdict is the reader of the rule.

using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Hosting;

/// <summary>
///     What a route does with one axis of extension.
/// </summary>
public enum PluginAxisCarriage
{
    /// <summary>
    ///     The axis crosses the route's process boundary in the form it has
    ///     in-process, and the route's door registers the contribution for real.
    /// </summary>
    Carried,

    /// <summary>
    ///     The axis works in-process, but this route cannot carry it. The door
    ///     answers <c>Result.Failure</c> with the declared reason, and the
    ///     plugin's registration fails with it — the contribution is refused,
    ///     never dropped in silence.
    /// </summary>
    Refused,

    /// <summary>
    ///     The axis is closed everywhere by owner decision, so no route carries
    ///     it — not in-process either. The door still answers
    ///     <c>Result.Failure</c>; the verdict says that this route is not the
    ///     reason the axis is dark.
    /// </summary>
    ClosedSeam,
}

/// <summary>
///     One axis on one route: the verdict and the reason for it.
/// </summary>
/// <param name="Route">Id of the route the verdict belongs to.</param>
/// <param name="Door">
///     The <c>Register*</c> door on <see cref="IPluginLoadHost" /> the verdict is
///     about. Spelled as a <c>nameof</c> expression, so a renamed door breaks
///     the build here rather than the gate there.
/// </param>
/// <param name="Carriage">What the route does with the axis.</param>
/// <param name="Reason">
///     Why — one sentence, quoted to the plugin author when the route refuses
///     the axis. It names what is missing from the wire, not what is missing
///     from the code.
/// </param>
public sealed record PluginRouteAxis(string Route, string Door, PluginAxisCarriage Carriage, string Reason);

/// <summary>
///     The per-route fate of every extension axis — the one place that says
///     which axes a route carries and why the others do not cross its boundary.
/// </summary>
/// <remarks>
///     <para>
///         The axes themselves are declared in
///         <see cref="PluginAxisRegistry" />, which is the single list of what
///         a plugin may contribute. This file is the second half of that
///         statement: what the PRODUCT'S ROUTE does with each of them. The two
///         are different questions, and #1723 is the defect of answering only
///         the first one.
///     </para>
///     <para>
///         The rows are keyed by DOOR, and the door is a <c>nameof</c>
///         expression over <see cref="IPluginLoadHost" />, so the door half of
///         the pair cannot drift from the interface. The marker half is
///         resolved from <see cref="PluginAxisRegistry" />, so it is not
///         written twice either.
///     </para>
/// </remarks>
public static class PluginRouteAxisRegistry
{
    /// <summary>
    ///     Id of the out-of-process route: the CLI talks to <c>harbor-plugins-host</c>
    ///     over MCP stdio, and the plugin's tools arrive as MCP tools.
    /// </summary>
    public const string McpStdioRouteId = "mcp-stdio";

    /// <summary>
    ///     The load host that serves the out-of-process route.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Named rather than referenced: the host lives in
    ///         <c>src/Harbor.Plugins.Host/</c>, which is an executable the
    ///         architecture test project deliberately does not reference (it is
    ///         a composition root, like <c>apps/*</c>). The name is what the
    ///         architecture rule matches the scanned implementation against, and
    ///         a rename that misses this string is reported by that rule rather
    ///         than ignored.
    ///     </para>
    /// </remarks>
    public const string McpStdioHostType = "McpPluginLoadHost";

    /// <summary>
    ///     Every axis on every route the product has, with the verdict and the
    ///     reason for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The order follows <see cref="PluginAxisRegistry" /> — the order
    ///         the host declares its doors — so the two lists read side by side.
    ///     </para>
    ///     <para>
    ///         The reasons, one per axis:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <b>IToolPlugin — CARRIED.</b> The tool's name, description
    ///                 and input schema travel on <c>tools/list</c> and its result
    ///                 on <c>tools/call</c>, which is the whole tool contract the
    ///                 core needs. The tool is not a first-class entry in the
    ///                 core's tool registry — the core reaches it through the
    ///                 <c>mcp</c> bridge tool — and the tool's own safety profile
    ///                 stays with the host that executes it.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>IProviderPlugin — REFUSED.</b> An <c>ILlmClient</c> is a
    ///                 live streaming client: the wire would have to carry
    ///                 per-turn token deltas and the provider's credentials, and
    ///                 the MCP tool protocol carries neither.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>IAgentPlugin — REFUSED.</b> An <c>AgentDefinition</c> is
    ///                 data and could be carried, but the core evaluates its
    ///                 <c>PermissionRuleset</c> per call and freezes its agent
    ///                 registry at startup — before the lazily spawned plugin
    ///                 host is asked anything. Carrying it needs a startup
    ///                 handshake this route deliberately does not have.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>ITuiPlugin — CLOSED SEAM (#564).</b> The plugin's TUI
    ///                 views are collected and never rendered, in-process or
    ///                 out-of-process; this route does not reopen that seam.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>ITuiPanelPlugin — REFUSED.</b> A panel is live UI-process
    ///                 state — it builds a view object and handles keys — so it
    ///                 is not a registration that can be serialized across a
    ///                 process boundary. Carrying it needs a panel protocol,
    ///                 which the plugin showcase (#1434) is the right place to
    ///                 design.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>ISessionStorePlugin — REFUSED.</b> The store is resolved
    ///                 in the core at startup through <c>HARBOR_STORAGE</c>; a
    ///                 store behind the pipe would put every session read and
    ///                 write on IPC, and the core's store contract has no remote
    ///                 form.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>ITuiBackendPlugin — REFUSED.</b> A renderer is
    ///                 constructed in the UI process and paints frames; a backend
    ///                 factory cannot be expressed as an MCP tool, so the
    ///                 renderer axis has no wire form.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public static readonly IReadOnlyList<PluginRouteAxis> All =
    [
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterTool),
            PluginAxisCarriage.Carried,
            "the tool's name, description and input schema travel on tools/list and its result on "
            + "tools/call, so the core can call it; the tool's own safety profile stays with the host "
            + "that executes it"),
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterProvider),
            PluginAxisCarriage.Refused,
            "an ILlmClient is a live streaming client: the wire would have to carry per-turn token deltas "
            + "and the provider's credentials, and the MCP tool protocol carries neither"),
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterAgent),
            PluginAxisCarriage.Refused,
            "the core evaluates the agent's PermissionRuleset per call and freezes its agent registry at "
            + "startup, before the lazily spawned plugin host is asked anything; carrying an agent needs a "
            + "startup handshake this route deliberately does not have"),
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterTuiPlugin),
            PluginAxisCarriage.ClosedSeam,
            "the seam is closed by owner decision (#564): the plugin's TUI views are collected and never "
            + "rendered, in-process or out-of-process"),
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterPanelProvider),
            PluginAxisCarriage.Refused,
            "a panel is live UI-process state — it builds a view object and handles keys — so it is not a "
            + "registration that can be serialized across a process boundary; carrying it needs a panel "
            + "protocol, which the plugin showcase (#1434) is the place to design"),
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterSessionStore),
            PluginAxisCarriage.Refused,
            "the store is resolved in the core at startup through HARBOR_STORAGE; a store behind the pipe "
            + "would put every session read and write on IPC, and the core's store contract has no remote "
            + "form"),
        new(
            McpStdioRouteId,
            nameof(IPluginLoadHost.RegisterTuiBackend),
            PluginAxisCarriage.Refused,
            "a renderer is constructed in the UI process and paints frames; a backend factory cannot be "
            + "expressed as an MCP tool, so the renderer axis has no wire form"),
    ];

    /// <summary>
    ///     Every row of one route.
    /// </summary>
    /// <param name="route">The route id.</param>
    public static IReadOnlyList<PluginRouteAxis> ForRoute(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return [.. All.Where(r => string.Equals(r.Route, route, StringComparison.Ordinal))];
    }

    /// <summary>
    ///     The row of one route for one door, or <c>null</c> when the route does
    ///     not declare that door.
    /// </summary>
    /// <param name="route">The route id.</param>
    /// <param name="door">The door name, as declared on <see cref="IPluginLoadHost" />.</param>
    public static PluginRouteAxis? Find(string route, string door)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(door);
        return All.FirstOrDefault(r => string.Equals(r.Route, route, StringComparison.Ordinal)
                                       && string.Equals(r.Door, door, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The marker of the axis a door belongs to, resolved from
    ///     <see cref="PluginAxisRegistry" />.
    /// </summary>
    /// <param name="door">The door name, as declared on <see cref="IPluginLoadHost" />.</param>
    /// <returns>The marker name, or the door name when the axis is not registered.</returns>
    public static string MarkerOf(string door)
    {
        ArgumentNullException.ThrowIfNull(door);
        return PluginAxisRegistry.All
                   .FirstOrDefault(a => string.Equals(a.Door, door, StringComparison.Ordinal))?.Marker
               ?? door;
    }

    /// <summary>
    ///     The message a route's door answers with when it refuses an axis.
    /// </summary>
    /// <remarks>
    ///     Built from the declared row, so the reason a plugin author reads is
    ///     the reason the product declares. A door that refuses without saying
    ///     why is the defect this whole file exists to prevent.
    /// </remarks>
    /// <param name="route">The route id.</param>
    /// <param name="door">The door name, as declared on <see cref="IPluginLoadHost" />.</param>
    /// <param name="subject">What was being registered — the provider id, the panel id, …</param>
    public static string RefusalMessage(string route, string door, string subject)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(door);

        PluginRouteAxis? row = Find(route, door);
        string marker = MarkerOf(door);
        string reason = row?.Reason
                        ?? "the route does not declare this axis, so it cannot say why it refuses it";
        return $"The {route} plugin route does not carry the {marker} axis; '{subject}' was not registered: {reason}";
    }
}
