# ADR-015: i18n shared string catalogue lives in `Harbor.Ui.Framework.Abstractions`

## Status

Accepted (slice 1 of #434: inventory + catalogue home only).

## Context

Issue #434 needs one string source of truth shared by three surfaces:
Avalonia, Blazor (`IStringLocalizer` + `.resx` per locale), and TUI/CLI
(resource lookup + terminal-width/unicode fallback). The inventory is
[docs/I18N_STRING_INVENTORY.md](../I18N_STRING_INVENTORY.md): ~1 370 hardcoded
occurrences pre-dedupe, plus a 10-entry non-UI `.resx` precedent
(`ErrorMessages`/`LogMessages`, log/diagnostic text only) whose
`CoreResources` accessor already falls back to the key (`?? name`).

Constraints from the repo:

- `Harbor.Abstractions` is frozen by `tools/check-abstractions-contract.py` —
  UI vocabulary must not grow there.
- `docs/ARCHITECTURE_LAYERS.md` §2: the Application layer must not know UI
  vocabulary; Presentation renderers reach Domain.
- `AGENTS.md` rule 9: Avalonia strings live in the XAML `ResourceDictionary`,
  never in a C# `*Tokens.cs`/`*Theme.cs` class.
- Blazor lives in `contrib/` (unmaintained, outside `Harbor.slnx`): the Blazor
  mechanism must not require an in-solution project move.

## Decision

The shared catalogue is `UiStrings.resx` (`en` = source of truth, embedded) +
per-locale satellites `UiStrings.<locale>.resx` plus a thin accessor with
missing-key → `en` fallback, placed at:

```text
src/Harbor.Ui.Framework.Abstractions/Strings/UiStrings.resx
src/Harbor.Ui.Framework.Abstractions/Strings/UiStrings.cs
```

Why that project:

- It is already classified `Layer.Domain` in `FullLayerMatrixTests` (row
  `["Harbor.Ui.Framework.Abstractions"] = new(Layer.Domain, …)`, cf. ADR-009
  precedent) — TUI renderers, both apps, and `contrib`-Blazor can all reference
  it with **zero new matrix edges**.
- Adding `.resx` + an accessor introduces no new Harbor reference and no I/O
  capability, so the Domain classification survives.
- No new project means no `BuildGraphCoverage` / scan-universe / matrix-row
  updates — the smallest change that satisfies the layering tests.

Per-surface projection off the same keys:

- **Avalonia**: per-locale `ResourceDictionary` XAML dictionaries whose keys
  mirror the catalogue (satisfies rule 9 by construction).
- **Blazor**: `IStringLocalizer` adapter over the same catalogue (lives in
  `contrib/`, no in-solution footprint).
- **TUI/CLI**: direct lookup over the same catalogue, with a documented
  width/unicode fallback (ASCII downgrade for narrow/legacy terminals).

Locale selection via config/env (`HARBOR_LOCALE`, default `en`) is part of this
decision; implementing it — like the second locale, the fallback tests, the
extraction, and the `InvariantCulture` fixes — is a later slice.

## Consequences

- One key list to audit; a string is keyed once, projected three ways.
- `Harbor.Abstractions` stays frozen; no architecture-test changes needed.
- Missing keys render `en`, never a raw resource key (extends the existing
  `CoreResources ?? name` shape).

## Alternatives considered

- **New `src/Harbor.Localization` Domain project** — cleaner on paper, but a
  new assembly forces a matrix row plus coverage/scan updates for zero extra
  reachability: every consumer that could reference it can already reference
  `Ui.Framework.Abstractions`. Rejected as needless surface.
- **Catalogue in `Harbor.Abstractions`** — rejected: frozen by contract gate.
- **Per-surface duplication** — rejected by the issue: triples keying drift.
