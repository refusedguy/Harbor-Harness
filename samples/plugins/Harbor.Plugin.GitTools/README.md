# Harbor.Plugin.GitTools

Sample plugin that adds a `git` tool — one tool that takes a git subcommand as an argument. Demonstrates `IToolPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- (nothing else — tool dispatch arrives through `ToolContext`, so the plugin
  needs no Application-layer reference)

## Public API

- `GitToolsPlugin` — implements `IToolPlugin`
- `GitTool` — the `ITool` implementation (tool name `git`)

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

Takes an `args` string (the subcommand and its flags) plus an optional `cwd`, and shells out to `git` with it via `Process.Start`, streaming stdout and stderr into `StringBuilder`s. Output is returned as a `ToolResult` verbatim — nothing is parsed per subcommand. `ValidateArguments` is where the safety lives: it rejects `push --force` unless `--force-with-lease` is also present, and rejects `reset --hard` outright. No native libgit2 dependency — requires `git` on PATH.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
