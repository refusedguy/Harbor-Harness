# Plan — Harbor.Providers.Anthropic

## Status: Stable

## Done

- [x] AnthropicLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] cache_control breakpoints on system prompt + tool schema
- [x] Extended thinking stream parsing

## TODO

- [ ] Retry + exponential backoff on 429 / 5xx
- [ ] Cancellation token propagation through all HTTP calls
- [ ] Token-usage reporting (prompt/completion/cache) on final event
- [ ] Automatic cache-control breakpoint placement
- [ ] Vision content blocks

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).
- No native prompt-caching hints emitted (only Harbor.Providers.Anthropic supports them).

## Next priorities

1. **P0**: Tighten cancellation semantics; verify CT flows into HttpClient.SendAsync
2. **P1**: Add retry policy for transient 5xx
3. **P2**: Surface token-usage telemetry as an event
