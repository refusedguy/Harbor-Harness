# Plugin showcase slice-1: TodoWrite done right

Persistent cross-session todos with search, plus a read-only TUI panel.
Built on the existing axes only: tools (`todo`) + `ITuiPanelPlugin`
(`todo-list` panel). No new axes per the plugin-axis freeze.

## What it shows

- Storage: the plugin persists every mutation to `<DataDirectory>/todos.json`
  (best-effort write-through; a corrupt or missing file degrades to
  in-memory state and never breaks startup or tool results).
- Panel: `TodoListPanel` renders text rows (totals + up to 10 recent items,
  clipped to the dock geometry). Side-effect free; `OnKey` returns false.
- Search: `action=search` with `query` matches todo content across sessions
  (case-insensitive substring, capped at 50 hits). `list` stays per-session.

## Install (one command)

```bash
bash samples/plugins/Harbor.Plugin.TodoWrite/install.sh
```

This copies the single-file plugin to `~/.harbor/plugins/todowrite.cs`
(or `$HARBOR_PLUGINS_DIR`). Restart Harbor; Roslyn compiles it at startup.
Set `HARBOR_PLUGINS_DIR` to override the destination.

## 60-second demo

```bash
bash samples/plugins/Harbor.Plugin.TodoWrite/demo.sh
```

The script walks install, add, list, cross-session search, and the panel
registration check. It is hermetic (no dotnet, no network) and finishes
well under 60 seconds.

## Tool schema

Tool name `todo`, sequential execution, opaque safety profile.
Actions: `add`, `update`, `list`, `complete`, `clear`, `search`.
`search` requires `query`; `add` requires `content`; `update`/`complete`
require `id`.

## Panel contract

`TodoWritePlugin` implements `IToolPlugin` and `ITuiPanelPlugin`, registering
`TodoListPanel` (`todo-list`, Right dock, size 30). The panel reads a snapshot
of the store and returns `IReadOnlyList<string>` rows only.

## Sources

- [TodoWritePlugin.cs](../samples/plugins/Harbor.Plugin.TodoWrite/TodoWritePlugin.cs)
- [install.sh](../samples/plugins/Harbor.Plugin.TodoWrite/install.sh)
- [demo.sh](../samples/plugins/Harbor.Plugin.TodoWrite/demo.sh)
- [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md)
- [PLUGIN_SYSTEM.md](./PLUGIN_SYSTEM.md)
