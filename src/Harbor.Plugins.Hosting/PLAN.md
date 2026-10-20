# Plan — Harbor.Plugins.Hosting

## Status: Stable

## Done

- [x] Full pipeline orchestration
- [x] Fluent PluginHostBuilder
- [x] ContinueOnError option
- [x] Per-plugin error isolation
- [x] Plugin dependency graph — the first slice (#1733): a plugin declares what
      it needs, the host holds it back (loaded, not activated) until the
      dependency is met, and a dependency cycle is reported instead of waited on

## TODO

- [ ] Hot reload
- [ ] Plugin lifecycle management (init/shutdown ordering)
- [ ] Dependency graph on unload: a plugin whose dependency goes away is
      unloaded and comes back when it returns (the second slice of #1733)

## Known issues

- Plugins load once at startup — no hot reload yet.

## Next priorities

1. **P0**: Hot reload (FileSystemWatcher + PluginHost.ReloadAsync)
2. **P1**: Dependency graph on unload (#1733, the second slice)
3. **P2**: Plugin unloading
