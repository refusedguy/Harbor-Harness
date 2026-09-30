# PATTERNS.md — the de-facto convention, and where it actually lives

> Status: normative for the current implementation. Every `file:line` below is
> cited against this branch. If code and doc disagree, code wins and the doc must
> be updated in the same PR. This is a working checklist, not a dated record —
> the "How to use this document" table below is read before designing anything,
> and four architecture guards quote these sections in their own failure
> messages, so a number that has rotted sends a reader to the wrong code. The
> `#826` gate fences every citation on this page
> (`tools/check-doc-cites.py`, rule `DOC-CITE-MISSING` / `DOC-CITE-EOF`), which
> is why the declaration is here and not just implied by the prose above.
>
> What the fence does **not** do is grade the *meaning* of a line — it proves a
> line exists, not that it is the right one. #868 is the worked example: eight of
> §8's ten pointers into `ChatScreenBridge.cs` named a line that exists and holds
> something else. Cite the symbol next to the number where a section argues from
> one.

> **Read this before you add a feature.** This is not the GoF catalog. It is the
> set of patterns Harbor *actually* uses, each one pinned to the line of code
> that proves it, plus the trap you are about to fall into.
>
> The distinction matters because of one measured fact about this repo:
>
> > The repo is conventional in the composition root (`Harbor.Hosting/Modules`)
> > and in UI-state (`Harbor.Ui.Framework.State`), but only selectively in the
> > layers AGENTS.md points at as canonical (`src/Harbor.Tui.CellForge/`, the
> > REPL, plugins). **A new feature comes out wrong not because the pattern is
> > absent, but because the canonical code path is not the place where the
> > pattern is applied.**
>
> So a section below is only useful if it tells you which path to copy. Each one
> answers three questions: **the convention**, **the canonical implementation
> (`file:line`)**, and **the known trap**.

