# Plan — Harbor.Scripting.Bridge

## Status: Draft

## Done

- [x] ScriptGlobals construction
- [x] ScriptToolAdapter (scripts can define tools)
- [x] ScriptEventSink (scripts can subscribe to events)

## TODO

- [ ] Script-defined agents (script defines an agent + its system prompt)
- [ ] Script-defined providers
- [ ] Sandboxing (restrict which Harbor APIs scripts can call)

## Known issues

- No sandboxing — scripts can call any Harbor API exposed via globals.

## Next priorities

1. **P0**: Capability-based API exposure (script declares what it needs)
2. **P1**: Script-defined agents
3. **P2**: Sandboxing
