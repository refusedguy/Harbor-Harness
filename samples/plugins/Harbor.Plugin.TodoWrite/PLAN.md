# Plan — Harbor.Plugin.TodoWrite

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
- [ ] Persistent todos (survive agent restart)
- [ ] Sub-task nesting
- [ ] Due dates + reminders

## Known issues

- Todos are in-memory only — lost on restart.
- No sub-task nesting yet.

## Next priorities

1. **P1**: Add unit tests
2. **P2**: Error handling polish
