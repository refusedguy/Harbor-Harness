# Plan — Harbor.Providers.OpenAiCompatible

## Status: Stable

## Done

- [x] OpenAiCompatibleLlmClient implementing ILlmClient
- [x] Streaming message conversion (assistant deltas -> AgentEvent.StreamDelta)
- [x] Tool-call serialization
- [x] Error handling via Result<T>
- [x] Configurable base URL + headers via JSON presets (`ProviderConfig`)
- [x] Streaming SSE parsing (`OpenAiSseParser` + `Harbor.Providers.Shared/SsePump.cs`)
- [x] Tool-call support
- [x] Live `/models` catalog mapping via `DynamicModelCatalog` / `ModelMapping` (PROD-UI-0 preset work)
- [x] Provider-specific quirk flags as a Strategy instead of string switches:
      `Compat/IProviderCompatFlag.cs` with `DeepSeekReasonerCompatFlag` (:49) and
      `GroqMaxTokensCompatFlag` (:71), cataloged in `ProviderCompatFlags`
- [x] Usage forwarding from stream end events (`OpenAiCompatibleLlmClient.cs:84`)

## TODO

- [ ] Wire centralized retry for 429 / 5xx (helpers exist in `Harbor.Application/Resilience/RetryPolicyExtensions.cs`)
- [ ] Extend quirk-flag coverage beyond DeepSeek/Groq where providers deviate further

## Known issues

- Cold-start latency on first request (HTTP connection pool warm-up).
- Some compatible providers (e.g. older vLLM builds) don't honor `n=1` — fallback to streaming single response.

## Next priorities

1. **P2**: Decorate `ILlmClient` with the shared retry policy
2. **P2**: Broaden quirk-flag profiles as new compatible providers are onboarded
