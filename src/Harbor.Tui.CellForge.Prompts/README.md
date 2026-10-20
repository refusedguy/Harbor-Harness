# Harbor.Tui.CellForge.Prompts

Console-free dialog prompt engines, extracted verbatim from
`Harbor.Tui.CellForge` ([#1618](https://github.com/refusedguy/Harbor-Harness/issues/1618))
the way `Harbor.Tui.CellForge.Syntax` was in #1617.

## What lives here

- **List prompt engine** (`Widgets/ListPromptEngine.cs`, namespace stays
  `Harbor.Tui.CellForge.Widgets`): the Spectre.Console `Prompts/List` port
  minus the console/render half (SC7, #1215, epic #1195) —
  `ListSelectionMode` (Single / Leaf / Multiple), `ListPromptInputResult`,
  `ListPromptItem<T>` (tree node), `ListPromptTree<T>` (forest → flat row
  order), `ListPromptState<T>` (navigable state: leaf-index navigation,
  wrap-around, page window, incremental search, vim keys, multi-select
  subtree toggle with parent recount), and the per-mode key policy pair
  `IListPromptStrategy<T>` / `SingleListPromptStrategy<T>` /
  `MultiListPromptStrategy<T>`.
- **Form prompt engine** (`Widgets/FormPromptEngine.cs`, namespace stays
  `Harbor.Tui.CellForge.Widgets`): the Spectre.Console `TextPrompt.Show`
  loop minus the console (SC6, #1214, epic #1195) — `FormPromptOutcome`,
  `FormConvertResult<T>`, `FormValueConverter<T>` + `FormConverters`
  (string/int/double/bool via `TypeConverter`-style delegates), the
  `IFormInputSource` port and its `QueueFormInputSource` in-memory impl,
  `FormPromptState<T>` (text in → value-or-error out: chained validators,
  defaults, allow-empty, secret masking, choice lists) and the yes/no
  `FormConfirmState`.

## Public API

Two dialogs' worth of state-only types, one file each, all in
`Harbor.Tui.CellForge.Widgets` — consumers keep their usings; only
`ProjectReference`s change.

### List prompt (`Widgets/ListPromptEngine.cs`)

Hosts own the key loop and the painting (`DialogSelectList`,
`CommandPaletteView`, `FilePickerView` in `Harbor.Tui.CellForge`); the engine
owns selection state. The host's contract is two lines:

```csharp
using Harbor.Tui.CellForge.Widgets;

// state machine: mode decides header semantics; search grows on Char keys
var state = ListPromptState<string>.FromValues(
    values, pageSize: 12, wrapAround: true,
    mode: ListSelectionMode.Leaf, searchEnabled: true);

// one key: true = repaint, false = state ignored the key
bool repaint = state.Update(in key);
```

Deeper:

- `ListPromptState<T>(items, converter, pageSize, wrapAround, mode,
  searchEnabled, initialIndex)` builds from an already-flattened row list;
  `FromValues(...)` is the flat-string shortcut. Query members: `Index`,
  `Items`, `ItemCount`, `PageSize`, `WrapAround`, `Mode`, `SearchEnabled`,
  `SearchText`, `TryGetCurrent`, `ScrollOffsetFor`.
- Moves: `MoveBy` / `MoveTo` / `PageBy` (leaf tables skip group headers
  under `Leaf`/`Multiple`); `ToggleCurrent` + `GetSelectedLeaves` under
  `Multiple`.
- Strategies (Spectre `IListPromptStrategy<T>` minus console coupling):
  `SingleListPromptStrategy<T>` maps Enter→Submit, Escape→Abort, everything
  else → `state.Update`; `MultiListPromptStrategy<T>` maps Space→toggle and
  types through `AcceptsTypedChar` when search is on; both surface
  `CalculatePageSize` / `CalculateInitialIndex` for host-driven page math.
- The key loop of `ListPromptState<T>.Update` is a command table
  (`ListPromptCommandTable`, internal — #1547): one row per gesture
  (arrows/Home/End/paging, the incremental-search keys, the vim keys), each
  row naming the command it runs and the mode it is live in
  (`SearchEnabled` on / off). `Update` is a lookup plus an execute, and the
  vim rows run the SAME command objects as the arrow rows (`j` is `Down`,
  `g` is `Home`), so a new key is one row and the old `KeyCode` switch +
  `UpdateVim` pair is gone.
- `ListPromptTree<T>` + `ListPromptItem<T>.AddChild` build group trees;
  `Flatten()` produces the interleaved row order `ListPromptState<T>` consumes.

### Form prompt (`Widgets/FormPromptEngine.cs`)

The engine owns text-in / value-or-error-out, the source owns line reading,
the host owns keys and repaint:

```csharp
using Harbor.Tui.CellForge.Widgets;

var prompt = new FormPromptState<int>(FormConverters.Int32());
prompt.AddValidator(v => v > 0 ? null : "must be positive");

var source = new QueueFormInputSource();
source.Enqueue("42");
FormPromptOutcome outcome = prompt.ReadAndSubmit(source, out int value, out string error);
// outcome == FormPromptOutcome.Submitted, value == 42
```

- `FormValueConverter<T>(tryConvert, toDisplay)` + `FormConverters.String()`
  / `.Int32()` / `.Double()` (culture-aware comma decimals) / `.Boolean()`
  (y/n/true/false words); `FormValueConverter<T>.FromDelegate(...)` for
  custom types — no exceptions, no reflection, AOT-clean by construction.
- `FormPromptState<T>`: `SetText` / `SetDefault` / `ClearDefault` /
  `SetChoices` / `AddValidator` / `AddTextValidator` / `ResetToDefault` /
  `Validate` / `TrySubmit`; flags `AllowEmpty`, `IsSecret`, `MaskChar`,
  `Culture`; `MaskedText()` for echo.
- `FormConfirmState` — the yes/no special case (`DefaultChoice`,
  `TrySubmit(out bool choice, out string error)`).
- `IFormInputSource` is the only port: return `null` for EOF/dismiss.
  `QueueFormInputSource` covers tests, fixtures, scripted demos.

## Wiring

Extracted from `Harbor.Tui.CellForge/Chat/Widgets/` verbatim — byte-equal
file bodies, unchanged namespace. The binding stays where it was:

- `Harbor.Tui.CellForge` references this assembly (`DialogSelectList` in
  `Chat/Widgets/DialogFormWidgets.cs` builds `ListPromptState<string>` for
  its option dialogs; painting sites keep their `Harbor.Tui.CellForge.Widgets`
  usings — no using edits anywhere).
- `tests/Harbor.Tui.CellForge.Tests` keeps the engine tests where the
  goldens live and binds this assembly directly (the test files
  `ListPromptEngineTests` / `FormPromptEngineTests` did not move).
- No App.Cli / Hosting reference: the engines have no composition-story of
  their own — they are dialog state machines, reached through CellForge.

## Usage

The engines are consumed through `Harbor.Tui.CellForge`; direct consumers
(test harnesses, scripted demos that drive dialogs without a renderer) add
the ProjectReference and speak `Harbor.Tui.CellForge.Widgets` as before:

```csharp
var confirm = new FormConfirmState { DefaultChoice = false };
confirm.SetText("n");                 // host already captured the line
string error = confirm.Validate();    // "" == accept, run TrySubmit again for the value
confirm.TrySubmit(out bool choice, out string submitError); // choice == false
```

Build/test demand is ordinary workspace stuff: the project builds plain
(`dotnet build`), its tests live in `tests/Harbor.Tui.CellForge.Tests`
(run that project — Harbor tests run per project, not per solution).

## Dependencies

| Reference | Why |
|-----------|-----|
| `Harbor.Extensions` | the `Ensure` guard helpers (file-local alias `using Ensure = Harbor.Abstractions.Extensions.Ensure`) |
| `Harbor.Ui.Framework.Rendering` | the key vocabulary the list strategies/nav consume: `KeyEvent`, `KeyCode`, `KeyModifiers` and `KeyModifierGate.AcceptsTypedChar` — exactly one namespace, imported once in `GlobalUsings.cs` |

No NuGet packages: neither engine touches the `Result`/`Maybe` railway, no
theme store, no palette — the pair is BCL + the two references above.
Referenced by `Harbor.Tui.CellForge`, `tests/Harbor.Tui.CellForge.Tests`, and
`tests/Harbor.Architecture.Tests` (matrix/IL probes load every Presentation
row's assembly).

## Limitations

- **Not widgets.** No painting, no palette, no focus handling, no layout —
  an engine answers "what happens to selection state" and returns a repaint
  verdict; the owning CellForge views that render this state cannot move
  here without dragging `Rendering` vocabulary across (see the
  `ListPromptEngine` doc header: `ListPrompt`/`RenderHook` were never
  portable).
- **No key loop.** Key gating stays with the host (the
  `KeyGateFamilyRules` buffer gate classifies `ListPromptEngine`'s
  `AcceptsTypedChar` wording, but the host decides which keys arrive); a
  strategy's `HandleInput` consumes one already-mapped `KeyEvent`.
- **`FormPromptState` reads lines, not keys.** Empty-by-default, secret
  masking and choice lists ride the existing `IFormInputSource` port; an
  interactive consumer wraps its composer, it does not push strokes in.
- **Two XML doc crefs went inert with the move.** The `ListPromptEngine`
  header cites `DialogSelectList` / `CommandPaletteView` / `FilePickerView`
  (and `ScrollableViewport`) — those types stayed in the CellForge /
  Ui.Framework assemblies, so the cref links no longer resolve from here.
  CS1574 is suppressed repo-wide (`Directory.Build.props`, the same
  suppression #1617's extraction relied on) and the bodies stay
  byte-equal — zero line drift for the cites standing on these files — so
  the links are accepted dead weight until someone edits these comments.

## See also

- [`../Harbor.Tui.CellForge/README.md`](../Harbor.Tui.CellForge/README.md) — the renderer this assembly was split out of
- [`../Harbor.Tui.CellForge.Syntax/README.md`](../Harbor.Tui.CellForge.Syntax/README.md) — the sibling seam precedent (#1617)
- [`../Harbor.Ui.Framework.Rendering/README.md`](../Harbor.Ui.Framework.Rendering/README.md) — the key vocabulary
- [`../../docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) — layering rules
