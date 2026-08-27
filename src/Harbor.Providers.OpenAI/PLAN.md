# Plan — Harbor.Providers.OpenAI

## Status: Stable

## Done

- [x] OpenAILlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] Chat Completions API
- [x] Responses API for reasoning models (o1/o3/o4-mini, or `ForceResponsesApi`) — selected per request (`OpenAILlmClient.cs:80-83`)
- [x] Tool-call streaming
- [x] Cancellation token propagation through all HTTP calls (`OpenAILlmClient.cs:59`, operation-canceled handling at :108)
- [x] Token-usage reporting on the final step event (`StepFinishEvent(usage)` at `OpenAILlmClient.cs:424`)

## TODO

- [ ] Wire centralized retry for 429 / 5xx (helpers exist in `Harbor.Application/Resilience/RetryPolicyExtensions.cs`)
- [ ] Vision content blocks
- [ ] Structured outputs (JSON schema)

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).
- OpenAI prompt caching is server-side and automatic; no client-side cache hints are emitted.

## Next priorities

1. **P2**: Vision content blocks
2. **P2**: Structured outputs (JSON schema)
3. **P2**: Decorate `ILlmClient` with the shared retry policy
