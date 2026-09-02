# Harbor.Core

**DEPRECATED thin facade.** After the S1 split (and subsequent ROP-D namespace
fixes), `Harbor.Core` contains **no code of its own** — only a `FacadeMarker`
sentinel type. All former content moved into two focused assemblies; this
project exists purely so existing consumers that still reference `Harbor.Core`
keep compiling.

## What's in it

| File              | Purpose                                                                       |
|-------------------|-------------------------------------------------------------------------------|
| `FacadeMarker.cs` | `Harbor.Core.FacadeMarker` — assembly identity for reflection-based layer tests. No members with behavior. |

The `.csproj` carries two `<ProjectReference>` entries (`Harbor.Application`,
`Harbor.Registries`) which transitively re-export every former type.

## Where everything went

| Former type                              | Now lives in            | Namespace                    |
|------------------------------------------|-------------------------|------------------------------|
| `AgentLoop`, `DefaultAgent`, `AgentContext` | `src/Harbor.Application/Agents/` | `Harbor.Application.*`  |
| `CompactionService`, `SystemPromptBuilder`, `CachingSystemPromptBuilder`, `MessageConverter`, `TokenTracker`, `WorkspaceContextSource` | `src/Harbor.Application/Sessions/` | `Harbor.Application.*` |
| `PermissionService`                      | `src/Harbor.Application/Permissions/` | `Harbor.Application.*`   |
| `HarborConfig`, `ConfigStore`, `AuthStore`, `ProviderPresets` | `src/Harbor.Application/Configuration/` | `Harbor.Application.*` |
| `OnboardingWizard`                       | `src/Harbor.Application/Onboarding/` | `Harbor.Application.*`    |
| `RetryPolicyExtensions`                  | `src/Harbor.Application/Resilience/` | `Harbor.Application.*`     |
| `ProviderHealthCheck` (`IProviderHealthCheck`) | `src/Harbor.Application/Providers/` | `Harbor.Application.*`   |
| `AgentRegistry`, `ToolRegistry`, `ProviderRegistry` | `src/Harbor.Registries/`  | `Harbor.Abstractions.*` / `Harbor.Registries.*` |
| `InMemoryEventBus` (+ event middleware)  | `src/Harbor.Registries/Events/` | `Harbor.Abstractions.Events` / `Harbor.Registries.Events` |
| `InMemoryMcpRegistry`                    | `src/Harbor.Registries/Tools/`  | `Harbor.Registries.Tools`    |

> Namespaces moved to the assembly of residence (ROP-D Z1): use cases declare
> `Harbor.Application.*`, registries keep their original `Harbor.Abstractions.*`
> namespaces or use `Harbor.Registries.*`. Consumer `using` directives may need
> a one-time update when you switch from the facade.

## Migration

```csharp
// Before:
services.AddSingleton<AgentLoop>();
services.AddSingleton<ProviderRegistry>();

// After — reference the real projects:
//   <ProjectReference Include="..\..\src\Harbor.Application\..." />
//   <ProjectReference Include="..\..\src\Harbor.Registries\..." />
```

## Why keep the shell?

- Consumers (entry points, older plugins) still `<ProjectReference>` this
  assembly; removing it would be a hard break before v0.5.
- Reflection-based rules in `tests/Harbor.Architecture.Tests/` load assemblies
  by name — an empty-looking assembly still needs at least one type.

Do not add new types here — add them to `Harbor.Application` (use cases) or
`Harbor.Registries` (registry implementations).

## See also

- [../Harbor.Application/README.md](../Harbor.Application/README.md) — use cases (agent loop, compaction, prompts)
- [../Harbor.Registries/README.md](../Harbor.Registries/README.md) — registry implementations + event bus
- [../Harbor.Abstractions/README.md](../Harbor.Abstractions/README.md) — interface contracts
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md) — canonical layering matrix
