# Plan — Harbor.Abstractions

## Status: Stable

The Domain layer is the foundation of Harbor. Every public API is XML-documented and stable across minor versions. Breaking changes require a major version bump.

## Done

- [x] All Domain contracts: `IPlugin`, `ITool`, `ILlmClient`, `ISessionStore`, `IPermissionService`, `IAgent`
- [x] Immutable records for `Message`, `ToolCall`, `ToolResult`, `AgentEvent`, `SessionMetadata`, `TokenUsage`
- [x] `Result<T>` everywhere (via CSharpFunctionalExtensions)
- [x] Discriminated union pattern for `AgentEvent`
- [x] `SessionId`, `AgentId` value objects
- [x] `Permission` system (`Allow | Ask | Deny` per tool per glob)
- [x] `CompactionSummary` for anchored-summary compaction
- [x] `BuiltinTools` constants (read, write, edit, bash, glob, grep, ls, task)
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

- No formal capability advertisement — `ProviderCapabilities` exists but isn't enforced (a provider can claim to support vision but actually not).
- `AgentEvent` discriminated union is C#-idiomatic but verbose — a future C# language feature (union types) would simplify this.
- `MemoryPack` is required for event serialization — couples Domain to a specific serializer. Mitigation: serializer is replaceable behind an `IEventSerializer` interface (TODO).

## Next priorities

1. **P0**: `IPluginManifest` with semver + dependency declaration (unblocks plugin versioning)
2. **P1**: `IVisionContent` for multimodal messages
3. **P1**: `IMcpServer` contract for MCP support
4. **P2**: `ITelemetrySink` for opt-in usage stats
5. **P2**: Replace `MemoryPack` coupling with `IEventSerializer` abstraction

## Stability promise

The Domain layer is the most stable part of Harbor. Breaking changes here break every consumer. We commit to:

- No breaking changes within a major version.
- Deprecation via `[Obsolete]` for at least one minor version before removal.
- Migration guides in [`docs/MIGRATION.md`](../../docs/MIGRATION.md) for any breaking change.
