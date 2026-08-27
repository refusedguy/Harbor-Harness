# Plan - Harbor.Registries

## Status: Stable

Thread-safe registry implementations extracted from `Harbor.Core` in the S1
split. Each registry implements an interface from `Harbor.Abstractions`.

## Done

- [x] `AgentRegistry` - thread-safe agent-definition registry (NonBlocking.ConcurrentDictionary)
- [x] `AgentRegistryBuilder` - fluent registration helper
- [x] `ToolRegistry` - tool registry with FrozenDictionary fast-lookup snapshot
- [x] `ToolRegistryBuilder` - fluent registration helper
- [x] `ProviderRegistry` migrated from Harbor.Core (`Providers/ProviderRegistry.cs`)
- [x] `InMemoryEventBus` migrated from Harbor.Core (`Events/InMemoryEventBus.cs`)
      with bounded scrollback + lock-free snapshot reads
- [x] `CompositeToolRegistry` — fan-in over multiple tool sources without re-freeze
      (`Tools/CompositeToolRegistry.cs:4`)
- [x] Bus middleware: `SamplingMiddleware`, `TypeFilterMiddleware` (`Events/`)
- [x] `InMemoryMcpRegistry` - in-memory MCP server registry
- [x] Lock-free scaling for hot paths

## TODO

- [ ] Add `IMcpRegistry` real implementation (stdio JSON-RPC client)
- [ ] Hot-reload support (swap FrozenDictionary when tools added at runtime via plugins)
- [ ] Concurrent registry stress tests (1M lookups, 10K writers)

## Known issues

- `ToolRegistry` maintains a dual read path (frozen snapshot vs concurrent
  fallback) that duplicates logic across `GetAllTools` / `ResolveTools` /
  `GetTool`; the fix is a composite delegating to a single `IToolSource` —
  flagged in code as `TODO(principles)[OCP, ROP]`
  (`Tools/ToolRegistry.cs:25`, см. аудит §OOP-005).
- `InMemoryMcpRegistry` cannot actually invoke MCP servers - production hosts must swap in a real client.
- Registry mutations require a re-freeze; for plugin-loaded tools, this happens once at startup. Hot-reload not yet wired.

## Next priorities

1. **P1**: Real MCP registry (stdio JSON-RPC client)
2. **P1**: Hot-reload (re-freeze FrozenDictionary on plugin load)
3. **P2**: Resolve the ToolRegistry frozen/concurrent duplication (§OOP-005)
4. **P2**: Concurrent stress tests

## See also

- [README.md](README.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../Harbor.Application/README.md](../Harbor.Application/README.md) - use cases consuming these registries
