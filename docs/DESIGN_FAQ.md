# Design FAQ

This page answers the "why" questions that come up most often when changing
Harbor. Each answer points at the source that carries the reasoning, so the
FAQ is a map to the decisions, not a second copy of them. For the current
state of a subsystem, read the linked document rather than this summary. If a
decision here no longer matches the tree, open an issue: the linked source is
the place to correct.

## Why .NET 10 and NativeAOT instead of a Node or Bun harness?

The original comparison measured roughly 1 GB of idle memory in Bun-based
coding agents. Most of it came from a JS runtime, WASM grammars, SDKs and a
vector store, none of which Harbor needs for its pipeline. NativeAOT removes
the JIT and its code cache, and System.Text.Json source generation keeps the
hot path free of reflection. The core is published AOT-ready; the interactive
renderers and the Roslyn plugin loader stay on the JIT side because they need
it. See [spec 08](./specs/08-native-aot.md), [spec 01](./specs/01-architecture.md)
and [AOT publish](./AOT_PUBLISH.md).

## Why is storage JSONL-first when SQLite is available?

JSONL is append-only, human-readable and diffs well in git, which matters for
a session log that an agent writes on every turn. SQLite is not gone: it is
one of the three selectable backends behind `ISessionStore`, together with
memory and JSONL, chosen with `HARBOR_STORAGE`. The design rule is that the
default path stays portable and inspectable, and a heavier backend is an
explicit choice. See [architecture](./ARCHITECTURE.md) §9 and
[spec 05](./specs/05-sessions.md).

## Why do operations return `Result<T>` instead of throwing for expected failures?

A missing file, a bad argument or a denied permission is a normal outcome of
an agent loop, not an exception. The `Result` rail keeps those outcomes in the
type and lets callers compose them with `Bind`/`Map` instead of try/catch
ladders. The audit records the failure modes this replaced: masking an error
as null and calling `.Value` without checking `IsSuccess` are both crash
paths. See [code principles audit](./CODE_PRINCIPLES_AUDIT.md) and the
`Result<T>` section of [AGENTS.md](../AGENTS.md).

## Why are plugins compiled from `.cs` source, and why does that fail under AOT?

A dropped-in `.cs` file is the lowest-friction extension path: no project, no
build step, no package. The compiler is Roslyn, which needs a JIT, so this
path is JIT-only. Under NativeAOT the supported paths are DLL-based plugins
and out-of-process hosts. No collectible load context is used because it is
not supported under AOT. See
[plugin development](./PLUGIN_DEVELOPMENT.md) and the plugin notes in
[AGENTS.md](../AGENTS.md#what-not-to-do).

## Why is `ITuiPlugin` a closed seam?

`ITuiPlugin` is a closed seam (#564). The contract is still collected, but the
host never calls `RegisterTui`, so a view registered through it is never
painted. Keeping a dead extension point alive would be worse than removing it:
it teaches plugin authors a path that cannot work. A panel a plugin actually
contributes uses `ITuiPanelPlugin` and `IPanelRegistry` instead; a cell-level
widget is built in-tree by the renderer that owns the layout. See
[plugin development](./PLUGIN_DEVELOPMENT.md) and
[ADR-013](./adr/ADR-013-cellforge-widget-axis-internal-contract.md).

## Why do only two renderers ship, and why did SharpConsoleUI leave the product?

`cellforge` is the canonical fullscreen cell-diff renderer and `ansi`/`plain`
covers streaming output, pipes and CI. The SharpConsoleUI-based renderer was
vendored code with its own maintenance and release story, and keeping it in
the product meant carrying a second UI stack that CI did not build. It moved
to `contrib/`, is neither compiled nor published, and its old id falls back
to `plain`. The other renderers in `contrib/` are reference material behind a
build flag, not shipping backends. See [alternative UIs](./ALTERNATIVE_UIS.md)
and [ADR-019](./adr/ADR-019-vendored-sharpconsoleui-renderer-leaves-the-product.md).

## Why must TUI code avoid Harbor.Application and Harbor.Registries?

Agent state reaches a renderer only as events on the event bus. If a view
could call application services directly, rendering would depend on agent
internals and the future two-process split would need a second code path.
The rule is mechanical: a TUI assembly subscribes to typed events and keeps
its own view models. See [architecture](./ARCHITECTURE.md) §2 and
[architecture layers](./ARCHITECTURE_LAYERS.md).

## Why are checks split between Python gates and Roslyn analyzers?

A gate in `tools/` has to run where the SDK and the build are not: on a
docs-only pull request, or on the pull request that breaks the build. Those
gates are standard-library Python over files and artifacts. A check that needs
a type or a symbol belongs in the compilation instead, where the compiler can
see it. The policy and the table of every gate CI runs are kept honest by a
checker of their own. See [CI gates](./CI_GATES.md).

## Why are Newtonsoft.Json, EF Core and xUnit banned?

The first two do not fit an AOT publish: reflection-based JSON and an ORM
both defeat the constraints the core is built around. System.Text.Json with
source-generated contexts, Dapper or raw ADO.NET, and UTF8 readers on hot
paths are the replacements. xUnit is excluded because the repository
standardised on TUnit; adding a second runner would split discovery and CI.
See [what not to do](../AGENTS.md#what-not-to-do) and
[antipatterns](./ANTIPATTERNS.md).

## Why does a docs-only pull request have a separate workflow?

`ci.yml` deliberately ignores markdown and `docs/**`. A documentation change
therefore gets its own fast workflow that checks links and anchors, file
shape and stale facts, plus self-tests that prove each gate still fails on
broken input. Removing the ignore would make a typo wait behind a full build
and hide the fact that the docs gates are the only thing such a patch needs.
See [development](./DEVELOPMENT.md) §Documentation checks and
[CI gates](./CI_GATES.md).
