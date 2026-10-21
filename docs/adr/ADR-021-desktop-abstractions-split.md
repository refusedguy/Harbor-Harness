# ADR-021: `Harbor.Desktop.Abstractions` is split, and the hub gets an honest name

> **Status (2026-10-20):** dated record, not normative. This file records the
> measurement that the decision rests on and the decision itself. The parts that
> must stay true are fenced where they live: the layer matrix in
> `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`, the config-leaf
> guard in
> `tests/Harbor.Architecture.Tests/DesktopAbstractionsLeafTakesNoIoRules.cs`, and
> the assembly's own `README.md`.
>
> **Update (2026-10-21), after the rebase onto the newer `dev`.** Three facts
> moved under the record, and the record is dated, so the deltas are written
> down here rather than edited into the measurement:
>
> 1. **The number is ADR-021, not ADR-020.** `dev` took ADR-020 for the
>    per-turn change oracle, so the split's record took the next free number.
>    The file, the title, the row in `DECISIONS.md` and every citation to it
>    moved with it.
> 2. **The `#188` edge now points at `Harbor.PolicyConfig`, not
>    `Harbor.Application`.** #1625 moved the configuration family — the
>    `ProviderPresets` catalogue among it — out of `Harbor.Application` into
>    `Harbor.PolicyConfig`. The catalogue's NAMESPACE stayed
>    `Harbor.Application.Configuration`, so the two view-models that read it
>    kept their `using` directives and only the project reference moved. The
>    measurement below was taken before that move, so it names
>    `Harbor.Application` where the tree now says `Harbor.PolicyConfig`.
> 3. **The namespace-ownership rule is now a build-time analyzer.** HARCH002
>    (`src/Harbor.Arch.Analyzers/NamespaceOwnershipAnalyzer.cs`) decides which
>    project owns which namespace root, and the split is consistent with it by
>    construction: the new root `Harbor.Desktop.ViewModels` is declared by the
>    new project of the same name, and the leaf keeps
>    `Harbor.Desktop.Abstractions` for the types it still holds. The text rule
>    (`AbstractionsNamespaceOwnershipRules`, R3) attributes every declaration in
>    the product tree to an assembly, and the new root is attributed to the new
>    project.

## Status

**Decided: split at the measured dependency seam.** `Harbor.Desktop.Abstractions`
keeps the config schema, the config-store ports, the cross-view-model messages
and the shared models/enums, and becomes a leaf with one Harbor reference. The
concrete desktop view-models move to a new assembly, `Harbor.Desktop.ViewModels`,
whose name says what it holds.

