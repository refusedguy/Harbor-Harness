# ADR-016: Roslyn plugin-host fate — AOT main plus JIT split host (option 2)

> **Status (2026-10-09):** dated record, not normative. Every cited fact below was
> read at `dev` = `0da7f2ba` (branch `dev`, HEAD `0da7f2ba`). This document records
> the decision issue #418 asks for; it asserts no machine-checkable fact about the
> tree as it stands, so it carries no `Status: normative` banner. The facts it rests
> on are fenced where they live: the publish record in `.github/aot-warning-baseline.txt`,
> the JIT half of the table in `docs/BENCHMARKS.md` section 4.4, the split-point build
> closure in `tests/Harbor.Architecture.Tests/BuildGraphCoverageRule.cs`, and the
> capability table in `src/Harbor.Plugins.Host/README.md`.

## Status

**Decided: option 2** — AOT main plus JIT plugin host over IPC/MCP, with
`src/Harbor.Plugins.Host` as the split point. Date of decision: 2026-10-09.
Owner sign-off (`@refusedguy`) is recorded as the closing comment on issue #418,
not in this file — no perf work gated on this decision merges before that comment
lands. This ADR confirms the direction already taken in the #48 scope comment
(split: Core AOT plus TUI JIT over UDS, late-attach, `harbor serve` / `harbor tui`,
ROADMAP v0.9); it does not re-open it.

## Context

Issue #418 (slice S4 of epic #48) is the decision slice: the three S1–S3 evidence
slices had to land first, and they have. At the read commit:

| slice | issue | state |
|---|---|---|
| S1 measurements | #411 | OPEN, JIT half landed via PR #1043 |
| S2 publish gate | #413 | CLOSED, gate landed via PR #1014 |
| S3 trimming audit | #414 | CLOSED |
| S5 split skeleton | #419 | CLOSED, parity table landed via PR #1041 |

The live contradiction S4 resolves: `apps/Harbor.App.Cli` publishes AOT while the
plugin pipeline compiles C# at runtime with Roslyn and loads it through a
collectible load context — an architectural fork, not a publish flag.

## 1. Evidence

### S1 — JIT half measured, AOT half blocked

Harness `tools/measure-jit-workload.sh`, runner `.github/workflows/meas-jit-aot.yml`
(measurement only, not a gate). Reported in `docs/BENCHMARKS.md` section 4.4 from
CI run 37935894413 (2026-10-09, AMD EPYC 7763, SDK 10.0.401, framework-dependent
Release, apphost-driven, N=7 median):

| verb | median | spread | RSS peak median |
|---|---|---:|---:|
| `--version` | 138 ms | 1.46x | 43 MB |
| `--help` | 134 ms | 1.09x | 43 MB |
| `providers` (DI-heavy, closest proxy to real startup) | 459 ms | 1.17x | 94 MB |

Publish dir 51 MB, apphost binary 40 MB (framework-dependent). The AOT column does
not exist: the NativeAOT publish of this tree fails (S2 record below), a failing
ILC publish emits no binary, so there is nothing to drive. The section 4.1 AOT
target (sub-100 ms cold start) stays a prediction, not a measurement. Whether AOT
buys enough to justify dropping in-process Roslyn plugins cannot be answered until
the AOT column exists — this table is the JIT half of it, and the baseline to beat
is about 459 ms / 94 MB on the DI-heavy verb.

### S2 — the publish fails, and the failure is the record

