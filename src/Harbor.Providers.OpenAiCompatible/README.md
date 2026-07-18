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
- `OpenAiCompatibleLlmClient` — concrete HTTP client
- `OpenAiCompatibleOptions` — base URL, API key, model name, custom headers

## Usage

Registered via DI in the composition root (e.g. `Harbor.App.Cli/Hosting/HostBuilder.cs`):

```csharp
services.AddHttpClient<OpenAiCompatibleLlmClient>(c => c.BaseAddress = new Uri(baseUri));
services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();
```

Then resolved by `ProviderRegistry` when an agent run targets the `openai-compatible` provider.

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
