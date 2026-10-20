# Observed instrumentation — the `[Observed]` contract

> **Status (2026-10-20):** the contract behind issue #1797. This document describes
> the attribute that ships with it and the source generator that consumes it. The
> generator does not exist yet — it is the next PR — so nothing described here is
> live behaviour today. The attribute itself is inert: a declaration with no
> runtime effect.

## What `[Observed]` means

Harbor's telemetry is hand-written. Three decorator classes in
`Harbor.Telemetry.Core` cover five interfaces — `IToolRegistry`/`ITool`,
`IProviderRegistry`/`ILlmClient`, `IAgent` — and every other seam in the contract
layer is bare, because a hand-written decorator costs roughly the same seventy-odd
lines no matter which seam it wraps. "Instrument the whole codebase by hand" does
not pay for itself at that price.

`[Observed]` changes the price of a seam from *write a decorator* to *write one
line*. It is a declaration of intent, placed on the seam:

```csharp
[Observed("store.append_message")]
public interface ISessionStore
{
    // …
}
```

The mechanism has two halves:

- **the attribute** (this PR) — a marker on an interface, a class, or a method.
  No runtime hook, no reflection, no emit: AOT-safe by construction, and free
  until something reads it;
- **the generator** (the next PR) — a Roslyn source generator that reads the
  attribute and emits a plain C# decorator that opens a span and records the
  call's metrics around the seam.

The property that makes the whole thing worth doing is that a decorator built
around an *interface* covers *every* implementation of it by construction. That
is the property the hand-written `InstrumentedToolRegistry` and
`InstrumentedProviderRegistry` already have — one wrapper, identical telemetry
for every provider — except that today it has to be written by hand, once per
seam.

Nothing is instrumented until the generator lands, and even then only where
someone wrote the attribute. This PR changes no runtime behaviour at all.

## The attribute surface

The attribute lives in `src/Harbor.Abstractions.Contracts/`, in the
`Harbor.Abstractions.Contracts` namespace, next to the three marker attributes
the existing generators already read (`MoodFrameAttribute`,
`TerminalEscapeAttribute`, `TuiRendererAttribute`).

```csharp
namespace Harbor.Abstractions.Contracts;

[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ObservedAttribute : Attribute
{
    public ObservedAttribute() { }

    public ObservedAttribute(string span) { Span = span; }

    public string? Span { get; set; }
    public string? Counter { get; set; }
    public string? Duration { get; set; }
    public ObservedScope Scope { get; set; } = ObservedScope.SpanAndMetrics;
    public bool CaptureArguments { get; set; }
    public bool CaptureResult { get; set; }
    public string[]? Exclude { get; set; }
}

public enum ObservedScope
{
    None = 0,
    Span = 1,
    Metrics = 2,
    SpanAndMetrics = 3,
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ObservedPolicyAttribute : Attribute
{
    public string? SpanPrefix { get; set; }
    public ObservedScope Scope { get; set; } = ObservedScope.SpanAndMetrics;
    public bool CaptureArguments { get; set; }
}
```

### `ObservedAttribute`

| Member | Meaning |
|--------|---------|
| `Span` | The span name. Unset ⇒ derived from the seam and the operation. |
| `Counter` | The call counter's name. Unset ⇒ derived from the seam. |
| `Duration` | The duration histogram's name. Unset ⇒ derived from the seam. |
| `Scope` | What the decorator emits. Default `SpanAndMetrics`. |
| `CaptureArguments` | Capture the call's arguments as span attributes. Default `false`. |
| `CaptureResult` | Capture the call's result as a span attribute. Default `false`. |
| `Exclude` | Members of the annotated *type* that are not instrumented. Only meaningful on a type. |

`CaptureArguments` and `CaptureResult` default to `false` on purpose: arguments
and results are potentially user data. Capturing them is a per-seam decision, not
a default.

### `ObservedScope`

| Value | Emitted |
|-------|---------|
| `None` | Nothing. An explicit opt-out; it beats every coarser policy level. |
| `Span` | A span around the call. |
| `Metrics` | A call counter and a duration histogram around the call. |
| `SpanAndMetrics` | Both. The default. |

### `ObservedPolicyAttribute`

The coarse policy: what a whole namespace — or a whole assembly — emits when no
more specific statement covers a seam.

