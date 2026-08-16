# Harbor.Domain

> Pure domain layer — models, value objects, events, permission rules.
> Zero Harbor project dependencies. The bedrock of the Harbor architecture.

## What's inside

| Folder                              | Contents                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
|-------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Models/Session.cs`                 | `Session`, `SessionMetadata`, `Usage`, `Pricing`, `ModelInfo`, `StopReason`, `ReasoningEffort`, `ToolChoice`, `CacheStrategy`, `JsonStringConverter<T>` base                                                                                                                                                                                                                                                                                                                                             |
| `Models/Messages.cs`                | `AgentMessage` (abstract), `UserMessage`, `AssistantMessage`, `ToolResultMessage`, `ContentPart` (abstract), `TextPart`, `ThinkingPart`, `ToolCallPart`, `FilePart`, `ToolResult`, `ToolResultEntry`, `FileAttachment` — MemoryPack `[MemoryPackable]` tagged-union hierarchy                                                                                                                                                                                                                            |
| `Models/MemoryPackFormatters.cs`    | `JsonElementMemoryPackFormatter` — custom MemoryPack formatter for `System.Text.Json.JsonElement` (round-trips JSON as length-prefixed UTF-16 string)                                                                                                                                                                                                                                                                                                                                                    |
| `Models/Identifiers/Identifiers.cs` | `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` — strongly-typed `ValueObject` wrappers + `IdentifierValidation` (regex-free char validators for hot paths)                                                                                                                                                                                                                                                                                                    |
| `Events/AgentEvent.cs`              | `AgentEvent` (abstract) + 13 derived event types (`AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`/`MessageUpdateEvent`/`MessageEndEvent`, `ToolExecutionStart/Update/EndEvent`, `TurnEndEvent`, `AgentEndEvent`, `AgentErrorEvent`, `CompactionStarted/CompletedEvent`, `SessionStatsEvent`) and the `LlmEvent` polymorphic stream hierarchy (`TextStart/Delta/EndEvent`, `ThinkingStart/Delta/EndEvent`, `ToolCallStart/Delta/EndEvent`, `StepStart/FinishEvent`, `FinishEvent`, `ErrorEvent`) |
| `Permissions/PermissionRuleset.cs`  | `PermissionRuleset` (Specification pattern), `PermissionRule`, `PermissionAction` enum, `PermissionRequest`, `PermissionResponse`, `IPermissionService` interface — domain logic for Allow/Ask/Deny evaluation with process-wide regex cache                                                                                                                                                                                                                                                             |

## Layer

**Domain.** This is the lowest layer of the Harbor onion. Nothing in the codebase is below it; every other Harbor project (Application, Infrastructure, Presentation) references it transitively via the `Harbor.Abstractions` facade.

## Dependencies

Only NuGet packages:

| Package                      | Why                                                                                                                                                                                                |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `CSharpFunctionalExtensions` | `ValueObject` base class for `SessionId` / `MessageId` / `ToolCallId` / `ProviderId` / `ModelRef` / `ToolName` / `AgentName`                                                                       |
| `MemoryPack`                 | `[MemoryPackable]` source generator for tagged-union serialization of `Session`, `AgentMessage`, `ContentPart`, `Usage`, `Pricing`, `ModelInfo`, `ToolResult`, `ToolResultEntry`, `FileAttachment` |

**Zero Harbor project references.** This is the architectural invariant enforced by `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs::Domain_HasZeroHarborProjectReferences`.

## Namespaces preserved

All files in `Harbor.Domain` keep their original `Harbor.Abstractions.*` namespace declarations:

| File                                | Namespace                                |
|-------------------------------------|------------------------------------------|
| `Models/Session.cs`                 | `Harbor.Abstractions.Models`             |
| `Models/Messages.cs`                | `Harbor.Abstractions.Models`             |
| `Models/MemoryPackFormatters.cs`    | `Harbor.Abstractions.Models`             |
| `Models/Identifiers/Identifiers.cs` | `Harbor.Abstractions.Models.Identifiers` |
| `Events/AgentEvent.cs`              | `Harbor.Abstractions.Events`             |
| `Permissions/PermissionRuleset.cs`  | `Harbor.Abstractions.Permissions`        |

This means **zero consumer code changes** — every project that does `using Harbor.Abstractions.Models;` keeps compiling and resolves to the types in `Harbor.Domain.dll` via the transitive project reference chain `Harbor.Abstractions → Harbor.Domain`.

## Design principles

1. **Pure domain.** No I/O, no DI, no logging, no HTTP. Just data and the in-process rules that govern it.
2. **Immutable.** All model types are `record` (positional or `init`-only). `PermissionRuleset` is a `sealed record` with a pre-sorted `_sortedRules` array; mutation produces a new instance via `Merge`.
3. **Allocation-light.** Hot paths use index-based `for` loops instead of LINQ; `IdentifierValidation` skips `Regex` entirely and walks `char`s directly; `AssistantMessage.AppendText/Thinking/ToolCall` use manual array copy to avoid `Append(...).ToArray()`'s struct iterator + final array allocation.
4. **Tagged unions for polymorphic hierarchies.** `AgentMessage`, `ContentPart`, `AgentEvent`, `LlmEvent` are abstract `record`s decorated with `[MemoryPackUnion]` (binary) and `[JsonDerivedType]` (JSON) — the same hierarchy serializes to both formats without per-impl custom code.
5. **No BCL helpers.** Pooling (`ArrayPool`, `StringBuilderPool`), FrozenSet materializers, and MemoryPack round-trip helpers live in `Harbor.Extensions` (they may grow infrastructure dependencies that the domain layer must not pull in).

## Public API surface (most-used types)

```csharp
// Session lifecycle
var session = Session.Create(directory: "/repo", agentName: "code", providerId: "anthropic", modelId: "claude-opus-4");
var sessionWithUsage = session with { Metadata = session.Metadata.AddUsage(usage) };

// Identifiers (ValueObject)
var sessionId = SessionId.Create("abc123");
var providerId = ProviderId.Create("Anthropic");    // normalized to "anthropic"
var modelRef = ModelRef.TryParse("anthropic/claude-opus-4");
var toolName = ToolName.Create("read_file");        // validates ^[a-z][a-z0-9_]*$

// Messages
var assistant = AssistantMessage.Empty(sessionId: "abc", model: "claude-opus-4")
    .AppendText("Hello")
    .AppendToolCall(new ToolCallPart("call_1", "read", argsJson))
    .WithFinish(StopReason.ToolUse, usage);

// Permission ruleset
var ruleset = PermissionRuleset.Default;
var action = ruleset.Evaluate(permission: "bash", argPath: "rm -rf /");  // → Deny
var merged = ruleset.Merge(userRuleset);
```

## See also

- `src/Harbor.Extensions/README.md` — pooling + MemoryPack round-trip helpers
- `src/Harbor.Abstractions/README.md` — interface contracts (the facade)
- `docs/ARCHITECTURE_LAYERS.md` — full layering matrix
- `tests/Harbor.Abstractions.Tests/` — tests for identifiers, sessions, permission rulesets
- `tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs` — enforces this layer's invariants
