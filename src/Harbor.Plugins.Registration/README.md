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
- `PluginCompatibility`: the #1721 compatibility gate. A plugin that declares a
  `RequiredHarborVersion` newer than the running Harbor is refused BEFORE it is
  initialized, so it contributes nothing to the host. `RequiredHarborVersion` is a
  floor, not a pin: a newer Harbor still satisfies an older declaration.
- `SafePluginRegistrar` — try/catch decorator over any `IPluginRegistrar` (one bad plugin doesn't kill the pipeline)
- `PanelRegistryPluginAdapter` — internal adapter for `ITuiPanelPlugin` -> `IPluginLoadHost.RegisterPanelProvider`

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
