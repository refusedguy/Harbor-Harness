# Plan — Harbor.Providers.Anthropic

## Status: Stable

## Done

- [x] AnthropicLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] cache_control breakpoints on system prompt + tool schema (ephemeral, 1h TTL — `AnthropicLlmClient.cs:137-148`)
- [x] Extended thinking stream parsing
- [x] Cancellation token propagation through the streaming pipeline
      (`[EnumeratorCancellation]` at `AnthropicLlmClient.cs:63`, flows into auth + SSE pump)
- [x] Token-usage reporting on the final step event incl. cache read/creation counts
      (`message_delta` -> `StepFinishEvent(usage)` at `AnthropicLlmClient.cs:404-413`)

## TODO

- [ ] Wire centralized retry for 429 / 5xx (helpers already exist in `Harbor.Application/Resilience/RetryPolicyExtensions.cs`, not yet applied by default to provider calls)
- [ ] Automatic cache-control breakpoint placement (currently manual on system prompt)
- [ ] Vision content blocks

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).

## Next priorities

1. **P1**: Automatic cache-control breakpoint placement
2. **P2**: Vision content blocks
3. **P2**: Decorate `ILlmClient` with the shared retry policy
