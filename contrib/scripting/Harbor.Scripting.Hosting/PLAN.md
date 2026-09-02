# Plan — Harbor.Scripting.Hosting

## Status: Draft

## Done

- [x] Full pipeline orchestration
- [x] ScriptHostBuilder fluent API
- [x] ContinueOnFailure option

## TODO

- [ ] Hot reload
- [ ] Script lifecycle (init/teardown hooks)
- [ ] Script dependency graph

## Known issues

- No hot reload.
- TypeScript compilation not yet complete (SharpTS in research).

## Next priorities

1. **P0**: Complete SharpTS so TS scripts actually compile
2. **P1**: Hot reload
3. **P2**: Script lifecycle hooks
