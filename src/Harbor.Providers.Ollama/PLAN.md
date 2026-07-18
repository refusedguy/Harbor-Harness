# Plan — Harbor.Providers.Ollama

## Status: Stable

## Done

- [x] OllamaLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] NDJSON streaming
- [x] Local model name resolution

## TODO

- [ ] Retry + exponential backoff on 429 / 5xx
- [ ] Cancellation token propagation through all HTTP calls
- [ ] Token-usage reporting (prompt/completion/cache) on final event
- [ ] Pull / list models endpoint
- [ ] Embeddings endpoint

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).
- No native prompt-caching hints emitted (only Harbor.Providers.Ollama supports them).

## Next priorities

1. **P0**: Tighten cancellation semantics; verify CT flows into HttpClient.SendAsync
2. **P1**: Add retry policy for transient 5xx
3. **P2**: Surface token-usage telemetry as an event
