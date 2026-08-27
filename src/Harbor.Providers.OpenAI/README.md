# Harbor.Providers.OpenAI

Native OpenAI provider for GPT models. Supports both Chat Completions API (default) and Responses API (required for o1 / o3 / GPT-5+ reasoning models).

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `OpenAILlmClient` — implements `ILlmClient` from Harbor.Abstractions; owns both Chat Completions and Responses wire formats internally
- `OpenAIConfig` — client configuration incl. `ForceResponsesApi`
- `OpenAIModels` — well-known model constants

## Usage

Registered in `src/Harbor.Hosting/Modules/ProviderFactories.cs` and resolved by
`ProviderRegistry` when an agent run targets the `openai` provider id from
`providers/openai.json`. The client itself picks the API per request:
reasoning models (o1/o3/o4-mini) or `_config.ForceResponsesApi = true` route
through the Responses API, everything else uses Chat Completions
(`OpenAILlmClient.cs:80`).

## Configuration

Provider is selected via `appsettings.json` or env var:

```json
{ "Harbor": { "Provider": "openai", "OpenAI": { ... } } }
```

Or via environment variable:

```bash
export HARBOR_PROVIDER=openai
export OpenAI__ApiKey=sk-...
```

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../providers/](../../providers/) — JSON-config templates for additional providers
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md)
