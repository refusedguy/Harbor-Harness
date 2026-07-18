# Plan — Harbor.Core

## Status: Stable

The Application layer is stable. Public API changes follow the same stability promise as `Harbor.Abstractions`.

## Done

- [x] `AgentLoop` — full orchestrator (LLM call -> tool dispatch -> loop)
- [x] `IEventBus` + `InMemoryEventBus` (pub/sub)
- [x] `ProviderRegistry`, `ToolRegistry`, `AgentRegistry` (FrozenDictionary + NonBlocking mutation)
- [x] `SessionManager` — load / save / list / compact sessions
- [x] `CompactionEngine` — anchored-summary compaction
- [x] `PermissionService` — `Allow | Ask | Deny` per tool per glob
- [x] System prompt builder (tools, agents, context, compaction summary)
- [x] Streaming response handling (`IAsyncEnumerable<AgentEvent>`)
- [x] Sub-agent delegation (`task` tool spawns `code` / `plan` / `explore`)
- [x] Cancellation token propagation through all async paths
- [x] ZLinq drop-in (zero-allocation LINQ)
- [x] Full XML docs on public API

## TODO

- [ ] Concurrent agent runs (multiple `AgentLoop.RunAsync` on the same session — needs session locking)
- [ ] Sub-agent cancellation cascade (parent cancel -> children cancel)
- [ ] Retry policy for transient provider errors (429 / 5xx)
- [ ] Token budget enforcement (hard cap on prompt tokens)
- [ ] Streaming tool-call output (currently buffered per tool call)
- [ ] Multi-modal system prompt builder (vision content blocks)
- [ ] Provider failover (primary provider down -> fallback provider)
- [ ] Session branching (fork a session at a given turn)

## Known issues

- `AgentLoop` is not safe for concurrent runs on the same session — caller must serialize.
- Retry logic is ad-hoc per provider — should be centralized in `Harbor.Core`.
- No token budget enforcement — long sessions can blow past model context windows (mitigated by compaction).

## Next priorities

1. **P0**: Concurrent run safety (session-level AsyncLock)
2. **P0**: Centralized retry policy for provider 429/5xx
3. **P1**: Token budget enforcement (pre-flight check, prompt trimming)
4. **P1**: Sub-agent cancellation cascade
5. **P2**: Session branching
6. **P2**: Provider failover
7. **P2**: Streaming tool-call output

## Performance targets

See [../../docs/BENCHMARKS.md](../../docs/BENCHMARKS.md) for current numbers. Targets:

- AgentLoop iteration overhead: <100us (excluding LLM latency)
- Event publish latency: <10us per subscriber
- Registry lookup: <100ns (FrozenDictionary)
- Compaction: <500ms for 100-message session

## Stability promise

Same as `Harbor.Abstractions` — no breaking changes within a major version. Deprecation via `[Obsolete]` for one minor version before removal.
