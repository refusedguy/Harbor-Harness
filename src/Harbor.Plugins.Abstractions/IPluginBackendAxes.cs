// IPluginBackendAxes.cs — the two axes #581/#584 opened at the host end and
// never finished at the plugin end.
//
// WHAT WAS MISSING
// ----------------
// `IPluginLoadHost` has declared two backend doors since #581/#584 — one for
// session stores, one for TUI renderers — and everything AROUND them ships and
// is wired: `PluginSessionStoreFactory` wraps a plugin store,
// `SessionStoreRegistry` folds plugin ids into the same index the compiled-in
// backends live in, `StorageModule` resolves from it; `PluginTuiBackend` does
// the same for renderers and `TuiModule` folds those in
// so `/renderer` lists them. Both methods carry XML documentation describing
// the capability.
//
// What never landed is the third line. `PluginRegistrar.Register` — the only
// place a plugin instance is dispatched — had branches for the five markers
// that existed and none for these, and a plugin is never handed the host to call
// a door on: `PluginContext` carries Services, Configuration, LoggerFactory,
// EventBus, PluginDirectory and DataDirectory, and NOT IPluginLoadHost. So the
// storage and renderer axes were open on the host side and unreachable on the
// plugin side — a sealed interface promising a capability no plugin had any way
// to use. `ExtensionAxisFreezeRule` (#620) is what found it; this file is the
// other half of that fix.
//
// WHY A NARROW SINK AND NOT IPluginLoadHost ITSELF
// -------------------------------------------------
// A plugin is handed a sink with exactly ONE method — the door for its own axis
// — and never the host. Handing over `IPluginLoadHost` would work, and it would
// quietly reopen every other axis to every plugin: a storage plugin could then
// register tools and agents. The one-method sink is the same reason
// `IToolPlugin` gets an `IToolRegistryBuilder` rather than the tool registry's
// whole surface, and the same reason `ITuiPanelPlugin` gets an `IPanelRegistry`
// rather than the host.
//
// The sinks are deliberately NOT named `…Plugin`: a name ending in `Plugin` is
// what #620's guard reads as a new axis, and these are the receiving end of an
// axis that already exists, not a new one.
//
// This file reaches into Harbor.Terminal.Abstractions for `ITuiRenderer`, which
// is a NAMED site in FullLayerMatrixTests' documented exceptions for exactly the
// reason `IPluginLoadHost.cs` is: the renderer axis is TUI vocabulary and its
// contract cannot be spelled without naming the renderer. Adding this file to
// that list is the layer matrix recording the edge, not widening an excuse.

using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Sessions;
using Harbor.Terminal.Abstractions;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     The single door a <see cref="ISessionStorePlugin" /> may push through.
/// </summary>
/// <remarks>
///     One method, so a plugin contributing a session store cannot reach any
///     other axis. Forwarded verbatim to
///     <see cref="IPluginLoadHost.RegisterSessionStore" />.
/// </remarks>
public interface ISessionStoreRegistrar
{
    /// <summary>
    ///     Register a session-store backend under <paramref name="backendId" />, so it
    ///     becomes selectable via <c>HARBOR_STORAGE</c>.
    /// </summary>
    /// <param name="backendId">Backend id, as spelled in <c>HARBOR_STORAGE</c>.</param>
    /// <param name="factory">Constructs the store; invoked lazily, when the store is first resolved.</param>
    /// <returns>Success, or failure with an error message (e.g. empty id, duplicate).</returns>
    public Result RegisterSessionStore(string backendId, Func<ISessionStore> factory);
}

/// <summary>
///     Plugin that contributes one or more session-store backends (#581).
/// </summary>
/// <remarks>
///     The backend is folded into the same registry the compiled-in backends live
///     in, so a plugin id that collides with a compiled-in one wins — loudly,
///     through a <see cref="Result" />, not silently.
/// </remarks>
public interface ISessionStorePlugin : IPlugin
{
    /// <summary>
    ///     Register the plugin's session stores through the supplied registrar.
    /// </summary>
    /// <param name="registrar">The session-store registrar.</param>
    public void RegisterSessionStores(ISessionStoreRegistrar registrar);
}

/// <summary>
///     The single door a <see cref="ITuiBackendPlugin" /> may push through.
/// </summary>
/// <remarks>
///     One method, for the same reason as <see cref="ISessionStoreRegistrar" />.
///     Forwarded verbatim to <see cref="IPluginLoadHost.RegisterTuiBackend" />.
/// </remarks>
public interface ITuiBackendRegistrar
{
    /// <summary>
    ///     Register a TUI renderer backend under <paramref name="backendId" />, so it
    ///     becomes selectable via <c>HARBOR_TUI</c> and swappable via <c>/renderer</c>.
    /// </summary>
    /// <param name="backendId">Backend id, as spelled in <c>HARBOR_TUI</c>.</param>
    /// <param name="aliases">Optional legacy spellings resolved to <paramref name="backendId" />.</param>
    /// <param name="factory">Constructs the renderer; invoked when the backend is selected or swapped to.</param>
    /// <returns>Success, or failure with an error message (e.g. empty id, duplicate).</returns>
    public Result RegisterTuiBackend(string backendId, IReadOnlyList<string>? aliases, Func<ITuiRenderer> factory);
}

/// <summary>
///     Plugin that contributes one or more TUI renderer backends (#581/#584).
/// </summary>
/// <remarks>
///     The host folds the backend into the same registry the compiled-in backends
///     live in, so <c>/renderer</c> lists it and a runtime swap can target it —
///     there is no second list a plugin backend could be forgotten from.
/// </remarks>
public interface ITuiBackendPlugin : IPlugin
{
    /// <summary>
    ///     Register the plugin's renderer backends through the supplied registrar.
    /// </summary>
    /// <param name="registrar">The TUI backend registrar.</param>
    public void RegisterTuiBackends(ITuiBackendRegistrar registrar);
}
