# Harbor.Providers.Anthropic

Native Anthropic Messages API provider for Claude models. Supports `cache_control` (prompt caching), extended thinking, and fine-grained tool-call streaming.

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`
- (HTTP via `HttpClient` from `Microsoft.Extensions.Http` is pulled in transitively from the composition root)

## Public API

- `AnthropicLlmClient` — the only public client type: implements `ILlmClient` from Harbor.Abstractions, owns SSE parsing and Messages-API JSON conversion internally (via `Harbor.Providers.Shared` helpers linked as source)
- `AnthropicConfig` — base URL / API version / beta features knobs

## Usage

Registered in `src/Harbor.Hosting/Modules/ProviderFactories.cs` via
`pb.AddProvider(new AnthropicProviderFactory(httpFactory, authStore))` and resolved by
`ProviderRegistry` when an agent run targets the `anthropic` provider id from
`providers/anthropic.json`.

## Configuration

Provider is selected via `appsettings.json` or env var:

```json
{ "Harbor": { "Provider": "anthropic", "Anthropic": { ... } } }
```

Or via environment variable:

```bash
export HARBOR_PROVIDER=anthropic
export Anthropic__ApiKey=sk-...
```

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../providers/](../../providers/) — JSON-config templates for additional providers
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md)
