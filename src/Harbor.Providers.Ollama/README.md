# Harbor.Providers.Ollama

Native Ollama provider for local LLM inference. Streams responses as NDJSON (one JSON object per line).

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `OllamaLlmClient` — implements `ILlmClient` from Harbor.Abstractions
- `OllamaLlmClient` — concrete HTTP client
- `OllamaNdjsonReader` — incremental NDJSON line parser over HTTP stream

## Usage

Registered via DI in the composition root (e.g. `Harbor.App.Cli/Hosting/HostBuilder.cs`):

```csharp
services.AddHttpClient<OllamaLlmClient>(c => c.BaseAddress = new Uri(baseUri));
services.AddSingleton<ILlmClient, OllamaLlmClient>();
```

Then resolved by `ProviderRegistry` when an agent run targets the `ollama` provider.

## Configuration

Provider is selected via `appsettings.json` or env var:

```json
{ "Harbor": { "Provider": "ollama", "Ollama": { ... } } }
```

Or via environment variable:

```bash
export HARBOR_PROVIDER=ollama
export Ollama__ApiKey=sk-...
```

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../providers/](../../providers/) — JSON-config templates for additional providers
- [../../docs/EXAMPLES.md](../../docs/EXAMPLES.md)
