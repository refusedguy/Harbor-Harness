# `DialogOverlay`: what the `_kind` enum actually is

Measurement for #473 (`[GoF-A2] DialogOverlay: State pattern replaced by _kind enum + 41 branch points`).
Companion to #979 / #472 (`DialogKeyDispatchCensusRule.cs`), which measures the **key-dispatch
shape**. This document measures the two things #473's own framing rests on and #979 does not
cover: whether a State pattern ever existed to be replaced, and whether the branches are
reachable.

Numbers below were re-derived against `dev@e3ec4b96`. Nothing is copied from the issue text.

---

## 1. The "State pattern replaced by `_kind` enum" premise is false — nothing was replaced

The issue title asserts a substitution: a State pattern (inheritance, polymorphism, more
classes) was *replaced* by a flat enum. The first version of the file already had the enum.

```
d0e192e7  341 lines  3 kinds   9 `_kind` lines   <- first version of DialogOverlay
```

`git show d0e192e7:src/Harbor.Tui.CellForge/Chat/Widgets/DialogOverlay.cs` line 9 is
`public enum DialogKind` and line 42 is `private DialogKind _kind = DialogKind.Alert;`. There
was never a per-kind class to replace it.

Searched the entire history for the classes a State pattern would have used:

| Symbol | Commits touching it |
|---|---|
| `IDialogState` | **0** |
| `DialogAlertState` | **0** |
| `DialogSelectState` | **0** |

The only `DialogState` string in the repository's history is `PermissionDialogState` in
`specs/07-tui.md:889` — prose in a spec, in a different feature area, never code.

So the answer to #473's framing question — *is the enum an improvement over a State pattern,
or one complexity swapped for another?* — is **neither**. There was no State pattern to lose.
The `_kind` enum is the original design, and the branch count grew with it as kinds were added:

| Commit | Lines | Kinds | `_kind` lines | Change |
|---|---|---|---|---|
| `d0e192e7` | 341 | 3 | 9 | first version, enum already present |
| `51172caa` | 361 | 3 | 9 | seated on overlay stack |
| `305724dc` | **914** | **6** | **45** | PRIM4 form primitives (+553 lines, +36 `_kind` lines, +3 kinds) |
| `54535b7e` | 1194 | 7 | 50 | approval modal |
| `298f83a1` | 1203 | 7 | 50 | key-release gate (#784) |

**One feature commit accounts for most of the growth.** `305724dc` (PRIM4: SelectList /
Radio / multiline input) took the file from 361 to 914 lines and 9 to 45 `_kind` lines. The
"god-object" shape is the cost of three new dialog kinds, not the residue of a bad refactor.
That is a different finding from "41 branch points" and it changes what a fix would cost.

## 2. The 41 branch points: 50 lines, 51 occurrences, and they are not 41 decisions

`_kind` occurs **51** times on **50** lines (one line, `:536`, uses it twice). Neither number
is 41; the issue's figure is stale in the low direction, as its own file length is (1194/1149
claimed vs 1203 measured).

Decomposition by role — this is the part that matters, because the roles are not equal:

| Region | Branches | What it is |
|---|---|---|
| `Show*` factories + field decl | 8 | writers, not decisions |
| Accessors (`:95`–`:349`) | 10 | pure reads, one ternary each |
| `HandleKey` **both overloads** (`:426`–`:676`) | **25** | **one table, written twice** |
| `Paint` if-chain (`:918`–`:936`) | 5 | one axis |
| `ControlRows()` (`:1019`) | 1 | one switch |
| field + `Kind` property | 2 | — |

Of the 25 key-path branches, **22 sit inside `HandleKey(in KeyEvent)`** and are the second
axis of a single `switch (key.Key)` — a key-by-kind table, not 22 independent decisions. The
legacy overload walks the *same* table state-major in two steps (`:444`, `:449`, then the
5-way `_kind switch` at `:464` into five per-kind handlers).

**Separating "enumeration of enum values" from "independent conditions":** all 25 are
conditions on `_kind` against a 7-member enum. They are **0 independent conditions** and
**25 cells of one lookup**. Genuinely independent logic in the same region — the
modifier gates on Enter (`:537`, `:538`) and the command-modifier gate on `Char` (`:649`) —
does **not** mention `_kind` and is therefore not in the 41 at all.

## 3. Reachability: this is where the finding is, and it is second-order

#979 measured which `DialogKind` values product code can show, and found 5 of 7 have no
product caller. Verified independently, and it holds — but it is an **undercount of the
problem**, because it measures one level.

