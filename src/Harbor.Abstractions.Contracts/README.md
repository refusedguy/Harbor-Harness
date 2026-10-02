# Harbor.Abstractions.Contracts

The **pure contract/data layer** of Harbor — value objects, domain events, messages, session models, permission rules, and MemoryPack formatters. Zero Harbor project references. Formerly `Harbor.Domain` (deleted in the F1 decoupling).

## Layer

**Domain (pure models).** The innermost ring of the Clean Architecture onion. Nothing in the solution depends on this except `Harbor.Abstractions` (the facade) and higher-layer consumers that need the actual types. Architecture tests enforce zero Harbor project references:

- `Contracts_HasZeroHarborProjectReferences` in [`tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs`](../../tests/Harbor.Architecture.Tests/AbstractionsSplitLayerRules.cs)

## What's in it

| Subfolder | Contents |
|-----------|----------|
| `Models/` | `Session`, `SessionMetadata`, `Usage`, `Pricing`, `AgentMessage` hierarchy (`UserMessage`, `AssistantMessage`, `ContentPart`, `TextPart`, `ToolResultMessage`), `JsonElementMemoryPackFormatter` (in `Models/MemoryPackFormatters.cs`) |
| `Models/Identifiers/` | `SessionId`, `MessageId`, `ToolCallId`, `ProviderId`, `ModelRef`, `ToolName`, `AgentName` — `ValueObject`-based identifiers with validation |
| `Models/Visitors/` | `AgentMessageVisitor<TResult>`, `ContentPartVisitor<TResult>` — the single GoF traversal over the two message sum types (#461) |
| `Events/` | `AgentEvent` discriminated union (`AgentStartEvent`, `TurnStartEvent`, `MessageStartEvent`, `ToolExecutionStartEvent`, etc.), `LlmStreamErrorException`, `ProviderErrorKind`, `ProviderErrors` |
| `Permissions/` | `PermissionRuleset`, `PermissionRule`, `PermissionAction`, `BashArgMatcher`, `ToolCategory` |

## Public API summary

- **Identifiers**: `SessionId.Create/New/TryCreate`, `MessageId`, `ProviderId`, `ToolName` — all immutable value objects.
- **Session model**: `Session.Create(...)`, `SessionMetadata`, `Usage`, `Pricing.CalculateCost(...)`.
- **Messages**: `AgentMessage` record hierarchy with `AppendText`, `AppendThinking`, `AppendToolCall`.
- **Message walks**: `AgentMessageVisitor<TResult>` / `ContentPartVisitor<TResult>` — one `Accept`/`Walk` dispatch for the message and part unions. Arms are `abstract` (a walker must decide what every kind does) and an unrecognised kind throws instead of being dropped, so extending either union cannot silently regress a consumer.
- **Events**: `AgentEvent` base record with `Timestamp`; sealed subtypes for every agent/tool lifecycle event.
- **Permissions**: `PermissionRuleset.Default/Empty`, `Merge`, `Evaluate`; `BashArgMatcher.IsDestructiveCommand/HasShellMetacharacters`.
- **Serialization**: `JsonElementMemoryPackFormatter` for `JsonElement` and message types. (This entry previously named `MemoryPackFormatters`, which is the *file* `Models/MemoryPackFormatters.cs`, not a type in it — the #966 shape, where a document promises a name no declaration owns.)

## Dependencies

| Package | Purpose |
|---------|---------|
| `CSharpFunctionalExtensions` | `ValueObject` base for identifiers |
| `MemoryPack` | Binary serialization for `[MemoryPackable]` types |

## Tests

Referenced transitively by `tests/Harbor.Abstractions.Tests/` and `tests/Harbor.Domain.Tests/`. No dedicated test project for Contracts alone.

Both projects' declared NuGet dependencies — this one's and the facade's — are
checked against their sources by
[`tools/check-abstractions-contract.py`](../../tools/check-abstractions-contract.py),
which fails on a `<PackageReference>` nothing has a `using` under and on a non-BCL
`using` no `<PackageReference>` covers. `--report` prints the derived census.

## Build

```bash
dotnet build src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj
```

## Known limitations

- No Harbor project references by design; consumers must go through `Harbor.Abstractions` or reference directly.
- `MemoryPack` formatters are generated/registered manually — `JsonElementMemoryPackFormatter.EnsureRegistered()` must be called before serialization.
