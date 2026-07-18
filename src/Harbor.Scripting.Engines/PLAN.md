# Plan — Harbor.Scripting.Engines

## Status: MVP

## Done

- [x] JintScriptEngine (full JS interpreter)
- [x] Timeout enforcement via CancellationToken
- [x] Memory limits via Jint's constraints

## TODO

- [ ] SharpTS AOT runtime (research)
- [ ] Async function support (top-level await)
- [ ] ESM module support

## Known issues

- Jint is slower than V8 — fine for short scripts, slow for hot loops.

## Next priorities

1. **P0**: Async function support
2. **P1**: ESM modules
3. **P2**: SharpTS AOT runtime
