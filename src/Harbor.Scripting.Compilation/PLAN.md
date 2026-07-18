# Plan — Harbor.Scripting.Compilation

## Status: Draft

## Done

- [x] NoopScriptCompiler for .js passthrough
- [x] CachingScriptCompiler decorator
- [ ] TypeScriptScriptCompiler (SharpTS integration — research phase)

## TODO

- [ ] Complete SharpTS TypeScript transpiler integration
- [ ] Source map support
- [ ] Diagnostics surfacing (TS compile errors)

## Known issues

- TypeScript transpilation not yet functional — SharpTS research in progress.

## Next priorities

1. **P0**: Complete SharpTS transpiler
2. **P1**: Source map + diagnostics
3. **P2**: Multi-file project support
