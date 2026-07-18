# Plan — Harbor.Plugins.Abstractions

## Status: Stable

## Done

- [x] All five pipeline contracts
- [x] Immutable data types
- [x] XML docs on every public type

## TODO

- [ ] Network plugin source contract (git URL)
- [ ] Plugin versioning contract (semver)
- [ ] Plugin sandboxing contract (AssemblyLoadContext boundary)

## Known issues

- No versioning contract yet — plugins are identified by type name only.

## Next priorities

1. **P0**: Add IPluginVersion contract
2. **P1**: Add INetworkPluginSource contract
3. **P2**: Sandbox boundary contract
