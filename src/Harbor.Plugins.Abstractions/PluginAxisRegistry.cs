// PluginAxisRegistry.cs — the single registry of plugin extension axes.
//
// WHY THIS FILE EXISTS (issue #1719)
// ----------------------------------
// #555 froze the axis of extension: a plugin could contribute a tool, a
// provider, an agent, a TUI plugin, a panel, a session store and a TUI
// backend, and no eighth kind. The freeze was LIFTED by owner decision on
// 2026-10-20 — its exit condition was met, the audit issues it named
// (#557, #560, #564, #568, #580-#584) are closed and the seams they fenced
// became additive. What the freeze bought was a hand-maintained list of the
// allowed axes, and the list was the price: opening a new axis meant editing
// a table in a test file, and the table was a SECOND copy of what the code
// already said.
//
// So the closed set is gone and the list is not. This file is the list — one
// declaration of what the extension surface is, in the product, next to the
// doors it names. It replaces the table that used to live in
// `tests/Harbor.Architecture.Tests/ExtensionAxisRegistrationRule.cs`, and the rule
// that grades it now reads THIS file rather than keeping a copy of its own.
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
// can ever reach. Either way the axis set is a lie. That class of defect is
// what the guard grades — see
// `tests/Harbor.Architecture.Tests/ExtensionAxisRegistrationRule.cs`.
//
// A door that ANSWERS `Result.Success()` without having registered anything is
// the same lie in a third shape: the axis exists, the door is called, and the
// registration it promised did not happen. `IPluginLoadHost` states that
// contract in its own XML documentation, and
// `tests/Harbor.Architecture.Tests/ExtensionAxisDoorHonestyRule.cs` is the
// guard for it (#1758).
//
// WHAT THE REGISTRY DOES AND DOES NOT CLAIM
// -----------------------------------------
// It claims which axes EXIST. It does not claim they WORK: a registered axis
// whose door nobody calls is still broken, and the rule fails it. The two
// halves of the contract are separate on purpose — the registry is the
// declaration, the rule grades the wiring.
//
// A NEW AXIS IS ADDITIVE, NOT FORBIDDEN
// -------------------------------------
// Adding an eighth kind is allowed. What it is NOT allowed to be is
// undeclared: a new `Register*` door on IPluginLoadHost with no entry here,
// or a new public `IPlugin`-deriving interface with no entry here, fails the
// architecture gate. That is the whole successor contract: open an axis by
// DECLARING it (one line here) and WIRING it (the marker dispatched, the
// door invoked), not by deciding against a freeze.
//
// WHY THE MARKER IS A NAME AND THE DOOR IS A `nameof`
// ---------------------------------------------------
// The door is spelled `nameof(IPluginLoadHost.RegisterTool)`, so renaming a
// door breaks this file at COMPILE time — the half of the pair that lives in
// this assembly cannot drift from the door it names.
//
// The marker is a plain name, and that is a layer decision rather than a
// preference. `ITuiPlugin` lives in Harbor.Terminal.Abstractions, and
// Harbor.Plugins.Abstractions may reach that assembly only from the sites
// FullLayerMatrixTests documents (`IPluginLoadHost.cs`,
// `IPluginBackendAxes.cs`); a third file naming `ITuiPlugin` would have to
// widen a documented allowance to carry a string. The name is resolved
// against the product's public interfaces by the architecture rule instead,
// and a name that resolves to zero types — or to two — fails the gate. So
// the marker half cannot drift silently either; it just fails later and
// in a different place than the door half.

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     One axis of extension: the plugin-implementable marker and the host door
///     that receives it. The PAIR is the unit — a half is not an axis.
/// </summary>
/// <param name="Marker">
///     Simple name of the public interface a plugin implements to contribute
///     along this axis.
/// </param>
/// <param name="Door">
///     Name of the <c>Register*</c> member on <see cref="IPluginLoadHost" /> that
///     receives the contribution.
/// </param>
public sealed record PluginAxis(string Marker, string Door);

/// <summary>
///     The single registry of plugin extension axes — the one list that says
///     which kinds of thing a plugin may contribute.
/// </summary>
/// <remarks>
///     <para>
///         One list, in the product, and nothing else repeats it. The
///         architecture rule
///         (<c>tests/Harbor.Architecture.Tests/ExtensionAxisRegistrationRule.cs</c>)
///         reads this file and grades the code against it; it keeps no copy of
///         its own, so the two cannot drift apart without the gate going red.
///     </para>
///     <para>
///         The per-kind registries a contribution LANDS in — <c>ToolRegistry</c>,
///         <c>ProviderRegistry</c>, <c>AgentRegistry</c>,
///         <c>SessionStoreRegistry</c>, <c>TuiBackendRegistry</c>,
///         <c>IPanelRegistry</c> — are a different thing and are not repeated
///         here. They hold implementations; this holds axes. A plugin store and
///         a compiled-in store are the same kind of thing in the same registry
///         precisely so that there is no second list a plugin backend could be
///         forgotten from; the axis that gets it there is what this file
///         declares.
///     </para>
/// </remarks>
public static class PluginAxisRegistry
{
    /// <summary>
    ///     Every axis of extension, in the order the host declares its doors.
    /// </summary>
    /// <remarks>
    ///     The marker names are resolved against the product's public
    ///     interfaces by the architecture rule; the door names are
    ///     <c>nameof</c> expressions over <see cref="IPluginLoadHost" />, so a
    ///     renamed door fails the build here rather than the gate there.
    /// </remarks>
    public static readonly IReadOnlyList<PluginAxis> All =
    [
        new("IToolPlugin", nameof(IPluginLoadHost.RegisterTool)),
        new("IProviderPlugin", nameof(IPluginLoadHost.RegisterProvider)),
        new("IAgentPlugin", nameof(IPluginLoadHost.RegisterAgent)),
        new("ITuiPlugin", nameof(IPluginLoadHost.RegisterTuiPlugin)),
        new("ITuiPanelPlugin", nameof(IPluginLoadHost.RegisterPanelProvider)),
        new("ISessionStorePlugin", nameof(IPluginLoadHost.RegisterSessionStore)),
        new("ITuiBackendPlugin", nameof(IPluginLoadHost.RegisterTuiBackend)),
    ];
}
