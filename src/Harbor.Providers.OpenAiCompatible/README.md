# Harbor.Providers.OpenAiCompatible

Generic OpenAI-compatible LLM provider. Works with any endpoint that implements the `/v1/chat/completions` shape — OpenRouter, DeepSeek, Groq, Mistral, xAI, Together, Fireworks, Cerebras, vLLM, etc.

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.Http`

## Public API

- `OpenAiCompatibleLlmClient` — implements `ILlmClient` from Harbor.Abstractions
- `ProviderConfig` — JSON preset mapping: base URL, auth env var, api type, `modelMapping`, headers (`ProviderConfig.cs:8`)
- `ModelMapping` — field names for `/models` parsing (`id`, `displayName`, `contextWindow`) (`ProviderConfig.cs:83`)
- `EnvVarAuthResolver` — resolves the per-provider API key from its env var (`ProviderConfig.cs:107`)
- `DynamicModelCatalog` — live `/models` fetching through the preset's mapping (`ProviderConfig.cs:143`)
- `Compat/IProviderCompatFlag` + `DeepSeekReasonerCompatFlag`, `GroqMaxTokensCompatFlag` — Strategy-pattern quirk flags replacing string switches on provider ids
- `OpenAiSseParser` *(internal)* — incremental SSE parse used by the client

## Usage

Registered in `src/Harbor.Hosting/Modules/ProviderFactories.cs` and driven by the
JSON presets in `providers/*.json` (`apiType: "openai-compatible"`). Every OpenAI-compatible
provider (kilocode, openrouter, deepseek, groq, mistral, xai, together,
fireworks, cerebras, vllm) is a JSON file — no code.

## Configuration

Provider is selected via `appsettings.json` or env var:

```json
{ "Harbor": { "Provider": "openai-compatible", "OpenAiCompatible": { ... } } }
```

Or via environment variable:

```bash
export HARBOR_PROVIDER=openai-compatible
export OpenAiCompatible__ApiKey=sk-...
```

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../providers/](../../providers/) — JSON-config templates for additional providers
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md)
