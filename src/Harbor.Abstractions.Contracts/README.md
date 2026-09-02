# Harbor.Abstractions.Contracts

The **pure contract/data layer** of Harbor — value objects, domain events, messages, session models, permission rules, and MemoryPack formatters. Zero Harbor project references. Formerly `Harbor.Domain` (deleted in the F1 decoupling).

## Layer

**Domain (pure models).** The innermost ring of the Clean Architecture onion. Nothing in the solution depends on this except `Harbor.Abstractions` (the facade) and higher-layer consumers that need the actual types. Architecture tests enforce zero Harbor project references:

- `Contracts_HasZeroHarborProjectReferences` in [`tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs`](../../tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs)

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Models/` | `Session`, `SessionMetadata`, `Usage`, `Pricing`, `AgentMessage` hierarchy (`UserMessage`, `AssistantMessage`, `ContentPart`, `TextPart`, `ToolResultMessage`), `MemoryPackFormatters` |
| `Models/Identifiers/` | `SessionId`, `MessageId`, `AgentId`, `ProviderId`, `ToolName` — `ValueObject`-based identifiers with validation |
| `Events/` | `AgentEvent` discriminated union (`AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`, `ToolExecutionStartEvent`, etc.), `LlmStreamErrorException`, `ProviderErrorKind`, `ProviderErrors` |
| `Permissions/` | `PermissionRuleset`, `PermissionRule`, `PermissionAction`, `BashArgMatcher`, `ToolCategory` |

## Public API summary

- **Identifiers**: `SessionId.Create/New/TryCreate`, `MessageId`, `ProviderId`, `ToolName` — all immutable value objects.
- **Session model**: `Session.Create(...)`, `SessionMetadata`, `Usage`, `Pricing.CalculateCost(...)`.
- **Messages**: `AgentMessage` record hierarchy with `AppendText`, `AppendThinking`, `AppendToolCall`.
- **Events**: `AgentEvent` base record with `Timestamp`; sealed subtypes for every agent/tool lifecycle event.
- **Permissions**: `PermissionRuleset.Default/Empty`, `Merge`, `Evaluate`; `BashArgMatcher.IsDestructiveCommand/HasShellMetacharacters`.
- **Serialization**: `MemoryPackFormatters` for `JsonElement` and message types.

## Dependencies

| Package | Purpose |
|---------|---------|
| `CSharpFunctionalExtensions` | `ValueObject` base for identifiers |
| `MemoryPack` | Binary serialization for `[MemoryPackable]` types |

## Tests

Referenced transitively by `tests/Harbor.Abstractions.Tests/` and `tests/Harbor.Domain.Tests/`. No dedicated test project for Contracts alone.

## Build

```bash
dotnet build src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj
```

## Known limitations

- No Harbor project references by design; consumers must go through `Harbor.Abstractions` or reference directly.
- `MemoryPack` formatters are generated/registered manually — `JsonElementMemoryPackFormatter.EnsureRegistered()` must be called before serialization.
