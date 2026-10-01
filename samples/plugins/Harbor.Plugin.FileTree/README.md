# Harbor.Plugin.FileTree

Sample plugin that adds a `tree` tool — renders a tree view of a directory. Demonstrates `IToolPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- (nothing else — tool dispatch arrives through `ToolContext`, so the plugin
  needs no Application-layer reference)

## Public API

- `FileTreePlugin` — implements `IToolPlugin`
- `TreeTool` — the `ITool` implementation (tool name `tree`)

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

Walks the directory tree depth-first (`depth`, default 3, max 10), skipping a hardcoded `IgnoredDirs` list (`node_modules`, `bin`, `obj`, `.git`, `dist`, …) and, unless `all=true`, dot-prefixed entries. There is no intermediate node type: entries are appended straight to a `StringBuilder` as ASCII art with `├──`/`└──`/`│   ` connectors, and file sizes go through a `FormatSize` helper. Returns the rendered string as a `ToolResult`.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
