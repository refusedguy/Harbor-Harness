# Plan — Harbor.Plugins.Registration

## Status: Stable

## Done

- [x] Tool / Provider / Agent / Panel registration dispatch
- [x] SafePluginRegistrar (try/catch per plugin)
- [x] Panel adapter for ITuiPanelPlugin
- [x] A refused registration fails the plugin's registration (#1758). The
      registrar used to log the refusal and answer `Success`, so a caller
      could not tell "fully wired" from "half-wired"

## TODO

- [ ] Plugin dependency resolution (plugin A depends on plugin B)
- [ ] Plugin unregistration on unload
- [ ] Conflict resolution (two plugins register same tool name)

## Known issues

- No conflict resolution between two plugins claiming the same tool name: the
  second registration is refused (a `Result.Failure`), and since #1758 that
  refusal fails the second plugin's registration.

## Next priorities

1. **P0**: Warn on duplicate tool registration
2. **P1**: Plugin dependency graph
3. **P2**: Conflict resolution policy
