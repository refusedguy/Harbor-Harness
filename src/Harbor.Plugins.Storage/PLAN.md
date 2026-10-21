# Plan — Harbor.Plugins.Storage

## Status: Stable

## Done

- [x] FileSystem source (recursive directory walk)
- [x] InMemory source
- [x] Composite source (fan-in)
- [x] EmbeddedResource source
- [x] Plugin manifest parsing (`<name>.harbor-plugin.json`, next to the source) — #1739

## TODO

- [ ] Network source (git URL)
- [ ] Hot reload (FileSystemWatcher)

## Known issues

- No hot reload — plugins are loaded once at startup.

## Next priorities

1. **P1**: Network source (clone git repo, scan for .cs)
2. **P2**: FileSystemWatcher hot reload
