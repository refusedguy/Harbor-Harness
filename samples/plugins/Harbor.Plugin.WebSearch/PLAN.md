# Plan — Harbor.Plugin.WebSearch

## Status: Sample

Sample plugin — demonstrates the `IToolPlugin` extension point. Not a production-grade implementation.

## Done

- [x] Implements `IToolPlugin`
- [x] Plugin manifest (name, version)
- [x] Initialize / Shutdown lifecycle
- [x] Works with the default plugin pipeline (`PluginHostBuilder`)

## TODO

- [ ] Add unit tests
- [ ] Add a sample invocation in docs
- [ ] Error handling polish
- [ ] Add `web_fetch` companion tool (HTTP GET -> markdown)
- [ ] Caching (don't re-search same query)
- [ ] Rate limiting
- [ ] Bing + Google CSE providers

## Known issues

- DuckDuckGo HTML scraping can break if their HTML changes.
- No caching — re-searches the same query.

## Next priorities

1. **P1**: Add unit tests
2. **P2**: Error handling polish
