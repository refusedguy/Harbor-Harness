# Plan - Harbor.Registries

## Status: MVP

Thread-safe registry implementations extracted from `Harbor.Core` as part of the Clean Architecture refactor. Each registry implements an interface from `Harbor.Abstractions`.

## Done

- [x] `AgentRegistry` - thread-safe agent-definition registry (NonBlocking.ConcurrentDictionary)
- [x] `AgentRegistryBuilder` - fluent registration helper
- [x] `ToolRegistry` - tool registry with FrozenDictionary fast-lookup snapshot
- [x] `ToolRegistryBuilder` - fluent registration helper
- [x] `InMemoryMcpRegistry` - in-memory MCP server registry
- [x] Lock-free scaling for hot paths

## TODO

- [ ] Migrate `ProviderRegistry` from Harbor.Core
- [ ] Migrate `EventBus` (InMemoryEventBus) from Harbor.Core
- [ ] Add `IMcpRegistry` real implementation (stdio JSON-RPC client)
- [ ] Hot-reload support (swap FrozenDictionary when tools added at runtime via plugins)
- [ ] Concurrent registry stress tests (1M lookups, 10K writers)

## Known issues

- `InMemoryMcpRegistry` cannot actually invoke MCP servers - production hosts must swap in a real client.
- Registry mutations require a re-freeze; for plugin-loaded tools, this happens once at startup. Hot-reload not yet wired.

## Next priorities

1. **P0**: Migrate `ProviderRegistry` from Harbor.Core
2. **P0**: Migrate `InMemoryEventBus` from Harbor.Core
3. **P1**: Real MCP registry (stdio JSON-RPC client)
4. **P1**: Hot-reload (re-freeze FrozenDictionary on plugin load)
5. **P2**: Concurrent stress tests

## See also

- [README.md](README.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../Harbor.Core/README.md](../Harbor.Core/README.md) - predecessor (still exists for backward compat)
