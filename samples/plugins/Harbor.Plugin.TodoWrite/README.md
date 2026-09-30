# Harbor.Plugin.TodoWrite

Sample plugin that adds a `todo` tool — structured todo list management for the agent. Demonstrates `IToolPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- (nothing else — tool dispatch arrives through `ToolContext`, so the plugin
  needs no Application-layer reference)

## Public API

- `TodoWritePlugin` — implements `IToolPlugin`
- `TodoWriteTool` — the `ITool` implementation
- `TodoItem` — immutable record model
- `TodoStatus` — the item's status enum

## Usage

Place the compiled assembly (or the source `.cs` file) under `~/.harbor/plugins/` (or whatever `PluginRoot` is configured to). Harbor will discover and load it on startup.

Or, in code:

```csharp
var host = new PluginHostBuilder()
    .WithSource(new FileSystemPluginSource("/path/to/this/plugin", logger))
    .Build();
await host.LoadAllAsync(ct);
```

## How it works

The tool takes an `action` (`add` / `update` / `list` / `complete` / `clear`) plus that action's arguments, and keeps the list in a static `ConcurrentDictionary` keyed by `context.SessionId`, so items survive across tool calls within a session. No event is published — the tool returns a `ToolResult` and nothing subscribes to it. This sample ships **no TUI panel**: a plugin that wants one implements `ITuiPanelPlugin` and registers through `IPanelRegistry` (see `docs/PLUGIN_DEVELOPMENT.md`).

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
