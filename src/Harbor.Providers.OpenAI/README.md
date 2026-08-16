# Harbor.Providers.OpenAI

Native OpenAI provider for GPT models. Supports both Chat Completions API (default) and Responses API (required for o1 / o3 / GPT-5+ reasoning models).

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `OpenAiLlmClient` — implements `ILlmClient` from Harbor.Abstractions
- `OpenAiLlmClient` — concrete HTTP client
- `OpenAiMessageConverter` — translates `Message` <-> OpenAI Chat Completions JSON
- `OpenAiResponsesConverter` — alternate converter for the Responses API

## Usage

Registered via DI in the composition root (e.g. `Harbor.App.Cli/Hosting/HostBuilder.cs`):

```csharp
services.AddHttpClient<OpenAiLlmClient>(c => c.BaseAddress = new Uri(baseUri));
services.AddSingleton<ILlmClient, OpenAiLlmClient>();
```

Then resolved by `ProviderRegistry` when an agent run targets the `openai` provider.

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
