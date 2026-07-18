// Harbor.Core — global usings for the thin facade.
//
// This project contains NO source code. It exists only to keep the legacy
// `Harbor.Core` assembly name alive for backward compatibility with consumers
// that still reference it. All types that used to live in this assembly have
// been split into two focused assemblies:
//
//   * Harbor.Application  — use cases (AgentLoop, DefaultAgent, CompactionService,
//                           SystemPromptBuilder, MessageConverter, PermissionService,
//                           OnboardingWizard, HarborConfig).
//   * Harbor.Registries   — registry implementations (AgentRegistry, ToolRegistry,
//                           InMemoryMcpRegistry, ProviderRegistry, InMemoryEventBus).
//
// Both new assemblies preserve the original `Harbor.Core.*` and
// `Harbor.Abstractions.*` namespaces, so existing `using Harbor.Core.Sessions;`
// directives resolve unchanged — the C# compiler searches all referenced
// assemblies for namespace contents, and Harbor.Core transitively references
// both Harbor.Application and Harbor.Registries via ProjectReference.
//
// The `using` directives below are intentionally retained as documentation of
// the namespaces this facade exposes. They are no-ops for this assembly (which
// has no code) but they make the public surface discoverable.

global using Harbor.Abstractions.Agents;
global using Harbor.Abstractions.Events;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Permissions;
global using Harbor.Abstractions.Providers;
global using Harbor.Abstractions.Sessions;
global using Harbor.Abstractions.Tools;