Gate `aot-publish` in `.github/workflows/ci.yml` (PR #1014): full-tree publish with
`-p:HarborWithAot=true`, inventory diff against `.github/aot-warning-baseline.txt`,
then the published-binary smoke. The committed record says, verbatim in intent: the
AOT publish fails, both recipes, measured not predicted. Full-tree recipe reaches
ILC and fails on three ids the csproj does not demote — IL3000, IL2070, IL2072 as
errors under warnings-as-errors. One of the IL3000 sites sits inside
`Microsoft.CodeAnalysis` itself and is not fixable from our code; the IL2070/IL2072
sites sit in the plugin instantiation path whose future this ADR decides. The
ACCEPTED rows (IL2026 x14, IL3050 x9, IL2104 x3, IL3053 x2) are demoted warnings on
reflection-based JSON plus runtime assembly loading — printed, not gone. While the
record says the publish fails there is no artifact to run; the smoke step asserts
the absence in that state and starts executing `--version` / `--help` / `providers`
the day the record flips to published.

Consequence for the options: an option that cannot publish is not a real option.
Options 1-AOT-half and 3 both presuppose a publish this record says does not exist
yet; option 2 is the only one that ships value (in-process-equivalent tools over a
pipe) while the record still says failed.

### S3 and S5 — audit closed, split point builds, parity written

S3 (#414) closed: the trimming-safety audit of hot paths is done; its unsupported
surface feeds the per-option lists below. S5 (#419) closed as skeleton plus parity,
not a finished plugin system: `src/Harbor.Plugins.Host` is in `Harbor.slnx` (one
solution row, PR #1008; guard `BuildGraphCoverageRule`), carries
`PublishAot=false`, speaks MCP stdio (`initialize`, `tools/list`, `tools/call`,
`ping`, protocol version `2024-11-05`), and its README carries the 8-row capability
parity table plus the failure paths (PR #1041). Still open inside S5 and therefore
not evidence here: AOT-never-loads-Roslyn enforcement, host startup-cost
measurement against the S1 baseline, parent-side EOF surfacing.

## 2. Options from #48, verbatim

1. Two distributions: JIT/CoreCLR full vs NativeAOT reduced (no in-process compiled plugins).
2. AOT main + JIT plugin host over IPC/MCP (`Harbor.Plugins.Host` as split point; process boundary is not a sandbox).
3. Build-time plugins (compiled into publish, no runtime arbitrariness).

## 3. Per-option unsupported-feature lists

Each row names the verdict per option. Sources: the S2 baseline rows for the
serializer/config rows, the csproj recipe notes for globalization, the Host README
parity and failure-paths sections for plugin availability and error paths.

### Config reflection (runtime assembly loading, plugin type scanning)

| option | verdict |
|---|---|
| 1 JIT-full | supported — collectible load context path stays as-is |
| 1 AOT-reduced | unsupported — no in-process compiled plugins, so no load contexts either |
| 2 split (chosen) | unsupported in AOT core; supported inside the JIT host only — the core never loads Roslyn in-process |
| 3 build-time | unsupported at runtime — references frozen at publish, no scanning |

### Serializers (reflection-based JSON on the S2 ACCEPTED sites)

| option | verdict |
|---|---|
| 1 JIT-full | supported with the current concession (prints, does not fail) |
| 1 AOT-reduced | degraded — the concession travels with the six types until source-gen lands; dropping plugins does not drop the codec rows |
| 2 split (chosen) | degraded, same as 1-AOT-half — the split moves Roslyn, not the JSON codecs |
| 3 build-time | degraded, same — freezing plugins does not source-generate the codecs |

### Globalization

| option | verdict |
|---|---|
| 1 JIT-full | supported — ICU as today |
| 1 AOT-reduced | supported — the AOT recipe keeps `InvariantGlobalization=false`, no ICU cut under any option |
| 2 split (chosen) | supported in both processes — no globalization cut is part of this decision |
| 3 build-time | supported — same, no cut |

### Error paths

| option | verdict |
|---|---|
| 1 JIT-full | tool-throw surfaces in-process; host-crash class does not exist |
| 1 AOT-reduced | plugin error paths absent with the plugins — silent surface, documented as reduced |
| 2 split (chosen) | specified in the Host README and implemented: tool-throw becomes an `isError` result, unknown tool or method becomes `-32602` / `-32601`, host death is stdio EOF the core must surface loudly (not a silent no-plugins state), protocol mismatch fails openly on the announced version |
| 3 build-time | compile-time errors replace runtime ones; a bad plugin breaks the publish instead of a turn |

### Plugin availability (tool / provider / agent / TUI view / panel / store / backend)

| option | verdict |
|---|---|
| 1 JIT-full | all supported in-process (TUI view: closed seam #564 — collected, never rendered — in every option) |
| 1 AOT-reduced | tool, provider, agent, panel, store, backend via CS-source plugins: unsupported — the reduced binary has no Roslyn |
| 2 split (chosen) | tool: supported over MCP stdio; provider, agent, panel, store, backend: unsupported with reason (streaming factory, policy rehydration, renderer-process state, per-read IPC, UI-process construction — see the Host README table); skill / MCP server: not plugin axes in any option; process boundary is not a sandbox — full trust, crash containment only |
| 3 build-time | tool supported frozen at publish; provider / agent / panel / store / backend supported only if compiled in — no load, no reload, no watcher; shipping a new plugin means shipping a new binary |

## 4. Decision

Option 2. The AOT binary must never load Roslyn in-process — no `Microsoft.CodeAnalysis`
reference in the AOT publish closure, no collectible load context, no runtime C#
compilation. The escape hatch is named and already ships as skeleton: out-of-process
`Harbor.Plugins.Host` (JIT, `PublishAot=false`) exposing tools over MCP stdio, wired
as the opt-in `harbor-csharp-plugins` entry. Build-time compilation stays available
as a deployment choice inside option 2 (freeze a known plugin set into a publish),
not as the architecture — it answers "how we ship", not "where Roslyn runs".

Why this one, on the evidence rather than by preference: it is the only option
consistent with all three S1–S3 facts at once. S2 says the AOT publish fails with
Roslyn inside the closure — options 1-full and 3 keep it there. S1 says the AOT gain
is unmeasured — options 1-reduced and 3 charge users the full plugin price for a
gain with no number. S5 already built the option-2 skeleton and wrote its parity
cost down (tools only, every other capability unsupported with reason) — options 1
and 3 have no equivalent honest-cost artifact. The split keeps the one capability
that fits the wire and declines the rest by name instead of by breakage.

## 5. Delta against #21

Issue #21 decides the UI half: Core (NativeAOT) plus TUI (JIT) in separate processes,
NDJSON over UDS, late-attach with scrollback replay, `harbor serve` / `harbor tui`.
This ADR decides the plugin half: AOT core plus JIT Roslyn host, MCP stdio, the Host
as split point. Overlap: the same split direction, the same late-attach-compatible
framing, the same stated non-claim (a process boundary is not a sandbox here; #21
likewise promises no isolation it has not built). What this ADR decides that #21
does not: the Roslyn fate itself — in-process compilation forbidden in the AOT
binary, the per-capability parity (tools cross, five capabilities do not), the
failure-path contract, and the unblock list below. Neither subsumes the other; S5
reuses #21's transport family and invents no second one.

## 6. Spec 17 section 8

The #418 criterion names spec 17 section 8. Section 8 is the repo-hygiene scope
guard (what hygiene fixes may touch); it says nothing about plugins or AOT. Plugin
policy lives in section 7 (freeze the API surface, capabilities as policy not
sandbox, honest "trusted code" wording) — and section 7 already agrees with this
ADR, so no spec update is part of this slice. If a future edit claims the plugin
architecture from spec 17, it cites section 7, not section 8.

## 7. What this unblocks, and what stays blocked

Unblocked by this ADR once the #418 closing comment (with sign-off) lands: the S5
remainder — AOT-never-loads-Roslyn enforcement test, host startup-cost measurement
(process spawn plus JIT warmup plus Roslyn compile plus tool registration) against
the S1 JIT baseline, parent-side EOF surfacing — and then the perf investments that
assume AOT (trimming discipline on the ACCEPTED codec rows, zero-alloc work on hot
paths that the S3 audit cleared).

Still blocked: the AOT column of the S1 table and the published-binary `ask` smoke —
both need the S2 record to flip from failed to published first. No perf claim that
depends on AOT binary numbers may be quoted until that column exists.

## References

Evidence, each read at the commit stated in the header: `docs/BENCHMARKS.md`
section 4.4 (JIT half, run 37935894413), `.github/aot-warning-baseline.txt`
(headline plus seven rows), `apps/Harbor.App.Cli/Harbor.App.Cli.csproj` (AOT
recipe notes), `src/Harbor.Plugins.Host/README.md` (parity plus failure paths),
`src/Harbor.Plugins.Host/McpStdioServer.cs` (protocol version),
`src/Harbor.Plugins.Host/Harbor.Plugins.Host.csproj` (`PublishAot=false`),
`Harbor.slnx` (Host row), `docs/specs/17-agentic-product-direction.md`
sections 7 and 8, issues #48, #411, #413, #414, #419 and PRs #1008, #1014, #1041, #1043.
