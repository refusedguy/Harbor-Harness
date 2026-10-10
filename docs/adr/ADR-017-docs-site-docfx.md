# ADR-017: docs site stays DocFX — metadata lane wired, Pages behind an opt-in

> **Status (2026-10-10):** dated record, not normative. Every cited fact below was
> read at `dev` = `21bdef4f`. This document records the tool decision issue #431
> asks for; it asserts no machine-checkable fact about the tree as it stands, so it
> carries no `Status: normative` banner. The wired state it describes is fenced
> where it lives: the metadata lane in `docfx.json`, the validation in
> `.github/workflows/docs.yml`, the landing page in `api/index.md`.

## Status

**Decided: DocFX.** Date of decision: 2026-10-10. Slice D2 of issue #431.

## Context

Issue #431 asked for the tool decision first (DocFX vs MdDocs) with a default to
DocFX, then config, XML-doc generation, CI publish, and a coverage gate. Slice D1
(PR #1049) already landed the DocFX-shaped foundation: `docfx.json` at the repo
root, `docs/toc.yml` covering the top-level `docs` pages, and a `site` job in
`docs.yml` that validates the manifest and assembles a static `_site` with stdlib
python only. The metadata lane was deliberately left empty and Pages publishing
deliberately left unwired — both are this slice.

## Decision

Keep DocFX; do not introduce MdDocs. Three facts, all measured on the tree,
all pointing the same way:

1. **The tree already committed.** `docfx.json`, `docs/toc.yml`, and the assembly
   lane with its artifact are landed and green. A second generator would strand
   that work and give the same `docs` tree two competing manifests.
2. **Markdown-only cannot produce the required surface.** Issue #431 item 2
   requires an API reference for the two frozen contract assemblies
   (`Harbor.Abstractions`, `Harbor.Abstractions.Contracts`). A markdown-only
   generator renders the guides but not the member surface; only the metadata
   lane (XML docs into generated reference pages) does.
3. **No flag change was needed.** `GenerateDocumentationFile` is already set
   globally in `Directory.Build.props`, with missing-doc warnings demoted via
   `CS1591`, so coverage can land incrementally without breaking the 0-warnings
   build. The issue's coordination requirement (set the flag once, shared with
   the API-freeze slice) is satisfied by that single global location — this
   slice wires the consumer (the metadata lane), not the flag.

## What this slice wires

- `docfx.json` metadata lane: the two contract assemblies, `dest` set to `api`,
  `TargetFramework` pinned to the tree's `net10.0`. The `site` job asserts every
  listed project file exists, so a moved project goes red instead of dropping
  out of the reference silently.
- `api/index.md`: hand-written landing page beside the future generated pages;
  the one tracked file under `api/` (the rest is docfx build output, still
  ignored). Build content covers `api/*.md`, so the static assembly carries it.
- `deploy` job in `docs.yml`: publishes the assembled `_site` to GitHub Pages.
  It runs only on `master` and only when the repo variable `HARBOR_PAGES_ENABLED`
  is `true` — until the owner enables Pages (Settings, source GitHub Actions)
  and sets the variable, the job skips. A publish lane that fails before its
  prerequisite exists would be red on a head nobody broke; the opt-in keeps
  every head green.
- `docs/ROADMAP.md` documentation-debt row now names this state instead of
  "stubbed but not wired".

## Deliberately NOT in this slice

- **Full `docfx build`** (generated `api/*.yml` member pages). It needs the
  docfx binary and the SDK in CI; `docs.yml` stays dotnet-free by design, so
  the static assembly ships first and the full build is a later slice.
- **XML-doc coverage gate** (issue item 5). Demoted warnings mean coverage is
  unmeasured today; a gate with no measurement would be advisory paint. Later
  slice, after the first measured sweep over the two assemblies.
- **README link to the published URL.** There is no live URL until the first
  master deploy, and a link to a URL that does not serve yet is a broken link
  by definition. The link lands in the slice that observes the first green
  deploy — not this one.

## Consequences

- The manifest, the nav, the assembly, and the publish lane agree on one tool.
- Removing the metadata lane, moving a listed project, or dropping the global
  documentation flag all go red in the `site` job rather than quiet.
- The first master push after the owner opt-in publishes; every push before
  that stays green by skipping.
