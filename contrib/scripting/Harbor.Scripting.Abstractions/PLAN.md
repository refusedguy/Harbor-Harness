# Plan — Harbor.Scripting.Abstractions

## Status: MVP

## Done

- [x] All three pipeline contracts
- [x] ScriptGlobals (safe API surface exposed to scripts)
- [x] ScriptEngineOptions (resource limits)
- [x] XML docs on every public type

## TODO

- [ ] Network script store contract (load scripts from URL)
- [ ] Script versioning contract
- [ ] Sandboxing contract (isolated AppDomain / AssemblyLoadContext)

## Known issues

- No versioning contract yet — scripts identified by name only.

## Next priorities

1. **P0**: Add script manifest schema
2. **P1**: Network store contract
3. **P2**: Sandbox boundary contract
