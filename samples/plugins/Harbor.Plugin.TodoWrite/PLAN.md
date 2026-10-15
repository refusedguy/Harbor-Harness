# Plan — Harbor.Plugin.TodoWrite

## Status: Showcase slice-1 (#1434)

Showcase plugin — demonstrates `IToolPlugin` + `ITuiPanelPlugin` (tools + panel only, no new axes).

## Done

- [x] Implements `IToolPlugin` + `ITuiPanelPlugin`
- [x] Plugin manifest (name, version)
- [x] Initialize / Shutdown lifecycle
- [x] Works with the default plugin pipeline (`PluginHostBuilder`)
- [x] Persistent todos (survive agent restart, `<DataDirectory>/todos.json`)
- [x] Cross-session search (`action=search`)
- [x] Read-only `todo-list` panel (text rows)
- [x] One-command install (`install.sh`) + 60-second demo (`demo.sh`)
- [x] Showcase doc page (`docs/PLUGIN_SHOWCASE_TODOWRITE.md`)

## TODO

- [ ] Add unit tests
- [ ] Sub-task nesting
- [ ] Due dates + reminders

## Known issues

- Persistence is best-effort JSON; a corrupt file resets to empty in-memory state.
- No sub-task nesting yet.
