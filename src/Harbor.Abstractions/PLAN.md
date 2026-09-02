# Plan — Harbor.Abstractions

## Status: Stable

The Domain/facade layer is the foundation of Harbor. Every public API is XML-documented and stable across minor versions. Breaking changes require a major version bump. Models themselves now live in `Harbor.Abstractions.Contracts` — see the F1 decoupling notes in [README.md](README.md).

## Done

- [x] All Domain contracts: `IPlugin`, `ITool`, `ILlmClient`, `ISessionStore`, `IPermissionService`, `IAgent`
- [x] Immutable records for `Message`, `ToolCall`, `ToolResult`, `AgentEvent`, `SessionMetadata`, `TokenUsage` (in `Harbor.Abstractions.Contracts`)
- [x] `Result<T>` everywhere (via CSharpFunctionalExtensions)
- [x] Discriminated union pattern for `AgentEvent`
- [x] Identifier value objects: `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` (`Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs`)
- [x] `Permission` system (`Allow | Ask | Deny` per tool per glob)
- [x] Session contracts: `ICompactionService` / `CompactionResult` / `ISystemPromptBuilder` / `ITokenTracker` (`Sessions/`)
- [x] F1 decoupling: models extracted to `Harbor.Abstractions.Contracts` with zero Harbor project refs; facade references Contracts only
      (`Abstractions_ReferencesOnlyContracts` in `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs:218`)
- [x] ZLinq drop-in generator (replaces `System.Linq`)
- [x] MemoryPack serialization for events
- [x] 100% safe code (`AllowUnsafeBlocks=false`)
- [x] Full XML docs on every public API

## TODO

- [ ] Formalize `IPluginManifest` schema (semver, dependencies, capabilities)
- [ ] Add `IMcpServer` contract for MCP (Model Context Protocol) support
- [ ] Add `IVisionContent` for multimodal message content
- [ ] Add `ITelemetrySink` for opt-in usage telemetry
- [ ] Consider `IAsyncDisposable` on `ILlmClient` for clean HTTP connection teardown
- [ ] Add `StreamingResponse` abstraction to replace raw `IAsyncEnumerable<AgentEvent>` on `ILlmClient`

## Known issues

- No formal capability advertisement — provider capabilities aren't enforced (a provider can claim to support vision but actually not).
- `AgentEvent` discriminated union is C#-idiomatic but verbose — a future C# language feature (union types) would simplify this.
- Event serialization couples the contract types to a specific serializer ([MemoryPackable]); serializer remains swappable at the storage layer, but there is no first-class `IEventSerializer` seam in the contracts.

## Next priorities

1. **P0**: `IPluginManifest` with semver + dependency declaration (unblocks plugin versioning)
2. **P1**: `IVisionContent` for multimodal messages
3. **P1**: `IMcpServer` contract for MCP support
4. **P2**: `ITelemetrySink` for opt-in usage stats

## Stability promise

The Domain layer is the most stable part of Harbor. Breaking changes here break every consumer. We commit to:

- No breaking changes within a major version.
- Deprecation via `[Obsolete]` for at least one minor version before removal.

> Note: `docs/MIGRATION.md` referenced here historically no longer exists; migration guidance for the v0.x splits lives in project READMEs (`Harbor.Core`, `Harbor.Plugins.Runtime`) and [`docs/ROADMAP.md`](../../docs/ROADMAP.md).
