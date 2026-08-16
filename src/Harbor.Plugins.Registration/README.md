# Harbor.Plugins.Registration

Concrete `IPluginRegistrar` implementations. Wires live plugins into the host's registries (tools, providers, agents, panels).

## Layer

Infrastructure — plugin pipeline layer. Depends on `Harbor.Plugins.Abstractions` + `Harbor.Plugins.Instantiation` (for `PluginLifecycle`).

## Dependencies

- `Harbor.Plugins.Abstractions`
- `Harbor.Plugins.Instantiation` (for PluginLifecycle helpers)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `PluginRegistrar` — dispatches `IPlugin.Initialize` + `Register*` methods into `IPluginLoadHost`
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
