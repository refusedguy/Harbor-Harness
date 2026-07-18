# Plan — Harbor.Plugins.Instantiation

## Status: Stable

## Done

- [x] Reflection-based activation
- [x] Parameterless ctor requirement
- [x] PluginLifecycle helpers (Initialize, ShutdownAsync)

## TODO

- [ ] DI-aware instantiation (constructor injection)
- [ ] AssemblyLoadContext isolation (sandboxing)
- [ ] Plugin unloading (collectible ALC)

## Known issues

- No DI injection — plugins must have parameterless ctors.

## Next priorities

1. **P1**: DI-aware instantiation via Microsoft.Extensions.DependencyInjection
2. **P2**: AssemblyLoadContext sandboxing
3. **P2**: Plugin unloading
