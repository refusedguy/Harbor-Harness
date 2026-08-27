# Harbor.Providers.Ollama

Native Ollama provider for local LLM inference. Streams responses as NDJSON (one JSON object per line).

## Layer

Infrastructure — LLM provider implementation. References `Harbor.Abstractions` (Domain) only.

## Dependencies

- `Harbor.Abstractions` (Domain)
- `Microsoft.Extensions.Logging.Abstractions`

## Public API

- `OllamaLlmClient` — the only public client type: implements `ILlmClient` from Harbor.Abstractions, NDJSON line parsing happens inline while streaming
- `OllamaConfig` — base URL / model knobs

## Usage

Registered in `src/Harbor.Hosting/Modules/ProviderFactories.cs` via
`pb.AddProvider(new OllamaProviderFactory(httpFactory))` and resolved by
`ProviderRegistry` when an agent run targets the `ollama` provider id from
`providers/ollama.json`. No API key is needed for a local daemon (`OLLAMA_HOST`).

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
