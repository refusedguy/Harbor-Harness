# Plan — Harbor.Providers.OpenAI

## Status: Stable

## Done

- [x] OpenAiLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] Chat Completions API
- [x] Responses API for o1/o3 reasoning models
- [x] Tool-call streaming

## TODO

- [ ] Retry + exponential backoff on 429 / 5xx
- [ ] Cancellation token propagation through all HTTP calls
- [ ] Token-usage reporting (prompt/completion/cache) on final event
- [ ] Vision content blocks
- [ ] Structured outputs (JSON schema)

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).
- No native prompt-caching hints emitted (only Harbor.Providers.OpenAI supports them).

## Next priorities

1. **P0**: Tighten cancellation semantics; verify CT flows into HttpClient.SendAsync
2. **P1**: Add retry policy for transient 5xx
3. **P2**: Surface token-usage telemetry as an event
