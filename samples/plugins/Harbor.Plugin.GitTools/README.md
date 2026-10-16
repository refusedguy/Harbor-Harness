# Harbor.Plugin.GitTools

Sample plugin that adds a `git` tool — one tool that takes a git subcommand as an argument — plus a structured `git_status` tool and a `git-status` TUI panel. Demonstrates `IToolPlugin` + `ITuiPanelPlugin`.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`, `ITuiPanelPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin` / `IGitQuery`)
- `Harbor.Ui.Framework.State` (for `ITuiPanelPlugin` / `IPanelProvider`)
- (nothing else — tool dispatch arrives through `ToolContext`, so the plugin
  needs no Application-layer reference)

## Public API

- `GitToolsPlugin` — implements `IToolPlugin` + `ITuiPanelPlugin`
- `GitTool` — the `ITool` implementation (tool name `git`, free-form args)
- `GitStatusTool` — the `ITool` implementation (tool name `git_status`, actions `status` / `branch` / `pr`)
- `GitStatusPanel` — the `IPanelProvider` implementation (`git-status`)
- `GitPrCache` — last-known PR summary shared by the `pr` action and the panel

## Usage

One-command install (CS-source drop-in, compiled at startup via Roslyn):

```bash
bash samples/plugins/Harbor.Plugin.GitTools/install.sh
```

60-second demo (hermetic, no dotnet or network):

```bash
bash samples/plugins/Harbor.Plugin.GitTools/demo.sh
```

See [../../../docs/PLUGIN_SHOWCASE_GITTOOLS.md](../../../docs/PLUGIN_SHOWCASE_GITTOOLS.md)
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

The `git` tool takes an `args` string (the subcommand and its flags) plus an optional `cwd`, and shells out to `git` with it via `Process.Start`, streaming stdout and stderr into `StringBuilder`s. Output is returned as a `ToolResult` verbatim — nothing is parsed per subcommand. `ValidateArguments` is where the safety lives: it rejects `push --force` unless `--force-with-lease` is also present, and rejects `reset --hard` outright. No native libgit2 dependency — requires `git` on PATH.

The `git_status` tool is the structured, read-only counterpart: `status` reports branch + dirty count + last commit, `branch` reports the name only, `pr` runs `gh pr status --json` best-effort (missing `gh`, non-repo, or no PR all degrade to a plain message and never fail the call). Every `pr` run refreshes `GitPrCache`, which the panel reads.

The plugin also implements `ITuiPanelPlugin`, registering a read-only `git-status` panel (text rows only) through `IPanelRegistry` (see `docs/PLUGIN_DEVELOPMENT.md`). The panel never forks a process per frame: branch facts come from `PanelServices.Git` (the daemon's `IGitQuery` port — daemon data over IPC in the two-process layout), and the PR line comes from `GitPrCache`. No new axes: tools + panel only.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
