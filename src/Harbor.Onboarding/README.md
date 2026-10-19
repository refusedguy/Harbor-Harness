# Harbor.Onboarding

## Overview

The first-run experience as a one-way leaf: the interactive onboarding wizard
that walks a new user through provider → API key → model → agent → saved
config, and the setup-checklist detector that reports which setup steps are
still outstanding. Sits in the **Application** layer as a leaf over
`Harbor.Application` — it binds the configuration family (AuthStore, config
store, provider presets) but nothing references it back except the
composition root (extraction #1614, from `Harbor.Application/Configuration/`).

## Public API

- `OnboardingWizard` — `RunAsync(Func<string, Task<string>> reader, Action<string> writer, CancellationToken ct)` drives the whole interactive flow over injected `IConfigStore` + `AuthStore`; optional `IProviderHealthCheck` / `IProviderRegistry` / `IAgentRegistry` upgrade the model prompt from static presets to a live, health-checked list (capped at `MaxListedModels = 15`). Written for pipe-style I/O (`Func<string, Task<string>>` reader, `Action<string>` writer) so it is renderer-agnostic.
- `SetupChecklistDetector` — `DetectAsync(CancellationToken)` returns `SetupChecklistSignals` (one bool per setup task: config present, provider key stored, provider health passed, …). Existence-tolerant by contract: a missing config, an unreadable store or a failing probe yield "not done" signals, never a throw — setup detection runs on the render-critical startup path and must not be able to fail the REPL.
- `OnboardingPicks` is `internal` (numbered-pick + model-id normalization helpers shared by the wizard's prompts, #1577).

## Wiring

Registered by the composition root: `Harbor.Hosting/Modules/CoreModule.cs` adds `OnboardingWizard` as a singleton. `SetupChecklistDetector` is constructed by the REPL hosts (`apps/Harbor.App.Cli` `SetupChecklistController` / `CellForgeOnboardingController`), which map `SetupChecklistSignals` onto the UI-framework `SetupChecklistModel` (`Harbor.Ui.Framework.Projection`). No env var, no feature flag.

## Usage

```csharp
using Harbor.Application.Configuration; // namespace kept from the verbatim move

var wizard = new OnboardingWizard(configStore, new AuthStore(configStore));
Result outcome = await wizard.RunAsync(
    prompt => Console.In.ReadLineAsync()!,   // reader
    line => Console.WriteLine(line),         // writer
    ct);
```

## Dependencies

- `Harbor.Application` — the one outgoing edge: `AuthStore`, `IConfigStore`, `JsonConfigStore`, `ProviderPresets`, `HarborConfig` stay with the configuration family there (the issue's "→Configuration" arrow).
- `Harbor.Abstractions` (+ transitively `Harbor.Abstractions.Contracts`) — `IProviderHealthCheck`, `IProviderRegistry`, `IAgentRegistry`, `AgentDefinition`, `ProviderId`.
- Packages: `CSharpFunctionalExtensions` (Result railway), `Microsoft.Extensions.Logging.Abstractions` ([LoggerMessage] source-gen).
- Referenced by: `Harbor.Hosting`, `apps/Harbor.App.Cli`, `tests/Harbor.Config.Tests`, `tests/Harbor.App.Cli.Tests` — pinned by `tests/Harbor.Architecture.Tests` (FullLayerMatrixTests row, Layer.Application; Harbor.Application itself does NOT reference this leaf).

## Known limitations

- The namespace stays `Harbor.Application.Configuration` (verbatim slice-1 move, the same convention as #1613 Hooks), so consumers keep their `using`s — do not "fix" it to `Harbor.Onboarding` without migrating every consumer and test projection at once.
- Deliberately no ZLinq drop-in (Application's zero-alloc LINQ): the single LINQ call site compiles against the SDK's implicit `System.Linq`. Performance-drift rules must not demand ZLinq here without measuring.
- No TUI of its own: the wizard is renderer-agnostic pipe I/O; the CellForge `OnboardingFlow` keeps its own copy of the pick logic until a seam visible outside `Harbor.Application` exists (#1577 records the follow-up).
- `OnboardingPicks` is internal to this assembly — external pickers (desktop `OnboardingViewModel`) must not bind it.

## See also

- [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) §2 — the layer matrix row.
- [`src/Harbor.Hooks/README.md`](../Harbor.Hooks/README.md) — the sibling one-way-leaf extraction whose conventions this follows.
- [`docs/CONFIGURATION.md`](../../docs/CONFIGURATION.md) — the config file the wizard writes.
