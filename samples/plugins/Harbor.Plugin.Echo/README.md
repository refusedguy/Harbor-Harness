# Harbor.Plugin.Echo

A minimal, dependency-free sample plugin for the plugin showcase (#1434). It adds an `echo` tool using only the existing tool axis, with no new extension axis (#555 freeze).

## Layer

Sample plugin implementing `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. The `.cs` drop-in is compiled by Harbor at startup via Roslyn; the optional `.csproj` builds the same source as a DLL package.

## Dependencies

- `Harbor.Abstractions` (for `IPlugin`, `IToolPlugin`, and `ITool`).
- No other dependencies. There is no UI panel, no persistence, and no Application-layer reference.

## Public API

- `EchoPlugin` implements `IToolPlugin`.
- `EchoTool` implements `ITool` (tool name `echo`).

## Usage

One-minute install (CS-source drop-in, compiled at startup via Roslyn):

```bash
bash samples/plugins/Harbor.Plugin.Echo/install.sh
```

Restart Harbor, then ask the agent to call `echo` with `{"text": "hello"}`. The tool returns `Echo: hello`. Pass `{"text": "hello", "upper": true}` to get `Echo: HELLO`.

The installer copies `EchoPlugin.cs` into `~/.harbor/plugins/` (override with `HARBOR_PLUGINS_DIR`). Place the compiled assembly (or the source `.cs` file) under `~/.harbor/plugins/` and Harbor discovers and loads it on startup.

## How it works

The plugin registers one tool through `IToolPlugin.RegisterTools`. The tool declares a JSON schema with a required `text` string and an optional `upper` boolean, validates the arguments, and returns a `ToolResult` echoing the text. It uses only the tool axis, so nothing new is needed in `PluginAxisRegistry`.

## See also

- [PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md) covers plugin development, including the one-minute install section.
- [PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md) is the full plugin system reference.
- [ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md) describes the layering rules.
