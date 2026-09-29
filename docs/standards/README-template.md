# Per-project README template

Every `src/**/<Project>/` directory must ship a `README.md`, and every
**packable** project (`<IsPackable>true</IsPackable>`) must ship one that
carries all six required sections below.

This file is the contract. The gate that enforces it lives in
[`tests/Harbor.Architecture.Tests/ReadmeCoverageTests.cs`](../../tests/Harbor.Architecture.Tests/ReadmeCoverageTests.cs)
and runs as part of `dotnet build -c Release` (the `HarborArchitectureGate`
target in [`Directory.Build.props`](../../Directory.Build.props)), so a missing
README or a missing section fails the build on CI — no separate test step.

## Why a README, and why a gate

The README is the only documentation a third party sees before
`dotnet add package`, and the first thing [`AGENTS.md`](../../AGENTS.md) points
a new agent at for a project's shape. This gate exists because the coverage
number used to live only as prose in `docs/ROADMAP.md` and had already drifted
twice (32/51 recorded, 45/54 and then 48/54 actually measured). A number in a
document cannot hold itself true; a check can.

## The six required sections

The gate keys, exactly as `ReadmeCoverageTests.RequiredSections` spells them:

```
what    public    wiring    usage    deps    limits
```

Headings are matched case-insensitively as a **substring of any `##`/`###`/`####`
heading**, so existing house styles (`What's in it`, `Публичный API`,
`Dependency rules`) keep passing without a mass rewrite. Match per heading,
never against the whole document — an anchored pattern applied to a joined
heading list silently only ever matches the first heading.

| Concept key | Accepted heading substrings | What it must tell the reader |
|-------------|----------------------------|------------------------------|
| `what` | `what`, `overview`, `назнач`, `что `, `inside`, `when to use` | What the project is and which architectural layer it sits in. One paragraph is enough. |
| `public` | `public api`, `public surface`, `api surface`, `what's in it`, `что запускается`, `files`, `namespaces` | The types a consumer actually touches, with signatures — not the file tree. |
| `wiring` | `wiring`, `registration`, `configuration`, `architecture`, `как включить`, `how it works` | How it gets activated: which DI module, which `HARBOR_*` env var, which `HARBOR_TUI` value, or a `[TuiRenderer(Backend = "…")]` id. State "no activation required" explicitly when that is the case. |
| `usage` | `usage`, `quick start`, `consuming`, `example`, `snippet`, `как `, `build` | A minimal snippet that compiles against the real API. Every type and member named in it must exist. |
| `deps` | `dependenc`, `зависим` | Which `ProjectReference`s it carries **and which projects reference it**. The reverse list is what the architecture tests actually pin. |
| `limits` | `limitation`, `when not to use`, `gotcha`, `caveat`, `known `, `ограничен`, `notes` | What it does *not* do, what is unimplemented, what is platform-gated. |

A project that genuinely has no limitation should still carry the heading and
write "None known." — an empty section is a claim, a missing one is a gap.

## The grandfather list

36 packable projects predate this template and are recorded in
`LegacyNonConformantReadmes` in `ReadmeCoverageTests.cs`, each mapped to the
sections it is still missing. The list is a **ratchet, not a waiver**:

- A **new** project has no entry, so the gate is strict from day one.
- The gate **fails if an entry names a section the README has since gained** —
  fix the README, drop the entry. The list can only shrink.
- The gate **fails if an entry names a project that no longer exists or is no
  longer packable** — stale entries cannot accumulate.

See [`README-AUDIT.md`](./README-AUDIT.md) for the staleness audit of the
pre-existing READMEs and [`docs/ROADMAP.md`](../ROADMAP.md) §Documentation debt
for the current coverage number.

## Front matter

Deliberately absent. The docs-site slice (DocFX) consumes these READMEs as
landing pages and may want YAML front matter; that contract is agreed in that
slice, not here, so the gate does not encode it yet.

## Skeleton

````markdown
# Harbor.Example

One paragraph: what this project is and which layer it sits in.

## Public API

The types a consumer touches, with real signatures.

## Wiring

How it is activated — DI module, `HARBOR_*` env var, `HARBOR_TUI` value.

## Usage

```csharp
// Compiles against the real API.
```

## Dependencies

- `Harbor.Something` — why
- Referenced by: `Harbor.Other` (pinned by `tests/Harbor.Architecture.Tests`)

## Known limitations

- What it does not do yet.

## See also

- [`docs/…`](../../docs/…)
````
