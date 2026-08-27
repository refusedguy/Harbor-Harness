# Plan - Harbor.Application

## Status: Stable

The Application layer was extracted from `Harbor.Core` in the S1 split and
holds all pure use cases. Depends on `Harbor.Abstractions` + `Harbor.Extensions`
+ `Harbor.Diagnostics.Abstractions` only.

## Done

- [x] `AgentLoop` (Chain-of-Responsibility turn loop) - extracted from Harbor.Core
- [x] `DefaultAgent` - stateful IAgent wrapping AgentLoop
- [x] `CompactionService` - anchored-summary compaction
- [x] `SystemPromptBuilder` - assembles system prompt (identity + tools + agents + MCP + skills)
      plus `CachingSystemPromptBuilder` memoizing decorator (`Sessions/CachingSystemPromptBuilder.cs:31`)
- [x] `MessageConverter` - domain `AgentMessage` <-> LLM `LlmMessage`
- [x] Migration out of Harbor.Core complete (Core is now an empty facade, see its PLAN)
- [x] Onboarding: live model catalog during first-run wizard via
      `ProviderPresets` + provider health probes (PROD-UI-0;
      `Configuration/ProviderPresets.cs:11`, `Providers/ProviderHealthCheck.cs:13`)
- [x] `IProviderHealthCheck` contract implementation (`Providers/ProviderHealthCheck.cs`)
- [x] Central retry policy helpers for 429/5xx (`Resilience/RetryPolicyExtensions.cs:17`)
- [x] `TokenTracker` / `WorkspaceContextSource` session helpers (`Sessions/`)
- [x] Pure Application layer - no Infrastructure references (no HTTP, no file I/O)

## TODO

- [ ] Add unit tests for AgentLoop edge cases (empty session, max-context, tool error)
- [ ] Formalize the SystemPromptBuilder sections as plug-in (SkillProvider interface)
- [ ] Streaming tool-call output (currently buffered per tool invocation)
- [ ] Concurrent agent runs on the same session (session-level AsyncLock)
- [ ] Wire the centralized `RetryPolicyExtensions` into provider call paths

## Known issues

- SystemPromptBuilder is monolithic - should be split into section providers.
- Retry helpers exist but are not yet applied by default to provider calls.

## Next priorities

1. **P1**: Add AgentLoop unit tests for edge cases
2. **P1**: Split SystemPromptBuilder into section providers
3. **P2**: Wire RetryPolicyExtensions as a decorator around ILlmClient
4. **P2**: Streaming tool-call output
5. **P2**: Concurrent session safety

## See also

- [README.md](README.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../Harbor.Core/README.md](../Harbor.Core/README.md) - deprecated shell this project replaced
