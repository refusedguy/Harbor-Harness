# Harbor.Plugins.Registration

Concrete `IPluginRegistrar` implementations. Wires live plugins into the host's registries (tools, providers, agents, panels).

## Layer

Infrastructure — plugin pipeline layer. Depends on `Harbor.Plugins.Abstractions` + `Harbor.Plugins.Instantiation` (for `PluginLifecycle`).

## Dependencies

- `Harbor.Plugins.Abstractions`
- `Harbor.Plugins.Instantiation` (for PluginLifecycle helpers)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `PluginRegistrar` — dispatches `IPlugin.Initialize` + `Register*` methods into `IPluginLoadHost`.
  A door that REFUSES a registration fails the plugin's registration (#1758): the
  registrar used to log the refusal and answer `Success`, so the caller could not
  tell a fully-wired plugin from one whose provider/agent/panel/store was dropped.
- the #1721 compatibility gate (`Harbor.Plugins.Abstractions.PluginCompatibility`):
  a plugin that declares a `RequiredHarborVersion` newer than the running Harbor
  is refused BEFORE it is initialized, so it contributes nothing to the host.
  `RequiredHarborVersion` is a floor, not a pin: a newer Harbor still satisfies
  an older declaration.
- `SafePluginRegistrar` — try/catch decorator over any `IPluginRegistrar` (one bad plugin doesn't kill the pipeline)

The registrar also holds the LEDGER and the RUN (#1737):

- the LEDGER (`RecordingPluginLoadHost`) remembers every registration the host
  ACCEPTED, together with the way to take it back. A registration the host
  REFUSED was never recorded, so there is nothing to undo for it.
- the RUN (`PluginRun`, the `IPluginRun` the registrar hands back) owns that
  ledger. Stopping it takes back exactly the registrations it made, in reverse
  order, and then shuts the plugin down (`IPlugin.ShutdownAsync`, which the
  host used to declare and never call).

A FAILED registration leaves nothing behind: whatever landed before the refusal
is taken back, so a half-wired plugin does not become a dead tool in the
registry. The rollback needs a host that implements `IReversiblePluginLoadHost`;
a host that does not is told so, in the failure text, rather than being
pretended away.
- `PanelRegistryPluginAdapter` — internal adapter for `ITuiPanelPlugin` -> `IPluginLoadHost.RegisterPanelProvider`

The registrar also holds the manifest gate (#1739): a plugin that ships a
manifest is checked against it before it is initialized. The name and the
version the manifest declares must be the ones the plugin reports, and the
extension axes it declares must be the axes the plugin implements — otherwise
the plugin is refused with the reason.

## Usage

```csharp
IPluginRegistrar registrar = new SafePluginRegistrar(
    new PluginRegistrar(logger), logger);
await registrar.RegisterAsync(loadedPlugin, loadHost, ct);
```

## Pipeline position

```
Storage -> Compilation -> Instantiation -> Registration -> Hosting
  ^ this project (layer 4: registration)
```

## See also

- [../../docs/PLUGIN_SYSTEM.md](../../docs/PLUGIN_SYSTEM.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../samples/plugins/](../../samples/plugins/) — sample plugins (FileTree, GitTools, TodoWrite, WebSearch)
