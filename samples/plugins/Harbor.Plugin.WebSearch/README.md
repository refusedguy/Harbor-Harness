# Harbor.Plugin.WebSearch

Sample plugin that adds a `web_search` tool — performs a web search via a configured search API (DuckDuckGo by default, or Google CSE / Bing Search via config). Demonstrates `IToolPlugin` + HTTP + config injection.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- `Harbor.Core` (for tool dispatch integration)
- `Microsoft.Extensions.Http` (for IHttpClientFactory)

## Public API

- `WebSearchPlugin` — implements `IToolPlugin`
- `WebSearchTool` — the `ITool` implementation
- `WebSearchOptions` — config (provider, API key, result count)

## Usage

Place the compiled assembly (or the source `.cs` file) under `~/.harbor/plugins/` (or whatever `PluginRoot` is configured to). Harbor will discover and load it on startup.

Or, in code:

```csharp
var host = new PluginHostBuilder()
    .WithSource(new FileSystemPluginSource("/path/to/this/plugin", logger))
    .Build();
await host.LoadAllAsync(ct);
```

## How it works

Tool takes a query string, calls the configured search API via `HttpClient`, parses the JSON response, returns a formatted list of `{title, url, snippet}` items. Provider is selected via `WebSearchOptions.Provider` (`duckduckgo` | `google` | `bing`).

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
