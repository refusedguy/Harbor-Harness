# Harbor.Plugin.FileTree

Sample plugin that adds a `file_tree` tool — renders a tree view of a directory. Demonstrates `IToolPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- `Harbor.Core` (for tool dispatch integration)

## Public API

- `FileTreePlugin` — implements `IToolPlugin`
- `FileTreeTool` — the `ITool` implementation
- `TreeNode` — internal tree node record

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

Walks the directory tree (depth-limited, ignored paths filtered via `.gitignore`), builds a `TreeNode` forest, renders as ASCII art (with `├──`/`└──`/`│   ` connectors). Returns the rendered string as a `ToolResult`.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