Level 1 (what #979 measured): `.ShowAlert` (2 sites) and `.ShowPrompt` (4) are the only
`.Show*` calls in `src/` + `apps/`; `Confirm`, `Select`, `Radio`, `Multiline`, `Approval` are
reachable from tests only.

Level 2 (not previously recorded): **all six of those product call sites are inside
`OnboardingFlow`, and `OnboardingFlow` is never constructed in product.** Every construction
form returns zero hits outside `tests/`:

| Form | Hits in `src/` + `apps/` |
|---|---|
| `new OnboardingFlow` | 0 |
| `GetService<OnboardingFlow>` / `GetRequiredService<…>` | 0 |
| `AddSingleton<OnboardingFlow>` | 0 |
| `typeof(OnboardingFlow` | 0 |
| any non-comment mention outside its own file | 0 |

The commit that added it (`e82e5c7e`, "first-run onboarding wizard on DialogOverlay
primitives", `Fixes #268`) touched **one file** — it added `OnboardingFlow.cs` and nothing
else. Its message says "saving is left to the wiring slice"; the wiring slice never landed.

The onboarding that *is* live is a different class in a different layer:
`Harbor.Application.Onboarding.OnboardingWizard` (registered at
`Harbor.Hosting/Modules/CoreModule.cs:39`, resolved by `SetupVerb`, `SetupCommand`,
`ReplRunner`). It contains **no** `Dialog` reference at all — it reads and writes config
directly, no overlay.

**So zero of the seven kinds are reachable from a running product binary.** Every one of the
41 branch points, and all 51 `_kind` occurrences, are reachable only from tests.

The second `DialogOverlay` instance does not rescue this. `ChatScreen.Dialog` (`:763`) *is*
constructed in product (`CellForgeModule.cs:79` builds `ChatScreen`), and it is seated on the
overlay stack — but no product code calls any `.Show*` on it, so `Visible` is permanently
false, and `DialogOverlayLayer` takes the `IOverlayLayer.OnKey` default (`=> false`), so it
never receives a key either. This matches what #812's `OverlayKeyPlaneCensusRule` already
recorded as "REGISTERED BUT PARKED" — the finding here is the **other** instance, and the fact
that the `.Show*` sites themselves are dead.

## 4. No behavioural bug: the Alert row is a correct answer, not a defect

#979 flags Alert as "the sharp row" — shown by product code, appearing in neither key path,
so every key either takes the unconditional `Escape`/`Tab` prefix or falls to `_ => false`.
Verified: it is **correct**, and deliberately so.

* `ShowAlert` (`:130`) adds exactly **one** button. `CycleFocus` over a 1-element list is the
  identity (`_focusedButton = (_focusedButton + 1) % 1`), so `Tab`/arrows have nothing to
  move and returning through them is right.
* `ControlRows()` returns 0 for Alert and `Paint` falls through to `DrawButtons`, so the box
  reserves no control rows — documented at `:1017`.
* Alert's commit gesture is `Enter`, and `OnboardingFlow.HandleKey` (`:198`) intercepts
  `Enter` *before* delegating to `Dialog.HandleKey`, committing the step itself. Alert
  therefore never needs a branch of its own.

There is no `_kind` branch that yields wrong behaviour. #775 already fixed the one real defect
in this area (the legacy overload typing a character the kitty overload refused, via a missing
modifier gate). **The split #473 asks for is not owed a bug report.**

## 5. Why nothing is refactored here

The proposed remedy is out of bounds while #555 holds. Per-kind widget classes would live in
`src/Harbor.Tui.CellForge/Chat/Widgets/`, which #555 freezes by name ("a new cellforge widget
primitive … overlay layers … are the set") — the same wall #945 measured seating a panel as a
layer. #555 explicitly permits what this is: documentation that reduces drift between
hand-written records and the code.

And the measurement changes the decision rather than merely supporting it. If the branches are
unreachable from a product binary, then splitting them is not a refactor of a live design — it
is deciding what to do with code whose only consumer is its own test suite. That is a product
decision (wire onboarding to `DialogOverlay`, or delete the dialog primitives and their 4 000
lines of tests), and it is bounded by #555 either way. **This issue's own framing — "State
pattern replaced by enum" — does not survive contact with the tree, so the split it asks for
should not be done on those terms.**

## Numbers

| Claim in #473 | Measured | Verdict |
|---|---|---|
| State pattern replaced by `_kind` | `_kind` present in first version; 0 commits for `IDialogState`/`Dialog*State` | **premise refuted** |
| 41 branch points | 51 occurrences / 50 lines / 25 in `HandleKey(in KeyEvent)` | one table, written twice |
| 1194 lines | 1203 | stale |
| seven kinds each deserve own handler | 0 of 7 reachable from a product binary | product decision, not a refactor |
| "most dangerous god-switch in the UI layer" | no behavioural defect found | nothing owed a bug |