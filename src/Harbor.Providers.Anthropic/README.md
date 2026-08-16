# Harbor.Providers.Anthropic

Native Anthropic Messages API provider for Claude models. Supports `cache_control` (prompt caching), extended thinking, and fine-grained tool-call streaming.

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`
- (HTTP via `HttpClient` from `Microsoft.Extensions.Http` is pulled in transitively from the composition root)

## Public API

- `AnthropicLlmClient` — implements `ILlmClient` from Harbor.Abstractions
- `AnthropicLlmClient` — concrete HTTP client
- `AnthropicMessageConverter` — translates `Message` <-> Anthropic Messages API JSON
- `AnthropicStreamParser` — SSE parser for streaming responses

## Usage

Registered via DI in the composition root (e.g. `Harbor.App.Cli/Hosting/HostBuilder.cs`):

```csharp
services.AddHttpClient<AnthropicLlmClient>(c => c.BaseAddress = new Uri(baseUri));
services.AddSingleton<ILlmClient, AnthropicLlmClient>();
```

Then resolved by `ProviderRegistry` when an agent run targets the `anthropic` provider.

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
