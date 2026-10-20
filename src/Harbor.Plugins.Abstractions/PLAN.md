# Plan — Harbor.Plugins.Abstractions

## Status: Stable

## Done

- [x] All five pipeline contracts (`IPluginSource`, `IPluginCompiler`, `IPluginInstantiator`, `IPluginRegistrar`, `IPluginLoadHost`)
- [x] Immutable data types (`PluginScript`, `CompiledPluginAssembly`, `LoadedPlugin`, `CompilationResult`)
- [x] XML docs on every public type
- [x] `IPluginLoadHost` doors report the OUTCOME of a registration (#1758):
      `Success` = registered and observable, `Failure` = not registered, with a
      reason. A host that does not carry a door MUST refuse it.

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
