# Plan — Harbor.Plugin.FileTree

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
- [ ] Respect .gitignore via `Microsoft.Extensions.FileSystemGlobbing`
- [ ] Color output (ANSI) when TTY

## Known issues

- Doesn't respect `.gitignore` yet — shows `bin/`, `obj/`, etc.

## Next priorities

1. **P1**: Add unit tests
2. **P2**: Error handling polish