| Member | Meaning |
|--------|---------|
| `SpanPrefix` | The prefix put in front of every derived name in this scope. Unset ⇒ the seam's own name is used. |
| `Scope` | What the policy emits. Default `SpanAndMetrics`. |
| `CaptureArguments` | Capture arguments in this scope. Default `false`. |

C# has no namespace attributes, so a namespace policy is a marker *type* in the
namespace:

```csharp
[ObservedPolicy(SpanPrefix = "store")]
internal static class TelemetryPolicy;
```

and the assembly policy is the same attribute applied to the assembly:

```csharp
[assembly: ObservedPolicy(SpanPrefix = "harbor")]
```

## The policy order

Four levels, most specific first:

| # | Level | Statement | Applies to |
|---|-------|-----------|------------|
| 1 | Attribute | `[Observed]` on the member | that member |
| 2 | Type | `[Observed]` on the declaring type | every member of that type except those named in `Exclude` |
| 3 | Namespace | the nearest `[ObservedPolicy]` marker type in the member's namespace | every member in that namespace |
| 4 | Assembly | `[assembly: ObservedPolicy]` | every member in the assembly |

The rules that make the order checkable rather than a convention:

- **Exactly one statement wins.** The most specific statement that mentions a
  member is the one that governs it; nothing is merged across levels. A member
  that carries its own `[Observed]` therefore does *not* inherit the namespace
  policy's prefix or scope — it says what it wants, in full.
- **A seam with no statement is not observed.** Silence is the default, and it is
  the safe one.
- **`Scope = ObservedScope.None` is an opt-out, not a silence.** It wins over
  every coarser level, so a member can be excluded from a namespace-wide policy
  without the policy being weakened for its neighbours.
- **Contradictions are compile errors, not silent resolutions.** A statement that
  says `None` *and* names a span, or two namespace policies that disagree about
  the same namespace, are generator diagnostics in the next PR — the generator
  refuses to guess.

The trade-off of "one statement wins" is that a fine-grained statement restates
what it wants. That is the point: the alternative — merging fields across
levels — makes the effective configuration depend on which levels happen to
exist, and a reader of the source cannot tell what a seam will emit.

## Names

