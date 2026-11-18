# Harbor.Plugin.Pomodoro

A dependency-free sample plugin for the plugin showcase (#1434). It adds a `pomodoro` focus-timer tool using only the existing tool axis, with no new extension axis (#555 freeze). The plugin runs out-of-proc: the separate `harbor-plugins-host` process compiles the `.cs` drop-in and serves the tool over MCP stdio, so the AOT CLI never loads Roslyn.

## Layer

Sample plugin implementing `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. The `.cs` drop-in is compiled by the out-of-process plugin host; the optional `.csproj` builds the same source as a DLL package. No UI panel, no persistence layer, and no Application-layer reference.

## Dependencies

- `Harbor.Abstractions` (for `IPlugin`, `IToolPlugin`, and `ITool`).
- No other runtime dependencies. Timer state is one JSON file under `~/.harbor/`.

## Public API

- `PomodoroPlugin` implements `IToolPlugin`.
- `PomodoroTool` implements `ITool` (tool name `pomodoro`).

## Usage

One-minute install (out-of-proc CS-source drop-in):

```bash
bash samples/plugins/Harbor.Plugin.Pomodoro/install.sh
```

Restart Harbor. The `pomodoro` tool appears in `/plugins`. The installer copies `PomodoroPlugin.cs` to `~/.harbor/plugins/pomodoro.cs` (override the directory with `HARBOR_PLUGINS_DIR`).

Then ask the agent (or call the tool directly) with these arguments:

```json
{"action": "start", "minutes": 25, "label": "deep work"}
```

```json
{"action": "status"}
```

```json
{"action": "stop"}
```

`start` writes `~/.harbor/pomodoro.json` with the start and end timestamps. `status` reports the remaining minutes. `stop` (or `reset`) clears the file and reports the elapsed time. Set `HARBOR_POMODORO_STATE` to move the state file.

## How it works

The plugin registers one tool through `IToolPlugin.RegisterTools`. The tool declares a JSON schema with a required `action` (`start`, `status`, `stop`, `reset`) and optional `minutes` (1-480, default 25) and `label`, validates the arguments, and returns a text result plus structured metadata. It uses only the tool axis, which is the one axis the out-of-process route carries, so nothing new is needed in `PluginAxisRegistry`.

## See also

- [PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md) covers plugin development, including the one-minute install section.
- [PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md) is the full plugin system reference.
- [Harbor.Plugins.Host README](../../../src/Harbor.Plugins.Host/README.md) describes the out-of-process host and which axes it carries.
