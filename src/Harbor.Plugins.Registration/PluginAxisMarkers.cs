// PluginAxisMarkers.cs — the ONE table that says which extension axes a
// plugin implements.
//
// WHY THIS FILE EXISTS (issue #1722)
// ----------------------------------
// Until now that table was a PRIVATE nested record inside `PluginRegistrar`:
// the registrar read it to dispatch the markers and to check a plugin's
// manifest against them (#1739), and nobody else could read it.
//
// #1722 needs to say the same thing in a machine-readable inventory: which
// plugins are loaded, which axes each one carries, and what it registered in
// the host. If the inventory spelled the `is` checks out a second time, the
// two tables could disagree — and the disagreement would be invisible, because
// both of them would look right.
//
// So the table MOVED here, unchanged, and both readers read it. There is still
// exactly ONE table: this one. That is the whole point of the move.
//
// THE TABLE AND THE DECLARATION ARE DIFFERENT ANSWERS
// ----------------------------------------------------
// `PluginAxisRegistry` (src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs)
// names the axes that EXIST — the marker/door pairs the product declares.
// This type says which of those axes a given plugin INSTANCE implements. The
// first is the declaration, the second is the observation, and the inventory
// reports both: the axes are the registry's names, and the plugin's set is
// read from the plugin.
//
// WHY THE MARKERS ARE FOUND BY TYPE AND NOT BY NAME
// ---------------------------------------------------
// The `is` checks below ARE the axis table: each one names a marker the
// registrar dispatches. The dispatch reads the result, and the manifest check
// compares the manifest against it, so an axis cannot be dispatched without
// being declared, or declared in a manifest without being dispatched.
//
// The checks are written in the `is` form on purpose: the architecture rule
// that grades the axes finds a dispatched marker by that shape
// (`tests/Harbor.Architecture.Tests/ExtensionAxisRegistrationRule.cs`).

using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using Harbor.Terminal.Abstractions.Plugins;
using Harbor.Ui.Framework.Panels;

namespace Harbor.Plugins.Registration;

/// <summary>
///     The extension-axis markers a plugin implements — read off a live plugin
///     instance, once, by one table that every reader shares.
/// </summary>
/// <remarks>
///     <para>
///         The registrar dispatches on this list and checks the plugin's
///         manifest against it; the plugin inventory (#1722) reports it. Two
///         readers, one table.
///     </para>
/// </remarks>
/// <param name="Names">
///     The names of the axes the plugin implements, in the order the registry
///     declares them. The name is the type's simple name — the spelling
///     <c>PluginAxisRegistry</c> uses and the spelling a manifest writes down.
/// </param>
/// <param name="Tool">The tool axis, when the plugin implements it.</param>
/// <param name="Provider">The provider axis, when the plugin implements it.</param>
/// <param name="Agent">The agent axis, when the plugin implements it.</param>
/// <param name="Tui">The TUI-plugin axis, when the plugin implements it.</param>
/// <param name="Panel">The panel axis, when the plugin implements it.</param>
/// <param name="SessionStore">The session-store axis, when the plugin implements it.</param>
/// <param name="TuiBackend">The TUI-backend axis, when the plugin implements it.</param>
public sealed record PluginAxisMarkers(
    IReadOnlyList<string> Names,
    IToolPlugin? Tool,
    IProviderPlugin? Provider,
    IAgentPlugin? Agent,
    ITuiPlugin? Tui,
    ITuiPanelPlugin? Panel,
    ISessionStorePlugin? SessionStore,
    ITuiBackendPlugin? TuiBackend)
{
    /// <summary>
    ///     Read the markers off a plugin instance.
    /// </summary>
    /// <param name="instance">The live plugin.</param>
    /// <returns>The markers it implements.</returns>
    public static PluginAxisMarkers Of(IPlugin instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        IToolPlugin? tool = instance is IToolPlugin typedTool ? typedTool : null;
        IProviderPlugin? provider = instance is IProviderPlugin typedProvider ? typedProvider : null;
        IAgentPlugin? agent = instance is IAgentPlugin typedAgent ? typedAgent : null;
        ITuiPlugin? tui = instance is ITuiPlugin typedTui ? typedTui : null;
        ITuiPanelPlugin? panel = instance is ITuiPanelPlugin typedPanel ? typedPanel : null;
        ISessionStorePlugin? sessionStore =
            instance is ISessionStorePlugin typedStore ? typedStore : null;
        ITuiBackendPlugin? tuiBackend =
            instance is ITuiBackendPlugin typedBackend ? typedBackend : null;

        var names = new List<string>(7);
        AddMarker(tool, names);
        AddMarker(provider, names);
        AddMarker(agent, names);
        AddMarker(tui, names);
        AddMarker(panel, names);
        AddMarker(sessionStore, names);
        AddMarker(tuiBackend, names);

        return new PluginAxisMarkers(
            names,
            tool,
            provider,
            agent,
            tui,
            panel,
            sessionStore,
            tuiBackend);
    }

    /// <summary>
    ///     Add the axis name of a marker the plugin implements.
    /// </summary>
    /// <param name="marker">The marker, or <c>null</c> when the plugin does not implement it.</param>
    /// <param name="names">The list to add to.</param>
    /// <typeparam name="T">The marker interface.</typeparam>
    private static void AddMarker<T>(T? marker, List<string> names)
        where T : class
    {
        if (marker is not null)
            names.Add(typeof(T).Name);
    }
}
