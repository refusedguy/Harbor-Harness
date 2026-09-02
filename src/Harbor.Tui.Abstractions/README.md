# Harbor.Tui.Abstractions

**Backward-compat facade** over the R6 split of the old TUI abstractions. This project contains **no code** — it exists only so existing `ProjectReference` entries pointing at `Harbor.Tui.Abstractions.csproj` keep building until the v0.6 removal.

The actual code lives in two split projects:

- `Harbor.Ui.Framework` — TEA state machine (`UiStore`, `UiState`, `UiEvent`, `UiReducer`, `TuiEffectHost` in `Harbor.Ui.Framework.State`) + dockable panel system — used by terminal TUIs **and** desktop GUIs
- `Harbor.Terminal.Abstractions` — terminal-specific renderer / view / view-model / plugin contracts + GFM table rendering

## Layer

Presentation contracts (facade). Transitive dependency direction:

```
Harbor.Tui.Abstractions → Harbor.Ui.Framework          → Harbor.Abstractions
                        → Harbor.Terminal.Abstractions  → Harbor.Ui.Framework
                                                        → Harbor.Abstractions
```

## Dependencies

- `Harbor.Ui.Framework`
- `Harbor.Terminal.Abstractions`

## Public API

`(none — the facade itself ships zero types)`; `ITuiRenderer`, `UiStore`, `UiState`, `UiEvent`, `ITuiPanelPlugin`, and friends resolve via the split projects above. Consumers must update their `using` directives: `Harbor.Tui.Abstractions.State` → `Harbor.Ui.Framework.State`, etc.

## InternalsVisibleTo

The `InternalsVisibleTo` grants for `UiStore.Transition` now live in
`src/Harbor.Ui.Framework.State/Harbor.Ui.Framework.State.csproj:23-27`
(`Harbor.App.Avalonia`, `Harbor.App.Wpf`, `Harbor.App.Maui`, `Harbor.App.Blazor`,
`Harbor.App.Cli`). Desktop GUIs use it to fold non-agent state transitions (e.g.
inserting a user-input line into the transcript before the agent emits a
UserMessage event).

## Usage

New code should reference the split projects directly. This facade is scheduled for removal in v0.6 (see `docs/ARCHITECTURE_LAYERS.md §2`).

## See also

- [../../docs/ARCHITECTURE_LAYERS.md](../../docs/ARCHITECTURE_LAYERS.md)
- [../../docs/ALTERNATIVE_UIS.md](../../docs/ALTERNATIVE_UIS.md) — full TUI/GUI renderer comparison
