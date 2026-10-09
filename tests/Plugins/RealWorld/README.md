# Real-world plugin fixtures

Corpus of deliberately awkward, self-contained CS-source plugins for
[issue #422](https://github.com/refusedguy/Harbor-Harness/issues/422)
(slice 1: DI, async, multi-facet). Each file is loaded through the production
`CsPluginLoader` pipeline by `RealWorldPluginsTests` — none of them is part of
any `.csproj`, so `dotnet build` never compiles them directly.

## Fixtures

- `DiToolPlugin.cs` — a tool whose only dependency (`ILoggerFactory`) arrives
  via its constructor through `builder.AddTool(lf => ...)`, and a plugin that
  captures `PluginContext.LoggerFactory` in `Initialize`. The test asserts both
  are the host singleton by identity.
- `AsyncPayloadPlugin.cs` — an async tool returning an ~8KB payload plus a tool
  whose failing path is a `ToolResult.Error` result, not an exception.
- `MultiFacetPanelPlugin.cs` — one plugin implementing `IToolPlugin`,
  `ITuiPanelPlugin` and `IAgentPlugin` at once. The panel half names
  `Harbor.Ui.Framework.Panels` types, which resolve through the declared
  contract tier (`Harbor.Ui.Framework.State` by deployment-directory name).

## How to add a fixture

1. Drop a single self-contained `.cs` file in this directory with a unique
   plugin name, unique tool names, unique class names (prefix `Rw`), and a
   one-paragraph comment stating what it exercises.
2. Keep every type the file needs inside the file; import only namespaces from
   the declared contract surface plus `System.*` and
   `Microsoft.Extensions.Logging`.
3. Load it in `RealWorldPluginsTests` via `LoadAsync`, following the
   `SessionBroadcastPluginTests` pattern (fresh temp `HOME` per load, assert an
   observable effect: registered tool name, executed tool result, registered
   panel id or agent name).
4. Run the suite before pushing:

```bash
dotnet run --project tests/Harbor.Plugins.Runtime.Tests -c Release --no-build -- --minimum-expected-tests 1
```

## What a fixture may and may not do

- No network access.
- No writes outside the test-owned temp `HOME` (`PluginTestFixture`).
- No unbounded loops; no `Thread.Sleep`, no blocking waits — `await` async work.
- No `Environment.SetEnvironmentVariable` (process-wide state breaks parallel
  tests).
- No `async void`, no fire-and-forget discards, no `.Result` / `.Wait()`.
- One top-level plugin file per fixture: `FileSystemPluginSource` discovers
  top-level `*.cs` only and each file compiles in isolation, so a fixture split
  across files or subdirectories does not load (tracked separately, not fixed
  here).

## See also

- [Plugin development guide](../../docs/PLUGIN_DEVELOPMENT.md)
- [Plugin tests README](../Harbor.Plugins.Runtime.Tests/README.md)
