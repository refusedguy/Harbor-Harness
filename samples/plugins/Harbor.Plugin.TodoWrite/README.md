# Harbor.Plugin.TodoWrite

Sample plugin that adds a `todo_write` tool — structured todo list management for the agent. Demonstrates `IToolPlugin` + `ITuiPlugin` (registers a TUI panel that shows the todo list).

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- `Harbor.Core` (for tool dispatch integration)
- `Harbor.Tui.Abstractions` (for the TUI panel)

## Public API

- `TodoWritePlugin` — implements `IToolPlugin`
- `TodoWriteTool` — the `ITool` implementation
- `TodoPanelPlugin` — the `ITuiPanelPlugin` that renders the list in the TUI
- `TodoList` — immutable record model

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

Tool takes a JSON array of `{content, status, priority}` items, validates, replaces the current todo list (stored in `IPluginLoadHost` shared state), emits an `AgentEvent` so subscribers (e.g. the TUI panel) refresh.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
