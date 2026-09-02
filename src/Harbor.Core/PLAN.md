# Plan — Harbor.Core

## Status: Deprecated — frozen

`Harbor.Core` is a backward-compatibility shell (see README). It receives no
features and no fixes; its single source file is `FacadeMarker.cs`. Real work
happens in `Harbor.Application` (use cases) and `Harbor.Registries` (registry
implementations).

## Done

- [x] S1 split: all use cases extracted to `Harbor.Application`
      (`Agents/AgentLoop.cs:10`, `Sessions/CompactionService.cs`)
- [x] S1 split: all registry impls extracted to `Harbor.Registries`
      (`Events/InMemoryEventBus.cs`, `Providers/ProviderRegistry.cs`,
      `Tools/ToolRegistry.cs`, `Tools/InMemoryMcpRegistry.cs`,
      `Agents/AgentRegistry.cs`)
- [x] Facade assembly kept compiling via two transitive project references
      (`Harbor.Core.csproj`) + `FacadeMarker.Type` sentinel so reflection-based
      layer tests always load it
- [x] ROP-D Z1: types now declare namespaces matching their assembly of
      residence (`Harbor.Application.*`, `Harbor.Registries.*`) instead of the
      legacy `Harbor.Core.*`

## TODO

- [ ] Remove the facade entirely (v0.5 target, same horizon as the obsolete
      `CsPluginLoader` wrapper) once no consumer references `Harbor.Core`.

## Known issues

- Any consumer still referencing `Harbor.Core` silently pulls both halves
  (`Application` + `Registries`) even if it needs just one.
- The `[Obsolete]` signal is only carried in docs/csproj metadata — consider
  adding package deprecation metadata when packing.

## Next priorities

1. **P0**: Audit consumers (`apps/*`, samples) and switch them to direct
   project references.
2. **P2**: Delete this project after the audit lands.

## See also

- [README.md](README.md) — migration guide
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../Harbor.Application/PLAN.md](../Harbor.Application/PLAN.md) — active plan for the use-case half
- [../Harbor.Registries/PLAN.md](../Harbor.Registries/PLAN.md) — active plan for the registry half
