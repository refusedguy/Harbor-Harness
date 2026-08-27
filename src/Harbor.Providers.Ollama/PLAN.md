# Plan — Harbor.Providers.Ollama

## Status: Stable

## Done

- [x] OllamaLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] NDJSON streaming (parsing inline in `OllamaLlmClient.cs`)
- [x] Local model name resolution; no API key needed (`OLLAMA_HOST` daemon)
- [x] Cancellation token propagation through all HTTP calls
- [x] Token-usage reporting on the final step event (`OllamaLlmClient.cs:325`)

## TODO

- [ ] Wire centralized retry for transient 5xx (helpers exist in `Harbor.Application/Resilience/RetryPolicyExtensions.cs`)
- [ ] Pull / list models endpoint from code paths that need it outside `/models`
- [ ] Embeddings endpoint

## Known issues

- Cold-start latency while the local daemon loads a model into VRAM/RAM.

## Next priorities

1. **P2**: Embeddings endpoint support
2. **P2**: Decorate `ILlmClient` with the shared retry policy
