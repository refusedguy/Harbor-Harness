# Harbor.Plugin.GitTools

Sample plugin that adds git tools: `git_status`, `git_diff`, `git_log`. Demonstrates `IToolPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- `Harbor.Core` (for tool dispatch integration)

## Public API

- `GitToolsPlugin` — implements `IToolPlugin`
- `GitStatusTool`, `GitDiffTool`, `GitLogTool` — the `ITool` implementations

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

Shells out to `git` (via `Process.Start`) and returns stdout as a `ToolResult`. Each tool wraps a specific git subcommand. No native libgit2 dependency — requires `git` on PATH.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
