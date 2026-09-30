# Harbor.Plugin.WebSearch

Sample plugin that adds a `websearch` tool — queries DuckDuckGo's HTML endpoint and scrapes the results. Demonstrates `IToolPlugin` + HTTP.

## Layer

Sample plugin — implements `IPlugin` (+ `IToolPlugin`) from `Harbor.Abstractions`. Loaded by the Harbor plugin pipeline (`PluginHostBuilder`).

## Dependencies

- `Harbor.Abstractions` (Domain — for `IPlugin` / `IToolPlugin`)
- (nothing else — tool dispatch arrives through `ToolContext`, so the plugin needs
  no Application-layer reference. The `HttpClient` is a static field, so there is
  no `IHttpClientFactory` and no `Microsoft.Extensions.Http` reference)

## Public API

- `WebSearchPlugin` — implements `IToolPlugin`
- `WebSearchTool` — the `ITool` implementation (tool name `websearch`)

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

Tool takes a `query` and an optional `maxResults` (default 5, max 20), GETs `https://html.duckduckgo.com/html/?q=…` through a static `HttpClient` (15s timeout), and scrapes `result__a` / `result__snippet` out of the returned HTML with two compiled regexes. Results are a private `SearchResult` record (title, url, snippet) returned as a formatted list. There is no configuration surface: the provider is not selectable, and there is no API key — DuckDuckGo's HTML endpoint is the only backend this sample talks to.

## See also

- [../../../docs/PLUGIN_SYSTEM.md](../../../docs/PLUGIN_SYSTEM.md)
- [../../../docs/PLUGIN_DEVELOPMENT.md](../../../docs/PLUGIN_DEVELOPMENT.md)
- [../../../docs/ARCHITECTURE_LAYERS.md](../../../docs/ARCHITECTURE_LAYERS.md)
