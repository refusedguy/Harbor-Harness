# Plan — Harbor.Plugins.Hosting

## Status: Stable

## Done

- [x] Full pipeline orchestration
- [x] Fluent PluginHostBuilder
- [x] ContinueOnError option
- [x] Per-plugin error isolation

## TODO

- [ ] Hot reload
- [ ] Plugin lifecycle management (init/shutdown ordering)
- [ ] Plugin dependency graph

## Known issues

- Plugins load once at startup — no hot reload yet.

## Next priorities

1. **P0**: Hot reload (FileSystemWatcher + PluginHost.ReloadAsync)
2. **P1**: Plugin dependency ordering
3. **P2**: Plugin unloading
