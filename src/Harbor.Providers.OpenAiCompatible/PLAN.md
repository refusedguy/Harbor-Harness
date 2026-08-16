# Plan — Harbor.Providers.OpenAiCompatible

## Status: Stable

## Done

- [x] OpenAiCompatibleLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] Configurable base URL + headers
- [x] Streaming SSE parsing
- [x] Tool-call support
- [x] Provider-specific quirk flags (e.g. some providers don't support system role)

## TODO

- [ ] Retry + exponential backoff on 429 / 5xx
- [ ] Cancellation token propagation through all HTTP calls
- [ ] Token-usage reporting (prompt/completion/cache) on final event
- [ ] Per-provider quirk profiles (OpenRouter, DeepSeek, Groq, etc.)

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).
- Some compatible providers (e.g. older vLLM builds) don't honor `n=1` — fallback to streaming single response.

## Next priorities

1. **P0**: Tighten cancellation semantics; verify CT flows into HttpClient.SendAsync
2. **P1**: Add retry policy for transient 5xx
3. **P2**: Surface token-usage telemetry as an event
4. **P2**: Per-provider quirk profiles