Names are a contract. The stable instrument names live in
[the telemetry README](../src/Harbor.Telemetry.Core/README.md#metric-names-stable)
and a dashboard that reads `tool.calls` breaks silently if it is renamed.

When a name is not stated, the generator derives it:

| Name | Derivation | Example |
|------|------------|---------|
| span | `<seam>.<operation>` | `store.append_message` |
| counter | `<seam>.calls` | `store.calls` |
| duration histogram | `<seam>.duration.ms` | `store.duration.ms` |

- `<seam>` is the policy's `SpanPrefix` when a policy is in scope, otherwise the
  declaring type's name with a leading `I` dropped, in snake case
  (`ISessionStore` ⇒ `session_store`).
- `<operation>` is the member's name in snake case, without the `Async` suffix
  (`AppendMessageAsync` ⇒ `append_message`).

The instruments land on the canonical `Harbor.Telemetry` meter, next to the
existing hand-written ones, so a `MeterListener` — and the OTLP exporter that
subscribes to that meter by name — sees them with no extra wiring.

## The switch: observability off

The whole mechanism sits behind one **compile-time** switch: the MSBuild property
`HarborWithObservedInstrumentation` (default `true`), read by the generator
through `AnalyzerConfigOptionsProvider.GlobalOptions`. It follows the flags the
repository already has — `HarborWithPlugins`, `HarborWithDaemon`,
`HarborWithRenderer` in `Directory.Build.props`.

**Observability off** — that is, `HarborWithObservedInstrumentation=false` —
means the generator emits **nothing**: no decorator type, no instrument field, no
span call, no wiring line. The binary carries no instrumentation code at all,
which is a stronger guarantee than "the instrumentation is cheap when switched
off at runtime": there is nothing to switch off.

Two smaller switches exist and need no new machinery:

- **runtime, no listener** — with no `ActivityListener`/`MeterListener` attached,
  `ITracer.StartSpan` returns `null` and the generated code degrades to a single
  null check. `ITracer`/`IMetrics` are contracts in
  `Harbor.Diagnostics.Abstractions`, and their implementations already behave
  this way;
- **runtime, no OTel SDK** — see the boundary below.

## What is deliberately not instrumented

These are non-goals, not omissions. Each of them is a place where instrumentation
would change the thing it measures, or where the mechanism does not apply.

- **The agent-loop hot path.** The turn runner publishes a `MessageUpdateEvent`
  per streamed *batch* of deltas (#1527 coalesced the per-token publish into
  batches; before that it was one per token); instrumenting that means measuring
  the measurer. The granularity there is the *turn*, not the delta — `agent.turn`
  already exists, and it stays the unit.
- **Per-frame and per-event paths.** The CellForge render path and the event-bus
  publish path are not instrumented per frame or per event; they are measured
  in aggregate.
- **The OpenTelemetry SDK in our binaries.** `Harbor.Telemetry.Otlp` is not
  referenced by the CLI or the TUI. Our binaries get observability through the
  AOT-safe sink in `Harbor.Telemetry.Core` — plain `ActivitySource`/`Meter`, no
  reflection, no protobuf, no gRPC. The boundary is issue #1799: the OTLP
  exporter's legitimate consumer is an *embedding JIT host* — an application that
  references `Harbor.Telemetry.Otlp` itself. Nothing in our binaries attaches it.
- **Runtime reflection and dynamic proxies.** The decorator is emitted as plain
  C# at build time. `DispatchProxy` (`RequiresDynamicCode`) and
  `Reflection.Emit`-based proxies are forbidden by the reflection convention in
  [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) §5.8, and they are not
  needed: the generated code is ordinary C#.
- **Code we do not compile.** Plugins and out-of-process MCP servers are outside
  the generator's reach; they keep their own manual `ILogger`/`LoggerMessage`
  instrumentation, or are observed from the protocol side.
- **The plugin doors.** `IPluginLoadHost` is not decorated. The seam to observe
  is the dispatch point where all of its doors converge, not the doors
  themselves.

## The plan

| PR | What | Ships |
|----|------|-------|
| **PR-1** (this one) | The attribute surface, this document, the public-API baseline update. No generator, no annotated seam, no behaviour change. | now |
| **PR-2** | The generator (`Harbor.CodeGen`), a fixture assembly, and a tripwire with a **negative control**: a marked seam emits a span and its instruments, an unmarked seam emits nothing, and a deliberately contradictory attribute fails the build. No product seam is marked yet. | next |
| **Later** | The seam migrations, in the quality/test-optimisation wave next to epic #1724, one seam family at a time. | after the backlog |

The ordering is the point, and it is the rule of epic #1724 applied literally:
**the tripwire comes before the first migration.** A generator without a
negative control is an unverified claim; a migration without a tripwire changes
the product and the measuring instrument in the same commit, which is the order
that rule exists to forbid. The hand-written decorators in
`Harbor.Telemetry.Core` stay until a generated decorator is proven to emit the
same span and instrument names.

## Boundaries this contract does not cross

- **AOT.** The attribute is a plain attribute and the generated code is plain
  C# — no reflection, no emit, no `Activator`, no `MakeGenericType`. The
  generator itself is build-time only, exactly like the other generators in
  `Harbor.CodeGen`.
- **The frozen facade.** `Harbor.Abstractions` is the v1.0 interface facade, and
  ADR-007 fixes its dependency direction: it references
  `Harbor.Abstractions.Contracts` and nothing else. This contract does not add a
  reference to it — which is one of the reasons the attribute lives where it
  does (see below).
- **The frozen public API.** Adding a public type to
  `Harbor.Abstractions.Contracts` is an additive change to a frozen surface, and
  the repository's mechanism for that is the public-API baseline: the type is
  added to `PublicAPI.Shipped.txt` in the same diff, where a reviewer sees it.
  Since #1845 that baseline is the compiler's own — `PublicApiAnalyzers`
  reports the missing row as `RS0016` and the build fails without it — and the
  re-signing rule is enforced per commit by
  `tools/check-public-api-resign.py`.
  The attribute is not an extension point: it is a marker for our own generator,
  and it registers no axis, opens no door, and changes no plugin contract.
- **The telemetry boundary.** `Harbor.Telemetry.Core` implements the contracts in
  `Harbor.Diagnostics.Abstractions`; `Harbor.Telemetry.Otlp` exports them. Our
  binaries reference the first and not the second (issue #1799).

## Why the attribute lives in `Harbor.Abstractions.Contracts`

The constraint that decides this is not aesthetic: **the assembly that carries a
marked type must be able to reference the assembly that declares the attribute.**
C# resolves an attribute usage against a real type, so wherever `[Observed]` is
written, that project needs a reference to the attribute's assembly.

The contract layer is where the seams live, and it is the one ring every seam
host already reaches:

- `Harbor.Abstractions` — the frozen interface facade — holds the bulk of the
  contract layer's interfaces (`ITool`, `IToolRegistry`, `ILlmClient`,
  `IProviderRegistry`, `IAgent`, `ISessionStore`, `IEventBus`). It already
  references `Harbor.Abstractions.Contracts`, and ADR-007 permits it no other
  Harbor reference.
- The other contract assemblies (`Harbor.Terminal.Abstractions`,
  `Harbor.Plugins.Abstractions`, `Harbor.Ipc.Abstractions`, …) reach
  `Harbor.Abstractions.Contracts` transitively through `Harbor.Abstractions` —
  which is exactly how `TerminalEscapeAttribute`, declared in the contracts
  assembly, is already used from `Harbor.Terminal.Abstractions` today. Measured
  on `dev` at 2026-10-20: 37 projects under `src/` and `apps/` reference
  `Harbor.Abstractions` directly, so they all reach the attribute.
- `Harbor.Diagnostics.Abstractions` is referenced by three projects under
  `src/`. A marker attribute there would reach the seams in
  `Harbor.Storage.*`, `Harbor.Providers.*` and `Harbor.Tools.Builtin` only by
  adding a reference edge to each of them — and would *not* reach the seams in
  the frozen facade at all without opening a second reference out of that
  facade, against ADR-007.

So the choice is between an additive public type in the contract assembly (one
baseline file, no new reference edges) and a new reference edge out of a frozen,
publicly-packaged facade (a matrix row, a layering-rule change, and a new
dependency edge in a v1.0 package). The first is smaller, and it is the one the
owner allowed when they ruled that the flip goes the other way if the matrix
churn exceeds the baseline churn — which, measured, it does.

The precedent agrees: all four of the repository's generator marker attributes
(`MoodFrameAttribute`, `TerminalEscapeAttribute`, `TuiRendererAttribute`,
`ToolInfoAttribute`) live in the ADR-007 contract pair, none of them in a
diagnostics assembly, and for the same reason — the marked type has to be able
to see the marker.

Two things this choice does **not** decide:

- **The generated code does not live here.** The decorator the generator emits
  belongs to the telemetry side (`Harbor.Telemetry.Core`, and the composition
  root that wires it), and it consumes `ITracer`/`IMetrics` from
  `Harbor.Diagnostics.Abstractions`. The marker and the thing it marks may sit
  in different assemblies; that is the normal shape of a source generator.
- **The public surface stays locked.** `Harbor.Abstractions.Contracts` has a
  public-API baseline, so the additive type is visible in the diff where a
  reviewer sees it. `Harbor.Diagnostics.Abstractions` has no baseline: a public
  type added there would enter the frozen surface without any gate noticing.

## What the next PR needs from this one

- the attribute surface above, unchanged, as its input contract;
- the naming rules, as the contract the generated instruments and spans keep;
- the policy order, as the rules the generator implements — including the
  "exactly one statement wins" rule and the contradictions that must become
  compile errors rather than silent resolutions;
- the switch name (`HarborWithObservedInstrumentation`) and the guarantee that
  "off" means *nothing is emitted*.

## See also

- issue #1797 — the design this document is the contract for;
- issue #1799 — the `Harbor.Telemetry.Core` ↔ `Harbor.Telemetry.Otlp` boundary;
- [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) — the layering matrix and
  the reflection convention the generator must respect;
- [the telemetry README](../src/Harbor.Telemetry.Core/README.md#metric-names-stable)
  — the stable instrument names the generated ones join;
- [ANTIPATTERNS.md](./ANTIPATTERNS.md) — the fire-and-forget rule that decides
  where a "call" and an "effect" are not the same thing, and must not be
  decorated as if they were.
