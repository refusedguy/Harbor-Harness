# Harbor API reference

> **Status:** landing page for the DocFX API section (issue #431, slice D2).
> The generated member pages (`api/*.yml`) are produced by `docfx build` from
> the metadata lane in `docfx.json` — a later slice. Until then this page
> names the surface the lane covers and points at the hand-written sources.

## Covered assemblies

The metadata lane reads the two frozen contract assemblies — the surface v1.0
freezes and the surface library consumers program against:

- `Harbor.Abstractions` — interface contracts (tools, providers, sessions,
  agents, events, plugins). Hand-written overview:
  [src/Harbor.Abstractions/README.md](../src/Harbor.Abstractions/README.md).
- `Harbor.Abstractions.Contracts` — pure models, value objects, events,
  permission rules. Hand-written overview:
  [src/Harbor.Abstractions.Contracts/README.md](../src/Harbor.Abstractions.Contracts/README.md).

XML documentation is generated for every project by the global
`GenerateDocumentationFile` flag in `Directory.Build.props` (missing-doc
warnings stay demoted via `CS1591`, so coverage can land incrementally
without blocking the build at 0 warnings).

## Concept docs

The matching hand-written guides live under `docs/` — start with
[Getting Started](../docs/GETTING_STARTED.md) and
[Architecture](../docs/ARCHITECTURE.md).

## Tool choice

Why DocFX and not a markdown-only generator, what this slice wires, and what
stays open (full `docfx build`, XML-doc coverage gate, Pages URL):
[ADR-017](../docs/adr/ADR-017-docs-site-docfx.md).