The issue asked for the owner's decision
([#1899](https://github.com/refusedguy/Harbor-Harness/issues/1899): *"нужно
решение владельца"*, three options, no preference stated). This ADR is the
decision record the issue asked for, and it recommends the split over the
rename on measured grounds (below). **Three questions remain the owner's**, and
they are listed at the end. None of them blocks the split: the split is the
part that does not depend on the answers.

## Context

`Harbor.Desktop.Abstractions` is a packable assembly
(`PackageId Harbor.Desktop.Abstractions`) whose name promises a layer of
contracts. Issue #1899 measured it on `origin/dev` = `283a9dbe` and found a
hub wearing a leaf's name: 9 project references and an 18-assembly dependency
closure, behind a package description that promised *"platform VMs derive from
the abstract VMs here"*.

### The re-measurement

The tree moved since the issue was filed (#1835 removed the dead bases, the
renderer removal reshaped the desktop layer, the TestKit split landed), so the
numbers below were taken again on `origin/dev` = `283a9dbe`:

| fact | issue #1899 said | measured on `283a9dbe` |
|---|---:|---:|
| `.cs` files | 39 | **30** |
| public types | 55 | **46** |
| project references | 9 | **9**, of which **2 bind no type at all** |
| dependency closure | (not measured) | **18 assemblies** |
| abstract bases | 13 | **3** view-model bases + 1 abstract record (`AppConfigBase`) |

The two dead references are `Harbor.Terminal.Abstractions` (no TUI vocabulary is
bound from it) and `Harbor.Ui.Framework` (an empty meta-assembly; the five
`Ui.Framework.*` modules are referenced directly). Both were already recorded
as vestigial edges in `EnforcerIntegrityTests` with the instruction *"Remove the
reference; see issue #543 / #542"*.

"Dead" means dead **in IL**, and that distinction cost one build to learn:
`Harbor.Terminal.Abstractions` bound no type from this assembly, but it was
load-bearing **transitively**. `tests/Harbor.TestKit/NullRenderContext.cs`
reaches `Harbor.Terminal.Abstractions.Renderers` through the leaf, and the
split made the leaf a two-assembly closure, so the build stopped with
*"The type or namespace name 'Terminal' does not exist in the namespace
'Harbor'"*. The fix landed where the file lives: `Harbor.TestKit.Tui`, the
UI-scoped part #1889 moved it to, already declares the reference. What the
build taught is that a reference can bind no type and still be the reason
another project compiles.

### The split the seam actually draws

The 46 public types divide by their dependency, not by taste:

| half | types | Harbor types it binds | where it lives | closure after |
|---|---:|---|---|---:|
| contracts, models, enums | 12 | `SessionStatus`, `AgentName.Fallback` (both in `Harbor.Abstractions.Contracts`) | the leaf | **2** |
| concrete view-models | 34 | `Harbor.Abstractions` (`IProviderHealthCheck`), `Harbor.Application` (`ProviderPresets` — the catalogue's assembly at the time of the measurement; #1625 moved it to `Harbor.PolicyConfig`, see the update above), five `Harbor.Ui.Framework.*` modules | the hub | 15 |

The 12 are the config schema (`IAppConfigStore<T>`, `ICommonConfigStore`,
`AppConfigBase`, `CommonConfig`, `CompositeConfig<T>`), the cross-view-model
messages (`ModelPickedMessage`, `OnboardingCompletedMessage`) and the shared
models/enums (`ThemeKind`, `ToastKind`, `SessionDotState`, `SessionDotStates`,
`CommandPaletteItem`). They bind **two** Harbor types, both in the innermost
assembly in the repository.

### What the split costs a consumer that only wants a contract

A consumer that wants `ICommonConfigStore` or `ThemeKind` and nothing else:

| | before | after |
|---|---:|---:|
| assemblies pulled in | **18** | **2** |
| …of which the preset catalogue's assembly (`Harbor.Application` at the time of the measurement, `Harbor.PolicyConfig` since #1625) | yes | no |
| …of which the `Ui.Framework` family | all five modules | no |

That is the whole of the issue's second complaint, and it is a real number: the
`#188` Presentation → Application edge (`ProviderPresets`) was on the path of
every config-contract consumer, because the two onboarding view-models that
read the catalogue sat in the same assembly as the schema.

### Two corrections to the issue's cost estimate

1. **There is no public-API baseline to re-sign.** The issue lists
   *"пере-подпись public-API baseline"* among the costs. The public-API freeze
   covers two assemblies: `Harbor.Abstractions` and `Harbor.Abstractions.Contracts`
   (each with its own `PublicAPI.Shipped.txt`). The desktop assembly was never in
   the freeze, so there is nothing to regenerate and nothing to re-sign.
2. **There are no external consumers today, and there never were.** The
   packages are `IsPackable` and CI packs them (`dotnet pack Harbor.slnx`), but
   the release workflow publishes only the CLI, and nuget.org has no Harbor
   package at all: the flat-container index for `harbor.desktop.abstractions`,
   `harbor.abstractions` and `harbor.abstractions.contracts` all answer 404
   (measured 2026-10-20). The breaking change this issue is worried about is
   therefore real in principle and empty in fact. It stops being empty the day
   a Harbor package is pushed to a feed, which is why the versioning question
   is still the owner's.

### The csproj promise, and what it cost to keep

The old csproj promised *"Platform VMs in apps/Harbor.App.* derive from the
abstract VMs here"*. After #1835 that promise was true of two of the three
surviving bases (`ChatViewModelBase` and `CommandPaletteViewModelBase` are
derived by the Avalonia app; `DiffViewModelBase` has only a TestKit stub), and
it is still true after the split: all three bases moved together with the
view-models.

## The options, with their measured cost

### (A) Rename the assembly

`Harbor.Desktop.Abstractions` → `Harbor.Desktop.ViewModels` (or
`Harbor.Desktop.Core`). One move, no duplication.

- **Mechanical cost:** the same 19 files move, the same ~56 files change their
  `using`, one matrix row is renamed, the same docs change. The rename shares
  its entire mechanical core with the split.
- **What it does not fix:** the closure. A consumer that wants
  `ICommonConfigStore` still pulls 18 assemblies, including
  `Harbor.Application` and the whole `Ui.Framework` family, because the
  contract and the view-models that read `ProviderPresets` are still in one
  assembly. The issue's second complaint survives the rename unchanged.
- **What it breaks:** the assembly-qualified identity of all 46 types, and the
  `PackageId`. Nothing published consumes them today.

### (B) Split at the dependency seam (chosen)

The leaf keeps the name `Harbor.Desktop.Abstractions` and the 12 contract
types; the 34 view-models move to `Harbor.Desktop.ViewModels`.

- **Mechanical cost over (A):** the leaf's csproj loses 8 references and the
  hub's csproj is written; the matrix gains a row and the `#188` exception
  changes key; the leaf's consumers (6 projects) keep compiling unchanged,
  because the leaf's namespaces and type identities do not move.
- **What it fixes:** both complaints. The name of each assembly says what it
  holds, and the leaf's closure is 2 assemblies instead of 18.
- **What it breaks:** the assembly-qualified identity of the 34 view-model
  types, and their namespace (`Harbor.Desktop.Abstractions.ViewModels` →
  `Harbor.Desktop.ViewModels`). No external consumer exists.
- **Bonus the issue did not predict:** the repository's own guard
  `DesktopAbstractionsLeafTakesNoIoRules` calls this assembly *"the config
  leaf"* and says the matrix *"allows no Harbor.Storage.* / Harbor.Tools.*
  edge, so there is nowhere for the leaf to call an infrastructure store"*. The
  guard's premise was a description of what the assembly should be; after the
  split it is a description of what it is.

### (C) Leave it and fix the description

Rejected by the issue author, and the measurement agrees: the name is the map a
reader navigates by, and a corrected description in a csproj comment is not a
map. It would also leave the 18-assembly closure and the `#188` edge in place.

## Decision

**Split (B).** The leaf's dependency closure drops from 18 assemblies to 2, the
`#188` edge moves off the leaf and stays scoped to the two files that read the
preset catalogue, and both assembly names become true.

The namespace of the 34 moved types moved with them
(`Harbor.Desktop.ViewModels`). Keeping the old namespace would have preserved
the source-level references, but it would have left two assemblies declaring
the same namespace root, which is the defect `AbstractionsNamespaceOwnershipRules`
was written to kill, and the issue's own complaint is that a name that does not
describe its content is worse than no name.

The leaf's single Harbor reference is `Harbor.Abstractions.Contracts`, not
`Harbor.Abstractions`: the two types it binds (`SessionStatus`,
`AgentName.Fallback`) both live in the contract assembly, and the facade
(`Harbor.Abstractions`) is not used at all. Pointing at the contract assembly
directly is what makes the closure 2 rather than 3.

### What the split does NOT do

- **It does not take the preset catalogue out of `Harbor.TestKit`'s closure.**
  The stubs that derive from `ChatViewModelBase` and `DiffViewModelBase` now
  live in the hub, and the hub carries the `#188` edge to the catalogue
  (`Harbor.Application` at the time of the measurement, `Harbor.PolicyConfig`
  since #1625). Moving those two bases down to `Harbor.Ui.Framework.ViewModels`
  is what would shrink that closure, and that is #1875's job, not this issue's.
  The `DiffViewModelBase` reservation row in
  `PublicAbstractBaseLivenessRules` now names its new home and still points at
  #1875.
- **It does not delete the dead public types.** The measurement found two:
  `ThemeKind` has **zero** references in the whole tree, and
  `Harbor.Desktop.Abstractions.Models.ToastKind` has zero as well, while
  `Harbor.Ui.Framework.Services` declares a second `ToastKind` that the app
  actually binds. Deleting published surface is the owner's call, so they stay.
- **It does not touch `Harbor.Abstractions.Contracts`.** The frozen contract
  assembly is referenced by the leaf now; not one byte of it changed.

## Consequences

- The leaf is a leaf: one Harbor reference, a two-assembly closure, and the
  `#188` debt is off it.
- `Harbor.Desktop.ViewModels` is a new packable assembly with a new
  `PackageId`. Nothing is published, so no consumer breaks; the name is a
  forward-looking commitment.
- `Harbor.Ui.Framework.Tests` and `Harbor.TestKit.Tui` reference the hub
  instead of the leaf; the core `Harbor.TestKit` references neither, because
  #1889 made it core-only. The `#188` edge that drags the preset catalogue in
  travelled with the view-models, so the part that still pays for it pays for
  it in the same place as before.
- The Avalonia app references both halves; the CLI, `Harbor.Hosting` and
  `Harbor.Desktop.Shared` reference the leaf only.

## What still needs the owner's sign-off

1. **The package identity and the versioning.** `Harbor.Desktop.ViewModels` is a
   new `PackageId`, and both assemblies are `IsPackable`. Nothing has been
   published (measured above), so today the choice costs nothing; it starts
   costing something the day a Harbor package reaches a feed. The owner decides
   whether the two packages ship at the same version, and whether the leaf
   keeps `0.4.0-alpha`.
2. **The deletion of `ThemeKind` and `Harbor.Desktop.Abstractions.Models.ToastKind`.**
   Both are unreferenced. The second duplicates a type that already exists in
   `Harbor.Ui.Framework.Services`. Removing published surface is a deliberate
   act, so this PR leaves both in place and records the measurement.
3. **Whether the leaf should be called `Harbor.Desktop.Contracts`.** The issue's
   own option (B) text keeps the name `Harbor.Desktop.Abstractions` for the
   leaf, so this implementation follows the issue. `Harbor.Desktop.Contracts`
   would be marginally more precise, but it would sit next to the frozen
   `Harbor.Abstractions.Contracts` and blur two different things.

## References

- The issue: [#1899](https://github.com/refusedguy/Harbor-Harness/issues/1899).
- The matrix row that carries the decision:
  `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`.
- The guard whose premise the split makes true:
  `tests/Harbor.Architecture.Tests/DesktopAbstractionsLeafTakesNoIoRules.cs`.
- The bases that would still move the preset catalogue out of the TestKit
  closure: [#1875](https://github.com/refusedguy/Harbor-Harness/issues/1875).
- The related decision on the narrow config reader:
  [ADR-009](DECISIONS.md).
