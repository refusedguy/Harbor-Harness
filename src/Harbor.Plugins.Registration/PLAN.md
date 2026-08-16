# Plan — Harbor.Plugins.Registration

## Status: Stable

## Done

- [x] Tool / Provider / Agent / Panel registration dispatch
- [x] SafePluginRegistrar (try/catch per plugin)
- [x] Panel adapter for ITuiPanelPlugin

## TODO

- [ ] Plugin dependency resolution (plugin A depends on plugin B)
- [ ] Plugin unregistration on unload
- [ ] Conflict resolution (two plugins register same tool name)

## Known issues

- No conflict resolution — last-writer-wins for duplicate tool names.

## Next priorities

1. **P0**: Warn on duplicate tool registration
2. **P1**: Plugin dependency graph
3. **P2**: Conflict resolution policy
