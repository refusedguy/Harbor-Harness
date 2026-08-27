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
- [x] AGENTLOOP-DECOMP: `ToolDispatcher` extracted from the loop — parallel dispatch,
      sequential fan-out for `ExecutionMode.Sequential` tools, permission gating, per-call
      timeouts (`Agents/ToolDispatcher.cs`, seam `Agents/IToolDispatcher.cs`, DI in CoreModule)
- [x] AGENTLOOP-DECOMP: `StreamingCoalescer` extracted — text/thinking/tool-call delta
      buffering out of the turn loop (`Agents/StreamingCoalescer.cs`)
- [x] AGENTLOOP-DECOMP: `RetryPolicy` in place with capped **exponential backoff**
      (`BaseDelay · 2^(attempt−1)`) and optional full jitter for transient LLM failures;
      wired into `AgentLoop`'s stream call site (`Resilience/RetryPolicy.cs`)
- [x] Pure Application layer - no Infrastructure references (no HTTP, no file I/O)

## TODO

- [ ] Add unit tests for AgentLoop edge cases (empty session, max-context, tool error)
- [ ] Formalize the SystemPromptBuilder sections as plug-in (SkillProvider interface)
- [ ] Streaming tool-call output (currently buffered per tool invocation)
- [ ] Concurrent agent runs on the same session (session-level AsyncLock)
- [ ] Extend RetryPolicy coverage to direct `ILlmClient` call sites outside the loop's
      wrapped stream (e.g. a retrying decorator around `ILlmClient`)

## Known issues

- SystemPromptBuilder is monolithic - should be split into section providers.
- `AgentLoop` retries its own LLM stream; provider calls made directly against an
  `ILlmClient` elsewhere are not retried by default.

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
