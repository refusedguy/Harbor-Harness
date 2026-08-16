# Plan - Harbor.Application

## Status: MVP

The Application layer extracted from `Harbor.Core` as part of the Clean Architecture refactor (S1). Holds pure use cases; depends on `Harbor.Abstractions` (Domain) + `Harbor.Registries` only.

## Done

- [x] `AgentLoop` (Chain-of-Responsibility turn loop) - extracted from Harbor.Core
- [x] `DefaultAgent` - stateful IAgent wrapping AgentLoop
- [x] `CompactionService` - anchored-summary compaction
- [x] `SystemPromptBuilder` - assembles system prompt (identity + tools + agents + MCP + skills)
- [x] `MessageConverter` - domain `AgentMessage` <-> LLM `LlmMessage`
- [x] Pure Application layer - no Infrastructure references (no HTTP, no file I/O)

## TODO

- [ ] Migrate remaining use cases from Harbor.Core (SessionManager, OnboardingService)
- [ ] Add unit tests for AgentLoop edge cases (empty session, max-context, tool error)
- [ ] Formalize the SystemPromptBuilder sections as plug-in (SkillProvider interface)
- [ ] Streaming tool-call output (currently buffered per tool invocation)
- [ ] Concurrent agent runs on the same session (session-level AsyncLock)

## Known issues

- Harbor.Core still duplicates some logic; full migration to Harbor.Application is in progress (S1).
- SystemPromptBuilder is monolithic - should be split into section providers.

## Next priorities

1. **P0**: Finish extracting use cases from Harbor.Core
2. **P1**: Add unit tests
3. **P1**: Split SystemPromptBuilder into section providers
4. **P2**: Streaming tool-call output
5. **P2**: Concurrent session safety

## See also

- [README.md](README.md)
- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../Harbor.Core/README.md](../Harbor.Core/README.md) - predecessor (still exists for backward compat)
