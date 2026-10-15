# Harbor.Plugin.TodoWrite

Sample plugin that adds a `todo` tool — structured todo list management for the agent. Demonstrates `IToolPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- (nothing else — tool dispatch arrives through `ToolContext`, so the plugin
  needs no Application-layer reference)

## Public API

- `TodoWritePlugin` — implements `IToolPlugin` + `ITuiPanelPlugin`
- `TodoWriteTool` — the `ITool` implementation (`todo`)
- `TodoListPanel` — the `IPanelProvider` implementation (`todo-list`)
- `TodoItem` — immutable record model
- `TodoStatus` — the item's status enum
- `PersistedTodo` — JSON persistence row model

## Usage

One-command install (CS-source drop-in, compiled at startup via Roslyn):

```bash
bash samples/plugins/Harbor.Plugin.TodoWrite/install.sh
```

60-second demo (hermetic, no dotnet or network):

```bash
bash samples/plugins/Harbor.Plugin.TodoWrite/demo.sh
```

See [../../../docs/PLUGIN_SHOWCASE_TODOWRITE.md](../../../docs/PLUGIN_SHOWCASE_TODOWRITE.md)
for the full showcase walkthrough.

Place the compiled assembly (or the source `.cs` file) under `~/.harbor/plugins/` (or whatever `PluginRoot` is configured to). Harbor will discover and load it on startup.

Or, in code:

```csharp
var host = new PluginHostBuilder()
    .WithSource(new FileSystemPluginSource("/path/to/this/plugin", logger))
    .Build();
await host.LoadAllAsync(ct);
```

## How it works

The tool takes an `action` (`add` / `update` / `list` / `complete` / `clear` / `search`) plus that action's arguments. `list` is per-session; `search` matches `query` across sessions. Every mutation is written through to `<DataDirectory>/todos.json`, so items survive Harbor restarts; a missing or corrupt file degrades to in-memory state. The plugin also implements `ITuiPanelPlugin`, registering a read-only `todo-list` panel (text rows only) through `IPanelRegistry` (see `docs/PLUGIN_DEVELOPMENT.md`). No new axes: tools + panel only.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