Связанные документы:
- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — 38 вещей, которые мы НЕ делаем.
- [CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — известные нарушения.
- [ARCHITECTURE.md](./ARCHITECTURE.md) — high-level дизайн.
- [EVENT_BUS_SINKS.md](./EVENT_BUS_SINKS.md) — нормативная таблица sink-вердиктов.
- Guards: `tests/Harbor.Architecture.Tests/` — каждый раздел ниже ссылается на
  тест, который держит его.

---

## How to use this document

| If you are adding… | Copy this | Guard |
|---|---|---|
| a TUI renderer, or event handling in one | §1 Observer | `RendererEventSeamRule` |
| a default member on an interface | §6 DIM | `DefaultInterfaceMemberRule` |
| a `switch` over an event/role union | §7 Type unions | `ExhaustiveUnionSwitchRule` |
| a pluggable backend selected by id | §2 Factory / registry-by-id | — (shape is self-evident; see §2) |
| a cross-cutting concern (metrics, tracing, sandboxing) | §3 Decorator | — |
| a REPL slash command | §4 Command | — |
| a plugin extension point | §5 Extension points | — |
| a stateful projection of the event stream | §8 State + lifetime | — |

**The freeze (#555) is why the guard column exists.** Per #555, guards land
*before or with* the rewrite, and a refactor without a guard gets undone by the
first new PR. If you touch a section, run its guard.

---

## 1. Observer — with sink verdicts, and ONE seam into the UI

**This is the most finished pattern in the repo. Model the others on it.**

### The convention

Every subscriber declares what it costs to be skipped, and the bus reads that
declaration **once, at composition time**. An event enters the UI through
exactly one of two declared seams.

### The canonical implementation

`src/Harbor.Abstractions/Events/IEventBusMiddleware.cs:63`:

```csharp
EventBusSinkKind SinkKind => EventBusSinkKind.Mandatory;
```

Read the doc comment above it (`:51-61`) before you add a sink — it documents
the **DIM-forwarder trap**, which is the single most instructive defect in this
repo and is stated in full in §6.

The composition-time decision, `src/Harbor.Registries/Events/InMemoryEventBus.cs:318-321`:

```csharp
// #47/S3: the mandatory set is computed ONCE, from each sink's own
// declared verdict, right here at composition time. Nothing on the
// publish path re-derives it, and no sink type is special-cased by
// name — a host that registers a mandatory sink keeps the full path
// without anyone editing this class.
IReadOnlyList<IEventBusMiddleware> sinks = _middlewares;
for (int i = 0; i < sinks.Count; i++)
{
    if (sinks[i].SinkKind == EventBusSinkKind.Mandatory)
    {
        _hasMandatorySink = true;
        break;
    }
}
```

The normative table is [`docs/EVENT_BUS_SINKS.md`](./EVENT_BUS_SINKS.md) §2-§4
(registration sites, subscriber sites, the guard term by term). The DIM is pinned
by `tests/Harbor.Core.Tests/EventBusSinkVerdictTests.cs:309-330`.

### The two UI seams — state this to yourself before writing a renderer

1. **Store seam (the convention).** An `AgentEvent` enters UI state through
   `ChatAppMsg.Agent` → `ChatAppReducer` → `UiStore`, and a renderer reads the
   result **as data**. Canonical:
   `src/Harbor.Tui.CellForge/Chat/CellForgeTuiRenderer.cs:314`
   (`_ = ActiveStore.Dispatch(new ChatAppMsg.Agent(@event));`).
2. **Handler seam (for imperative side effects).** A renderer that must act on an
   event outside the state fold registers an `IAgentEventHandler` via
   `RegisterHandler` (`src/Harbor.Terminal.Abstractions/BaseTuiRenderer.cs:238`;
   the contract is `Renderers/AgentEventHandler.cs:12-21`, whose doc comment
   states the intent outright: "instead of duplicating a per-renderer
   `switch (AgentEvent)`"). Canonical:
   `src/Harbor.Tui.AnsiPlain/AnsiPlainTuiRenderer.cs:85-88`.

AnsiPlain does both (`:74` dispatches to the store, `:79` runs the handlers) —
that is fine and is not a third mechanism. Three renderers reach the store seam
one hop away, through a `*TeaBridge` field
(`contrib/tui/Harbor.Tui.TerminalGui/TerminalGuiRenderer.cs:36` →
`TerminalGuiTeaBridge.cs:56`, and the Termina/RazorConsole pair) — the guard
follows that hop, because the bridge dispatches into the same `UiStore`.

### The known trap (#575)

There were **three** mechanisms for one event stream: 21 `IAgentEventHandler`
objects across 5 renderer families, one hand-written 21-arm
`switch (AgentEvent)` in `ChatScreenBridge`, and 5 renderers routing through
`UiStore.Dispatch` + `ChatAppReducer`.

`ChatScreenBridge.HandleEvent` (`src/Harbor.Tui.CellForge/Chat/Streaming/ChatScreenBridge.cs:127`,
15 outer arms at `:131-390` plus a nested `switch (update.LlmEvent)` with 6 more
at `:174-189`) receives events through its **own** subscription
(`Chat/Streaming/EventSubscription.cs:18`), not the base class's dispatcher, and
is registered as a **singleton** with `autoSubscribe: false`
(`apps/Harbor.App.Cli/Hosting/CellForgeModule.cs:101-106`).

> The counts in this paragraph were `18` / `:129-314` / `4` until #840 touched
> the arm set and forced a recount — 18 was 14 outer + 4 nested, i.e. the total,
> reported as if it were the outer count, and the range had already drifted.
> Corrected here because a number this paragraph edits should at least end up
> true; the neighbouring stale pointers are tracked separately.

The consequence is the whole point of #575: **a new `AgentEvent` subclass was
invisible to `BaseTuiRenderer`, and the next person added a `case` to a
621-line bridge.** That is exactly what happened for `CompactionFailedEvent` —
present in the conformance sweep at
`tests/Harbor.Tui.RendererTests/RendererVisitorRegressionTests.cs:63`, therefore
*required* of the 5 conforming renderers, and **absent** from `ChatScreenBridge`'s
arms until #840 added the arm. A third classification of the same taxonomy also
lives in the base class:
`ShouldRenderPlacement` (`BaseTuiRenderer.cs:289-309`) re-derives "which event
repaints which placement" as `placement switch { … @event is … }`, so adding one
event type needed three edits in three shapes — or zero, if you only knew about
the mechanism your own renderer used.

> **Visitor is NOT the convention here.** The repo's best implementation is the
> store + reducer (`Harbor.Ui.Framework.State` is the model), not a visitor.
> Do not go looking for a `Visitor` to copy.

**Guard:** `tests/Harbor.Architecture.Tests/RendererEventSeamRule.cs` — every
concrete `BaseTuiRenderer` must reach one of the two seams, verified over **IL**
(not source text, which cannot see the one-hop bridge case or partial classes).
Empty baseline: all 10 families already conform. The guard's own positive control
is two renderers declared in the test file — one with a seam, one without — so
"the rule can fail" is a runtime fact, not a claim.

---

## 2. Factory / registry-by-id — three identical siblings

**The convention.** A pluggable backend selected by an id is a **strategy class
plus a frozen index**, not a `switch`. The three registries in
`src/Harbor.Hosting/Modules/` have the same shape on purpose:

| Registry | id table | `Build()` | `Resolve` |
|---|---|---|---|
| `TuiBackendRegistry.cs` | `FallbackBackendId` `:228`/`:231` | `:235` | falls back to that single id `:302` |
| `SessionStoreRegistry.cs` | **none** — the id set is `Build()`'s own keys | `:108` | `:158-163` |
| `HarborModeRegistry.cs` | **none** — the id set is `Build()`'s own keys | `:80` | `:98-103` |

Every one of them is:

```csharp
internal static FrozenDictionary<string, TStrategy> Build();  // built once, frozen;
                                                            // its Keys ARE the id list
internal static Maybe<TStrategy> Resolve(FrozenDictionary<string, TStrategy> registry,
                                         string rawId);    // unknown id → None
```

> `KnownIds` is **gone**, and its absence is the point. Both
> `SessionStoreRegistry` and `HarborModeRegistry` used to carry a second
> hand-written copy of their own key set next to the factory set, on the strength
> of a doc comment claiming adjacency prevented drift. #581 deleted both: "there
> is no second list left to drift; the registry below is the only declaration"
> (`SessionStoreRegistry.cs:20-23`). A table of pointers is also a hand-maintained
> list — which is §7 rule 3, one section up.

`Build()` returns a `FrozenDictionary` — no per-lookup allocation, no lock.
`Resolve` returns `Maybe.None` for an unknown id and the **caller fails loudly**:
`SessionStoreRegistry`'s own header says the silent fallback "is gone: unknown
ids fail fast" (`SessionStoreRegistry.cs:11`).

Provider factories follow the same shape without the id index, because a
provider is looked up by a strong id rather than a raw string:
`src/Harbor.Hosting/Modules/ProviderFactories.cs:76`, `:94`, `:114`, `:136` —
each `IProviderFactory` with `ProviderId` and `CreateClient(ILoggerFactory)`.

### The known trap

`TuiBackendRegistry` is the one exception, and it is a *deliberate* one: unknown
`HARBOR_TUI` falls back to a single id — `"ansi"` when Spectre is compiled in,
`"plain"` otherwise (`TuiBackendRegistry.cs:226-232`) — and `TuiModule` logs a
warning naming the requested id and every available one
(`TuiModule.cs:50-54`). A fallback is acceptable **only when it is loud and
single**. If you add a fourth registry, copy `SessionStoreRegistry`, not
`TuiBackendRegistry` — the difference is whether a typo is silent.

---

## 3. Decorator — for cross-cutting concerns only

**The convention.** Cross-cutting policy is a decorator registered **at the DI
boundary**, wrapping the concrete instance. Three sites, same shape:

- `src/Harbor.Hosting/Modules/RegistriesModule.cs:115-121`:
  ```csharp
  // sprint3-C C1: instrument at the DI boundary. Plugins keep mutating the
  // RAW registries (ctx.Registries) before Freeze; consumers resolving the
  // interfaces get the instrumented views.
  services.AddSingleton<IToolRegistry>(new InstrumentedToolRegistry(
      toolRegistry, MeterMetrics.Instance, ActivityTracer.Instance));
  ```
- `src/Harbor.Hosting/Modules/CoreModule.cs:83-86`: `IAgent` → `TracingAgentProxy`
  over the real `DefaultAgent`.
- `src/Harbor.Hosting/Modules/TelemetryModule.cs:8-15`: the doc comment states
  the rule — the decorators wrap in `AddHarborRegistries`/`AddHarborCore`, "one
  chain, no per-app wiring".

### The known trap

The decorator is at the **container**, so plugins keep mutating the raw registry
and consumers see the decorated one. If you wrap earlier, a plugin's
contribution silently misses the policy. And note `TelemetryModule.cs:25-33`:
the reporter is only registered when the bus implements `IEventBusQueueMetrics`
— "a bus that is not instrumented is simply not registered", so the metrics
surface carries **nothing** for the event bus instead of a fake zero. Prefer an
absent surface to a plausible lie.

---

## 4. Command — the REPL slash palette

**The convention.** A slash command is an object, not a `switch` arm.
Contract: `apps/Harbor.App.Cli/Repl/Commands/IReplCommand.cs:8-16` — `Id`,
`Aliases`, `Title`, `Description`, `Group`, `ExecuteAsync`. Registry:
`ReplCommandCatalog.cs:12-33` — an `OrdinalIgnoreCase` dictionary, one
registration point, and `GetAll()` returning `Distinct()` so alias expansion does
not duplicate a command.

18 implementations live beside it. The catalog's own doc comment
(`:1-8`) says what it replaced: "two mirrored 12-branch switches in
CellForgeReplRunner (ExecutePaletteCommandAsync/SubmitAsync)".

**AOT-friendly by construction**: plain `Task`, no reflection, no
`Spectre.Console.Cli`. `IReplCommand` is `internal` — deliberately; a plugin
command is a different pattern (see `docs/PLUGIN_DEVELOPMENT.md`).

### The known trap

The mirrored-switch failure mode is back again in a second place: if you add a
command, register it in `CreateDefault` **only**. There is no second switch to
keep in step — that is the entire point of the catalog.

---

## 5. Extension points — an interface plus a *list* of interfaces

**The convention, stated for one paragraph because there is no rule today:**

> An extension point is **an interface + a registry that holds a list of it**.
> Never mutate a concrete registry from an adapter.

### What exists

`src/Harbor.Abstractions/Tools/IToolSource.cs:6` — `{ GetAllTools, ResolveTools, GetTool }`.
Composition: `src/Harbor.Registries/Tools/CompositeToolRegistry.cs:6` holds
`List<IToolSource> _sources`, folds first-success in `GetTool` (`:97`), and
makes `Register`/`Unregister` **read-only** (`:119,121`, each returning an
explanatory `Result.Failure`). Two internal sources exist:
`ToolRegistry.cs:157` (`ConcurrentToolSource`, a `private sealed class` nested in
`ToolRegistry` itself, unfrozen path) and
`FrozenToolView.cs:26` (frozen path). The "single read path" rationale is
documented at `ToolRegistry.cs:26-29` — including the sentence that states the
intent: "A third source (e.g. lazy-loaded plugin tools) plugs in as another
`IToolSource` (or a composite) without touching GetAllTools / ResolveTools /
GetTool." That sentence describes a caller that does not exist.

This is a correct Composite.

<!-- check-doc-cites: allow-unwired ConcurrentToolSource - private nested class, constructed and used only inside ToolRegistry.cs:48, so DOC-TYPE-UNWIRED's one-declaring-file heuristic cannot see the wiring -->

### The known trap (#577)

**`CompositeToolRegistry.AddSource` has exactly one caller in the repository, and
it is a test.** No production code constructs a `CompositeToolRegistry`, so the
abstraction exists to satisfy code nobody composes in anger:

```
tests/Harbor.Registries.Tests/ToolRegistrySnapshotTests.cs:237-238
```

The two sibling registries have **no Composite at all**:
`src/Harbor.Registries/Providers/ProviderRegistry.cs:16` and
`src/Harbor.Registries/Agents/AgentRegistry.cs:8` are `ConcurrentDictionary` plus
a `Builder`. There is no `IProviderSource` / `IAgentSource`, so a second source of
providers cannot exist without editing the registry class. Both files carry the
same self-description ("Implements Registry pattern (GOF)") without the composite
step the tools registry took.

So the real extension path is **Adapter + direct mutation**, three parallel
copies, in `src/Harbor.Plugins.Registration/PluginRegistrar.cs:75-87`:

| plugin role | adapter | reaches |
|---|---|---|
| `IToolPlugin` | `ToolRegistryBuilderAdapter` (`:202`), `AddTool` at `:227` wraps the tool in `SandboxedPluginTool` (`:231`, a Decorator) then calls `_host.RegisterTool` (`:238`) | `IPluginLoadHost.RegisterTool` (`IPluginLoadHost.cs:67`) |
| `IProviderPlugin` | `ProviderRegistryBuilderAdapter` (`:249`) | `IPluginLoadHost.RegisterProvider` (`:76`), called at `:279,290,297` |
| `IAgentPlugin` | `AgentRegistryBuilderAdapter` (`:314`) | `IPluginLoadHost.RegisterAgent` (`:83`), called at `:327` |

Two consequences worth stating plainly:

- The sandbox Decorator is **adapter-local**. The tool path gets a 30 s timeout +
  10 MB allocation guard + `PluginBlockedEvent`; the provider and agent paths get
  **nothing equivalent** — no timeout, no capability enforcement, no audit. Policy
  bolted onto an adapter is policy one plugin role does not have.
- `docs/PLUGIN_DEVELOPMENT.md` teaches `IToolPlugin` as *the* way to add a tool,
  which silently teaches Adapter-not-Composite and gives no hint that providers
  and agents work differently.

**What to do:** adding a new extension point, add the `I*Source` interface and a
`Composite*Registry` that holds a `List<I*Source>`, and have `PluginRegistrar`
contribute one source per plugin. Do not add a fourth builder adapter.

---

## 6. Default interface members — a default must fail towards *loudly wrong*

**The convention, in one sentence:**

> A default interface member must fail towards **"loudly wrong"**, never towards
> **"quietly right"**. Conservative defaults are allowed — `false`, `Mandatory`,
> a deliberate no-op. Permissive defaults are banned — `Result.Success()`,
> `Task.CompletedTask` where a callback was requested, and delegating to an
> overload that loses the error.

### The canonical, blessed shape

`IEventBusMiddleware.cs:63` (`SinkKind => Mandatory`) is the model: the default
is the direction in which **nothing silently goes wrong**, and the verdict table
is normative rather than advisory. Also blessed, all pointing the same way:

- `src/Harbor.Terminal.Abstractions/Views/ITuiView.cs:29` — `HandleKey => false`
  (the key falls through).
- `src/Harbor.Tui.CellForge.Engine/Rendering/OverlayStack.cs:40,46` —
  `IsModal => false`, `OnKey => false` (observe-only layer).
- `src/Harbor.Ui.Framework.Rendering/Widgets/ChatBlock.cs:161` —
  `TryHitHeader => false` (an unpainted block never claims a click).

### The known trap (#579) — the DIM-forwarder, live

`src/Harbor.Ui.Framework.Services/Services/IThemeWatcher.cs:31`:

```csharp
public IDisposable Watch(string path, Action<string>? onError) => Watch(path);
```

Structurally identical to the defect #496 fixed on `SinkKind`: a *new* member
added as a DIM whose default routes to the *older, information-losing* overload.
An implementation written before `:31` existed compiles, satisfies the new
contract, and silently swallows every watcher error the caller explicitly asked
to see. `IThemeWatcher` is precisely the role that carries `JsonThemeLoader`'s
parse/IO failures.

Two more live instances of the banned direction:

- `src/Harbor.Abstractions/Tools/ITool.cs:124` — `ValidateArguments(args) =>
  Result.Success()` accepts any argument shape. Currently harmless **only**
  because all 22 in-tree `ITool` implementations override it (`grep -rnE "class
  [A-Za-z0-9_]+ *: *[A-Za-z0-9_.]*ITool" src/`, 22 distinct types, none in a file
  without a `ValidateArguments`) — which is exactly why it is safe to delete
  rather than reason about. §9 cited this same member at `:74`, which is a
  doc-comment line on `SafetyProfile`; two numbers for one claim in one document,
  and neither was right.
- `src/Harbor.Terminal.Abstractions/Views/ITuiView.cs:32` —
  `OnEventAsync(...) => Task.CompletedTask`, and the hook has **zero callers**.
  `BaseTuiRenderer` only calls `view.RenderAsync`
  (`BaseTuiRenderer.cs:318-330`). Key and event handling moved to the reducer
  (`AppMsg.KeyInput` via `Ui.Framework.State/State/KeyEventAdapter.cs:23`,
  `ChatAppMsg.Agent` via `CellForgeTuiRenderer.cs:314`), but the old hooks stayed
  on the interface with a no-op default, so the compiler will not tell anyone
  they are dead. A plugin author following `ITuiPlugin`'s `RegisterTui`
  (`Terminal.Abstractions/Plugins/ITuiPlugin.cs:124`, and note `:11`: that whole
  seam is closed as of #564) implements `OnEventAsync`, sees no exception, and
  ships a view that never updates.

### The rule for your own DIM

Ask one question: **if an implementer omits this, what happens?**

- *Nothing goes wrong, and the omission is visible* → allowed. (`=> false`,
  `=> Mandatory`, an identity fold, a `Toggle`-style exact derivation.)
- *Something goes wrong and nobody finds out* → banned. (`=> Success()`,
  `=> Task.CompletedTask` on a hook that was supposed to report, `=> OlderOverload(...)`.)

If you need a hook that is genuinely optional **and** a default that is genuinely
safe, the two are separate members — as `IEventBusMiddleware` does with `Name`
(abstract) and `SinkKind` (conservative default).

**Guard:** `tests/Harbor.Architecture.Tests/DefaultInterfaceMemberRule.cs` — a
reflection whitelist over `Harbor.Abstractions*`, `Harbor.Terminal.Abstractions`
and `Harbor.Ui.Framework.*`. Every default must be on the reviewed list **and**
fail in the direction the list claims; the direction is re-derived from the
signature, so the annotation is a claim the test checks rather than a comment. The
classifier's four permissive shapes are: a richer default with a poorer overload
of the same name (the forwarder), returns `Result`, returns `Task` with a
`Handle`/`On`/`Run` name, and — by contrast — everything else is conservative.

---

## 7. Type unions — a `switch` is a compile error when the union grows

**The convention** (from #578, which is the synthesis of five audits):

1. A `switch` over a sealed type union has **no `default`/wildcard arm**. Every
   arm is named, so adding a member to the union is a compile error at every
   site. That is the point.
2. Where a default is genuinely required — forward compatibility with input from
   *outside the process* — it must **not be silent**: it logs **and** increments an
   observable counter. A default that neither logs nor counts is a bug with a
   comment.
3. **A hand-maintained list of names is a union** and gets the same treatment.
4. Cover the union with a test **driven by reflection**, so the compiler's
   exhaustiveness is backed by a runtime check. A hand-written list of test cases
   is NOT this — it ages exactly like the copy it replaced.

### Why rule 1 exists — the failure is not divergence, it is that adding a value is *allowed to compile*

A copy does not break itself. It ages silently until somebody adds a variant, and
then the default arm invents an answer. Every instance in #578 is the default arm:

| instance | how it drifts |
|---|---|
| #495 | two `AgentEvent → HarborEvent` switches whose `_ => null` **silently dropped** a new event type on one host — *fixed on `dev`; kept here because it is the worked example of the rule, and because the guard's ratchet had four rows deleted when the fix landed* |
| #556 | `ChatRole → (label, markdown?)` written 4×, all four `_ =>` arms **silently relabelling** a new role |
| #567 | tool-call lifecycle as three enums, all `_ =>` render a new state as `running` — a cancelled call spins forever |
| #553 | `Rect.Width < 2` guard lost in 2 of 7 copies; corners drawn **outside** the requested rect |
| #557 | the tool-name list behind the path guard — `PathArgExtractionPolicy.DefaultTools` (`PathArgExtractionPolicy.cs:53`), a hand-rolled set rather than a union; omitting a path-taking write-tool means `new("mytool","src/*",Allow)` authorises `src/../../../etc/passwd`. #557's fix was to make `ITool.SafetyProfile` a required member and derive the set from it, so what is cited here today is the derived fallback, not the literal that caused it |

### The wire unions, which are the hand-maintained lists

- `src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs:9-25` — 17
  `[JsonDerivedType]` entries for the `AgentEvent` record union; the nested
  `LlmEvent` union has 13 at `:210-222` (immediately above
  `public abstract record LlmEvent;` at `:223` — the tags are on the *members*,
  so the block that counts them is the one above the declaration, not the one
  below). Declared in **`Harbor.Abstractions.Events`**,
  not `Harbor.Abstractions.Contracts.Events` — the *project* is
  `Harbor.Abstractions.Contracts`, the *namespace* is not under it. Read the
  `namespace` line; do not infer it from the folder.
- `src/Harbor.Abstractions.Contracts/Models/ChatLine.cs` — `ChatRole`, 7 members,
  in **`Harbor.Abstractions.Models`** (same folder-vs-namespace gap).
- `src/Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs:15-25` — 11 MessagePack
  `[Union(n, typeof(T))]` tags whose doc comment (`:11`) says they "MUST match
  the `HarborEventKind` enum values exactly". Here the namespace **does** match
  the project (`Harbor.Ipc.Protocol`), which is exactly why the previous bullet
  is worth writing down.
- `src/Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs:35-49` — 15 MessagePack
  `[Union(n, typeof(T))]` tags, the request union the guard grades as
  `HarborRequest` since #485. Unlike the three above, the guard does **not** read
  its members off the tags: `MessagePack.UnionAttribute` exposes only `Key` as a
  public member in 3.1.x, so a tag census yields integers and no names. It reads
  the type system instead — which is the stronger source anyway, because it also
  names a subtype added *without* a tag, and tag coverage is already owned by
  MessagePack's analyzer plus `ProtocolSerializationTests` (which round-trips
  every subtype through the abstract base, so an untagged member fails there).

These are exactly the lists rule 3 is about, and rule 4 is what keeps them honest:
the guard reads the member set **by reflection** rather than from a typed-out
copy.

### The known trap, and the sequencing

Issue #578 is explicit: *"Do NOT try to land one giant enforcement PR. Add the
guard in the same PR as the refactor of each union."* The ratchet opened at 17
rows and stands at **7** — `WildcardBaseline` in the guard is the number, not
this sentence, and every deletion from it is a fix that landed. Landing the rule
bare would be a permanently red build; landing it as a ratchet means **the set
may shrink but never grow**, and each existing row names the issue that owns
removing it.

The ratchet is not decorative. It already fired once: the two IPC projections
were four rows, #495 was fixed on `dev` while this document was being written,
and `WildcardBaseline_IsLive` failed the moment the rows outlived their
violations. Deleting a fixed row is the whole workflow, and a row that has
quietly stopped matching is a build failure rather than a stale amnesty.

**One thing this guard does NOT catch**, and you should know before relying on
it: a *missing* arm with no default. `ChatScreenBridge.HandleEvent` has no
wildcard arm, and it missed `CompactionFailedEvent` for the whole life of the
event — it fell through the switch and nothing happened, silently. That is the
one member of the lifecycle the arm census in
`CompactionLifecycleLineTests.AnsiPlain_EveryCompactionMember_NarratesItself` is
there to keep true, because as of #840 (PR #859) the arm exists and nothing
mechanically would put it back. A wildcard arm invents an answer; a missing arm
simply does not. The exhaustiveness half (#578 rule 1 done properly)
is the per-union reflection test, and per #578 it lands with each union's
refactor.

**The first one to land it is `HarborRequest` (#485).** That union's refactor did
not produce a switch with every arm named — it produced no switch at all:

| piece | where | what it guarantees |
|---|---|---|
| the census | `src/Harbor.Ipc.Server/Protocol/HarborRequestTypes.cs` | the union's member set, by reflection over `typeof(HarborRequest).Assembly` |
| the composition boundary | `RequestDispatcher`'s constructor | **throws**, naming the missing types, if a member has no handler — a new request type without one is a startup failure |
| the upstream exception | `HarborRequestTypes.HandledBeforeDispatch` | `PskAuthRequest` is consumed by `MessagePackRpcServer.ApplyPskGateAsync` before dispatch, and is **named** rather than special-cased, so a second such seam must be declared too |
| the wire boundary | `RequestDispatcher.Unhandled` | a request outside the tagged union is logged, counted on `UnhandledRequestCount`, and refused with `NO_HANDLER:<Type>: …` echoing the request id — rule 2, not silence |
| the table test | `tests/Harbor.Ipc.Tests/RequestHandlerCoverageTests.cs` | 15 members = 14 handlers + 1 upstream, asserted rather than assumed, plus both boundaries exercised |

The lesson generalises past IPC. For a union that crosses a **process**
boundary, a wildcard arm is the smaller half of the problem: the check that
actually matters is at composition time, because a member with no handler is
otherwise indistinguishable from a member with the wrong handler. Enumerate the
union by reflection and **refuse to start** when coverage is incomplete.

**Guard:** `tests/Harbor.Architecture.Tests/ExhaustiveUnionSwitchRule.cs` — the
union census comes from reflection, the wildcard-arm scan runs over
`src/`, `apps/`, `contrib/tui/`, `contrib/apps/`, and the current set must be a
subset of the baseline — **7 rows** (`WildcardBaseline`, down from 17; read the
dictionary, do not read this sentence, it is the number that goes stale). The
scan is deliberately conservative: a switch must
name at least 2 distinct members of a registered union before it counts as "a
switch over that union", so unrelated switches are never graded for
exhaustiveness they were not claiming.

---

## 8. State — the store + reducer, and the lifetime contract it implies

**The convention.** UI state is an immutable record; transitions are pure
functions of `(state, message)`; the store owns the revision ledger.

`src/Harbor.Ui.Framework.State/` is the reference implementation and it is
consistent throughout:

| role | site |
|---|---|
| state | `State/UiState.cs` (immutable record) |
| messages | `State/AppMsg.cs`, `State/ChatAppMsg.cs` |
| transitions | `State/AppReducer.cs`, `State/ChatAppReducer.cs` |
| store / lifecycle | `State/UiStore.cs:175` `Dispatch` → `:211` `Notify`, with a revision ledger and a stale-drop guard |
| consumption | `CellForgeTuiRenderer.cs:314` dispatch, `:290` `PumpProjection()` — the renderer reads the fold **as data** |

The split is deliberate: `AppReducer` is domain-free (panels, scroll, input,
focus, quit) and the chat half plugs in through `IAppReducerPlugin`, so a
non-chat host uses the generic reducer alone.

### The known trap (#576): two State objects for one session, and no lifetime contract

`src/Harbor.Tui.CellForge/Chat/Streaming/ChatScreenBridge.cs` is a **second,
parallel state machine for the same `AgentEvent` stream** — the same fact ("what
is on screen for the current session") with two owners:

- the state, as fields: `:41-61` `_panel`, `_status`, `_streams`, `_cards`,
  `_context`, `_gates`, `_displayedMessageIds`; plus `:59` `_toolRetryShown`,
  `:68` `_parentSessionId`, `:72` `_runHadError`, `:74` `_errorCardSeq` —
  **~11 pieces of mutable state**;
- the transition: `HandleEvent` at `:127`, 21 arms mutating those fields directly;
- the lifetime: `apps/Harbor.App.Cli/Hosting/CellForgeModule.cs:101` registers it
  `AddSingleton`, over a per-process `ChatScreen` (`:77-79`, also a singleton).

Meanwhile the reducer side *is* per session: `CellForgeTuiRenderer.cs:226`
(`ActiveStore => _sessions?.ActiveContext?.Store ?? _store`) and `:236-258`
(`EnsureSubscribedToActiveStore`) re-bind on switch.

**The module's own doc comment claims the opposite** — `CellForgeModule.cs:32`
says «одно состояние экрана на сессию» ("one screen state per session") — which
the registration two lines below does not deliver. **A doc comment promising a
lifetime the container does not provide is a defect**, and that is the rule.

The concrete cost, all visible in the current tree:

1. `SessionChangedEvent` — *the* "new session" transition — is handled at
   `ChatScreenBridge.cs:339-343` (`case SessionChangedEvent changed:`) by
   assigning **one** field (`_parentSessionId`).
   `_runHadError`, `_errorCardSeq`, `_toolRetryShown`, `_displayedMessageIds` and
   everything inside `_cards` / `_streams` / `_gates` survive the switch.
2. The cleanup that does exist lives in a **caller**:
   `apps/Harbor.App.Cli/Repl/SessionSwitchManager.cs:245-247`
   (`host.Bridge.ResetMessageTracking(); host.Timeline.Clear(); host.Selection.Clear();`)
   — the contract is "whoever switches sessions must remember to poke three
   internals", enforced by nothing and documented nowhere in the interface. The
   one method that comes close, `ResetMessageTracking()`
   (`ChatScreenBridge.cs:494`), covers exactly one of ~11 fields, and `MarkSeen`'s
   own doc comment (`:500-501`) admits the coupling: "`ResetMessageTracking`
   re-arms on session switch/new session, where the timeline is cleared
   alongside."
3. `Dispose()` (`:722`) releases only the bus subscription, so a renderer swap
   mid-session leaves every accumulated field in place for the next subscriber.

Every number in this list was off when #868 measured it — the field block, both
caller citations, the method and the doc comment. They are named with the symbol
they point at for that reason: a `Dispose()` at `:617` is a line 87 lines above
the `Dispose` the sentence is about, and it still *exists*, so nothing but a
reader notices.

**The rule:** *if a type holds session-derived mutable state, it needs a
`Reset`/`ResetTo(sessionId)` — and the "new session" transition must call it, so
the transition and the lifecycle are the same code path rather than two.*

---

## 9. Anti-convention summary — read before you design

| Anti-convention | Where it lives today | Rule |
|---|---|---|
| Visitor as the UI event mechanism | `ChatScreenBridge.cs:127` (21-arm switch) | Use store + reducer (§1) |
| Third mechanism for one event stream | handler registry + switch + store, all at once | Pick one of the two seams (§1) |
| Lifecycle promised ≠ lifecycle provided | `CellForgeModule.cs:32` vs `:101` | Doc comment promising an unprovided lifetime is a defect (§8) |
| Cleanup as a caller convention | `SessionSwitchManager.cs:245-247` | The state object resets itself (§8) |
| Extension by mutating a concrete registry | `PluginRegistrar.cs:75-87` | Interface + a list of them (§5) |
| Policy bolted onto an adapter | `SandboxedPluginTool` wraps tools only (`PluginRegistrar.cs:231`); the provider and agent paths get none | Policy at the extension point (§5) |
| Permissive DIM | `IThemeWatcher.cs:31`, `ITool.cs:124`, `ITuiView.cs:32` | Fail towards loudly-wrong (§6) |
| Dead hook on a live interface | `ITuiView.cs:29,32` — zero callers | Zero callers ⇒ delete it (§6) |
| Wildcard arm over a Harbor union | 7 sites, baselined in the guard | Name every arm; or log **and** count (§7) |
| **Missing** arm, no default | was `ChatScreenBridge.HandleEvent` missing `CompactionFailedEvent` and falling through silently — **fixed in #840**; the census in `CompactionLifecycleLineTests` is what holds it | The default arm invents an answer; a missing arm just doesn't. Not covered by the wildcard guard (§7); covered by an arm census per family |
| Hand-maintained name list as a union | `PathArgExtractionPolicy.cs:53` (`DefaultTools`), `[JsonDerivedType]` tables | It IS a union — test it by reflection (§7) |
| Unknown id → silent default | fixed in `SessionStoreRegistry`/`HarborModeRegistry` | `Resolve` returns `Maybe.None`; caller fails loudly (§2) |
| A fake metric | see `TelemetryModule.cs:25-33` for the right shape | Absent surface beats plausible zero (§3) |

---

## See also

- [docs/ANTIPATTERNS.md](./ANTIPATTERNS.md) — the long form, 38 entries.
- [docs/CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md) — 45 known
  violations; §ARCH-001..§ARCH-NNN covers layering.
- [docs/EVENT_BUS_SINKS.md](./EVENT_BUS_SINKS.md) — the normative sink-verdict
  table referenced by §1.
- [docs/ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — the layering matrix
  that the guards in `Harbor.Architecture.Tests` also enforce.
- [docs/SPECTRE_TUI_DEEP_DIVE.md](./SPECTRE_TUI_DEEP_DIVE.md) — render-loop
  anatomy for the interactive shell.
- Issues: [#575](https://github.com/refusedguy/Harbor-Harness/issues/575) (§1),
  [#576](https://github.com/refusedguy/Harbor-Harness/issues/576) (§8),
  [#577](https://github.com/refusedguy/Harbor-Harness/issues/577) (§5),
  [#578](https://github.com/refusedguy/Harbor-Harness/issues/578) (§7),
  [#579](https://github.com/refusedguy/Harbor-Harness/issues/579) (§6),
  [#555](https://github.com/refusedguy/Harbor-Harness/issues/555) (the freeze
  that puts guards before rewrites).
