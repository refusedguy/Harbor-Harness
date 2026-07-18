# Harbor.Domain — PLAN

## Status

**Stable.** Extracted from `Harbor.Abstractions` in round 6 (Task A1) as the pure-domain leaf of the dependency pyramid. Namespaces preserved — zero consumer code changes.

## Done

- [x] Split `Harbor.Abstractions` god-project: domain models / events / permissions / identifiers moved to `Harbor.Domain`
- [x] Pure-domain dependency invariant: only `CSharpFunctionalExtensions` + `MemoryPack` (no `Microsoft.Extensions.*`, no `ZLinq`, no `Harbor.*` project refs)
- [x] Namespaces preserved (`Harbor.Abstractions.Models`, `.Events`, `.Permissions`, `.Models.Identifiers`) — consumers' `using` lines unchanged
- [x] Architecture test `Domain_HasZeroHarborProjectReferences` added in `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs`
- [x] All `[MemoryPackable]` types still compile (MemoryPack source generator runs on `Harbor.Domain.dll`)

## TODO

### P1 — deeper domain extraction

- [ ] Move `AgentDefinition` (currently in `Harbor.Abstractions/Agents/AgentDefinition.cs`) into `Harbor.Domain/Agents/` — it's a pure domain model (`sealed record AgentDefinition(AgentName, ...)`) but currently lives in the interfaces project. Trivial move once we decide whether `IAgent`, `IAgentLoop`, `IAgentRunner`, `AgentState` (which ARE interfaces) stay in `Harbor.Abstractions`.
- [ ] Move `PluginContext` (currently in `Harbor.Abstractions/Plugins/IPlugin.cs`) — it's a domain-style value object but currently bundled with the `IPlugin` interface. Splitting it would let `Harbor.Domain` carry the plugin context data and `Harbor.Abstractions` carry just the interface.
- [ ] Move `ToolContext`, `ToolProgressUpdate`, `ToolDescriptor`, `ExecutionMode` (currently in `Harbor.Abstractions/Tools/ITool.cs`) — same pattern: data types that should be in Domain, interfaces that stay in Abstractions.
- [ ] Move `LlmRequest`, `LlmMessage`, `LlmUserMessage`, `LlmAssistantMessage`, `LlmToolResultMessage`, `LlmContentBlock`, `LlmTextBlock`, `LlmImageBlock`, `LlmToolCallBlock`, `LlmToolResultBlock`, `LlmThinkingBlock`, `ToolDefinition` (currently in `Harbor.Abstractions/Providers/ILlmClient.cs`) — same pattern.
- [ ] Move `CompactionResult`, `ITokenEstimator`, `HeuristicTokenEstimator`, `ISessionContext`, `SystemPromptContext`, `ContextFile`, `SkillDescriptor` (currently in `Harbor.Abstractions/Sessions/`) — same pattern. `HeuristicTokenEstimator` is a concrete class with logic; moving it to Domain lets pure-domain consumers (e.g. compaction tests) skip the Abstractions reference.

### P2 — domain events polish

- [ ] Consider renaming `Harbor.Abstractions.Events` → `Harbor.Domain.Events` namespace. Currently preserved for backward compat — would require consumer-side `using` changes. Defer until a major version bump.
- [ ] Add a `DomainEvent` base class distinct from `AgentEvent` (currently `AgentEvent` conflates "agent-loop-internal event" with "domain-level event"). This would let pure-domain subscribers (e.g. audit log) filter on domain events only.
- [ ] `LlmEvent` hierarchy could move to its own file (`Events/LlmEvent.cs`) — currently bundled with `AgentEvent` in one 200-line file.

### P2 — MemoryPack formatter ergonomics

- [ ] `JsonElementMemoryPackFormatter` currently round-trips via UTF-16 string. A UTF-8 path (`MemoryPackWriter.WriteUtf8Bytes`) would halve payload size for JSON-heavy tool calls (e.g. `bash` with long argument objects). Requires careful handling of `JsonElement.GetRawText`'s UTF-16 contract.
- [ ] Register `JsonElementMemoryPackFormatter` globally at assembly-load time rather than via the `ToolCallPart.StaticConstructor` hook. The current lazy registration works but makes the first `ToolCallPart` (de)serialization slower than subsequent ones — a benchmark would surface this.

## Known issues

- **`PermissionRuleset.Default` is hard-coded.** The 27-rule "safe defaults" ruleset is baked into the source. A config-driven default (loaded from `permissions/default.json`) would let hosts ship custom defaults without recompiling. Tracked in `docs/CODE_PRINCIPLES_AUDIT.md` §P-007.
- **`IdentifierValidation.IsValidProviderId` rejects uppercase.** Provider ids are normalized via `ToLowerInvariant()` before validation, so this is intentional — but it means `ProviderId.Create("Anthropic")` works while `ProviderId.Create("Anthropic-Claude")` would also work (the dash is allowed). Document the normalization contract in the XML doc.
- **`Session.Create` derives `ProjectId` from `Directory.GetHashCode()`** — `String.GetHashCode` is not deterministic across process restarts (different per-appdomain in .NET Core 5+). If sessions are persisted by `ProjectId` and a restart picks up a different hash, the project grouping breaks. Use a stable hash (e.g. SHA-256 of the absolute path, hex-encoded) before this ships in v1.

## Next priorities

1. **P1 — Move `AgentDefinition` + `ToolContext` + `LlmRequest` + `CompactionResult` to Domain** (half-day, mechanical split, no behavior change). This completes the "Domain = all data + in-process rules; Abstractions = all interfaces" goal of the A1 split.
2. **P2 — Stable `ProjectId` hash** (1 hour, fixes a latent durability bug).
3. **P2 — UTF-8 MemoryPack path for `JsonElement`** (2 hours, benchmark first to confirm it's worth the complexity).
