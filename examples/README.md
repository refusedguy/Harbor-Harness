# Runnable examples

This index points at examples you can run from a checkout. Each row names the
source, the exact command, and what appears when it runs. The four demo scenes
boot an in-process mock LLM, so they need no API key and make no network calls.
The CS plugin example compiles at startup through Roslyn and needs the host to
be built first.

Build the CLI once before running any command below:

```bash
dotnet build apps/Harbor.App.Cli -c Release
```

## Scenes and samples

| Example | Source | Command | What you see |
|---|---|---|---|
| Hero (CellForge) | [demo/hero.tape](../demo/hero.tape) | `dotnet run --project apps/Harbor.App.Cli -- --demo --scene hero --tui cellforge` | A scripted agent turn in the fullscreen cell-diff renderer. This is the lead GIF at the top of the README. |
| Markdown (CellForge) | [demo/markdown.tape](../demo/markdown.tape) | `dotnet run --project apps/Harbor.App.Cli -- --demo --scene markdown --tui cellforge` | Markdown rendering inside the same renderer. |
| Approval (CellForge) | [demo/approval.tape](../demo/approval.tape) | `dotnet run --project apps/Harbor.App.Cli -- --demo --scene approval --tui cellforge` | A tool approval prompt followed by its result. |
| Plain pipe | [demo/plain.tape](../demo/plain.tape) | `dotnet run --project apps/Harbor.App.Cli -- --demo --scene hero --tui plain` | The same hero turn as ANSI text on a plain pipe, the mode CI uses. |
| HelloWorld plugin | [samples/plugins-cs/HelloWorldPlugin.cs](../samples/plugins-cs/HelloWorldPlugin.cs) | Copy the file into `~/.harbor/plugins/`, then start Harbor | The file compiles at startup and registers a `hello` tool. |

The `--scene` flag accepts `hero`, `markdown`, `approval`, or `all`. The `--tui`
flag accepts `ansi`, `plain`, or `cellforge`. Both flags are documented in the
CLI help text and in [DemoCommand.cs](../apps/Harbor.App.Cli/Commands/DemoCommand.cs).

## Recording the GIFs

The four tapes are recorded by the `demo-gifs` workflow through
[demo/run-scene.sh](../demo/run-scene.sh). Run one locally with
`vhs demo/hero.tape` once the CLI is built. The wrapper unsets every API key,
pins the locale, and scrubs nondeterministic output so two runs produce the same
frames. Read [DEMO_GIFS.md](../docs/DEMO_GIFS.md) before changing a tape or a
committed GIF.

## Where to go next

- [EXAMPLES.md](../docs/EXAMPLES.md) holds 40-plus short recipes.
- [PLUGIN_DEVELOPMENT.md](../docs/PLUGIN_DEVELOPMENT.md) walks through five complete plugins.
- [GETTING_STARTED.md](../docs/GETTING_STARTED.md) covers install and first run.
- [TOOLS_CATALOG.md](../docs/TOOLS_CATALOG.md) lists every builtin tool with examples.
