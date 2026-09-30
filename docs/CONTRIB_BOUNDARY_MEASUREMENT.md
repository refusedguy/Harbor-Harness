# The `contrib/` boundary: what connecting it to CI would actually cost

> Status (2026-10-01): measurement snapshot. A dated record, not a normative
> claim about the current implementation. Nothing in `contrib/` was modified and
> nothing in `contrib/` was added to a build, to produce this document.

Issue #915 asks one question that is an owner decision, not a mechanical one:
**should `contrib/` join the build?** This document prices that decision. It does
not make it.

The question exists because the boundary is invisible to every gate in this
repository, and the invisibility is structural rather than accidental:

- `contrib/` is in no solution any workflow builds. `Harbor.slnx` does not
  reference it, and neither `Harbor.Samples.slnx` nor `contrib/Contrib.slnx` is
  named by any file under `.github/`.
- `SourceScan.IsBuildOutput` rejects `/contrib/` outright
  (`tests/Harbor.Architecture.Tests/SourceScan.cs:66`), and
  `SourceScan.ProductTrees` is `["src", "apps"]`
  (`tests/Harbor.Architecture.Tests/SourceScan.cs:47`). Every repository scan
  therefore walks past `contrib/` entirely.
- `DiffSurfaceNameCollisionProbe.SkippedDirectories` prunes `contrib` by name
  (`tests/Harbor.Architecture.Tests/DiffSurfaceNameCollisionRule.cs:205`).

`contrib/` holds **279 tracked files**, of which **164 are `.cs`** and **26 are
`.csproj`.** None of them has ever been compiled by CI.

## How this was measured, and what that cannot see

**No compilation took place.** A local `dotnet build` of any kind was out of
scope, so the method was a static resolution pass over the whole repository:

1. Index every namespace and every simple type name declared anywhere in the
   repository, attributed to the owning project.
2. For each `contrib/` project, compute its transitive `ProjectReference`
   closure.
3. Strip comments with a lexer equivalent to the repository's own
   `SourceCommentStripper` (block, line, XML doc, string, verbatim, char), then
   resolve every remaining capitalized identifier against the project's own
   types plus its closure.

Comment stripping is not cosmetic here. The unstripped pass reports 85
cross-boundary names; the stripped pass reports 18. Of those 18, hand-checking
every one leaves **6 real errors and 12 false positives** (`Task`,
`builder.Build()`, `[RelayCommand]`, `System.Windows.Input.ICommand`,
`Markdig.Syntax.CodeBlock`, a property named `Theme`, and string literals such as
`"Harbor.Test"`). Anyone quoting a number from this area without stripping
comments first will be wrong by a factor of five.

**What this method cannot see, stated plainly:** deleted members, changed method
or constructor signatures, generic arity, overload resolution, analyzer and
warnings-as-errors failures, and anything a type's *body* requires. Those need a
compiler. The numbers below are a floor, not a total.

## 1. How many projects, and in what state

`contrib/Contrib.slnx` lists **22 active projects**. Four more exist on disk and
are commented out of the solution, with the solution's own header saying why:

| group | active | commented out |
|---|---|---|
| `tui/` | 7 | 0 |
| `scripting/` | 6 | 0 |
| `apps/` | 1 | 2 |
| `tests/` | 8 | 2 |
| **total** | **22** | **4** |

The four commented-out projects are `Harbor.App.Maui`, `Harbor.App.Wpf` and
their two test projects. They target **`net10.0-windows`** and
**`net10.0-windows10.0.19041`**, so they cannot build on `ubuntu-latest` — the
only runner the `build` job uses — regardless of their source. They need the
Windows Desktop and MAUI workloads. They are excluded for a structural reason,
not because their code is broken.

**All 22 active projects target portable `net10.0`.** Nothing in the active set
is excluded for platform reasons. `Contrib.slnx`'s claim that it builds on
Linux and macOS is therefore not contradicted by the target frameworks; whether
it builds at all is a different question, and §2 answers it for four projects.

**State: 4 of 26 projects on disk definitely do not compile. 2 of the 22 active
in `Contrib.slnx` definitely do not compile.** The remaining 22 are *not*
certified good — they are simply not disproven by a static pass.

## 2. What breaks, and whether there is a common cause

There are exactly two causes, and both are small. This is the finding that
matters most, because "one moved thing" is the difference between a bounded cost
and an open-ended one.

### Cause A — `Harbor.Scripting.Tests` imports a namespace that no longer exists

`contrib/tests/Harbor.Scripting.Tests/GlobalUsings.cs:4` reads:

```csharp
global using Harbor.Abstractions;
```

No project in the repository declares a namespace literally `Harbor.Abstractions`
any more. `src/Harbor.Abstractions` was split into fifteen sub-namespaces
(`Harbor.Abstractions.Tools`, `.Agents`, `.Sessions`, `.Events`, …) and the flat
one went with it. This is **CS0246**, and it is the only dead `Harbor.*` import in
all 164 `contrib/` `.cs` files — every other one resolves.

**Price: delete one line.** It is a `global using`, and the file already imports
the specific sub-namespaces it needs on lines 5 through 10.

### Cause B — three desktop apps use a `Harbor.Hosting` type without referencing `Harbor.Hosting`

`JsonAppConfigStore<T>` is declared at
`src/Harbor.Hosting/Configuration/JsonAppConfigStore.cs:61` and
`JsonCommonConfigStore` at `src/Harbor.Hosting/Configuration/JsonCommonConfigStore.cs:73`.
All three `contrib/` desktop apps construct both, in real code:

| project | sites | in `Contrib.slnx`? |
|---|---|---|
| `Harbor.App.Blazor` | `Program.cs:143`, `Program.cs:163` | yes |
| `Harbor.App.Wpf` | `App.xaml.cs:209`, `App.xaml.cs:229` | no (Windows-only) |
| `Harbor.App.Maui` | `MauiProgram.cs` | no (Windows-only) |

None of the three has `src/Harbor.Hosting` in its reference closure, and none
imports `Harbor.Hosting.Configuration`. **CS0246**, twice per project.

This is a missing *edge*, not a moved type, and it is legal to add: the product
desktop app `apps/Harbor.App.Avalonia` references `src/Harbor.Hosting` at
`apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj:91` and constructs the same
two types. Both constructor signatures still match the call sites, so nothing
downstream of the reference has drifted.

**Price: one `ProjectReference` and one `using` per project, three projects.**

### What this does to #759's claim

#759 reported that three desktop apps in `contrib/` stopped building. The
measurement refines it rather than confirming it: two of those three
(`Harbor.App.Wpf`, `Harbor.App.Maui`) target Windows-only frameworks and are
already excluded from the solution, so they are not "broken", they are
*unbuildable on this runner for a reason unrelated to their source*. The third,
`Harbor.App.Blazor`, is the only broken one in the active set, and it is broken
by the same one missing reference as the other two.

So the honest restatement is: **one broken project in the active set, for one
reason — plus one dead `using` in a test project.** That is a far smaller price
than "three desktop apps stopped building" implies, and it is the single most
decision-relevant fact in this document.

### Two checks that came back clean

- All **86** `ProjectReference` edges out of `contrib/` (61 of which leave
  `contrib/` and point into `src/`) resolve to a project that exists on disk.
  Zero broken edges.
- Every `PackageReference` in `contrib/` has a central version in
  `Directory.Packages.props`, and every `Compile` / `None` /
  `EmbeddedResource` `Include` names a file that exists. Zero broken.

One apparent finding dissolved on inspection and is recorded so nobody repeats
it: four `contrib/` test projects contain `[Test]` attributes and declare no TUnit
package, which looks fatal. It is not — `Directory.Build.targets:18` adds
`<PackageReference Include="TUnit"/>` to every project whose name ends in
`.Tests`, and `contrib/tests/Directory.Build.props` imports the repo root, so the
package is there implicitly.

## 3. Did the #908 `src/*` literal bug reach `contrib/`

**No, and it could not have.** The two questions are separable and this one has a
structural answer.

#908's agent found that a `"src/*"` string literal made the comment stripper read
a file as being inside a block comment, and that 845 lines of real code across 5
files were blanked. #919 fixed the lexer. The question here is whether any of
that damage landed in `contrib/`.

It did not, for a reason that does not depend on which files the stripper
happened to be handed: **no rule can feed `contrib/` to the stripper.** Every
stripper consumer reaches files through `SourceScan`, which filters through
`IsBuildOutput`, and that function rejects `/contrib/`. The only walk in the
suite that reaches `contrib/` at all is the one #863 added, and it does so by
explicitly taking `contrib` back out of a skip set
(`tests/Harbor.Architecture.Tests/ContribBoundaryNameRule.cs:128`). No caller
passes a `contrib` root to `SourceScan.EnumerateCsFiles`.

The stripper's own header records the blast radius as `src/` + `apps/`
(`tests/Harbor.Architecture.Tests/SourceCommentStripper.cs:245`) — which is
exactly the set it is able to see. The bug and the boundary are the same
boundary. **`contrib/` is undamaged by #908.**

## 4. The third option: a rule that enumerates and never compiles

This is possible, and it was measured rather than assumed. The question is not
whether such a rule can be written — it is whether it produces a signal worth
having on a tree that does not build.

**What an enumeration-only rule can decide, and what it found here:**

| check | result |
|---|---|
| a `using` / `global using` naming a namespace no project declares | **1** (Cause A) |
| a `ProjectReference` that does not resolve on disk | 0 |
| a `PackageReference` with no central version | 0 |
| an `Include` naming a missing file | 0 |
| a `contrib/` project in no solution any workflow builds | **26** (the meta-fact) |

**What it cannot decide:** Cause B. Nothing in `contrib/apps/Harbor.App.Blazor/Program.cs`
names an assembly. The missing `Harbor.Hosting` reference is invisible to
enumeration, because a file that uses a type never states where the type lives —
only a compiler or a full type resolver connects the two. Signature drift, deleted
members and analyzer failures are invisible for the same reason.

**Verdict: feasible, cheap, and not a substitute.** Measured against the four
projects that are known-broken, an enumeration-only rule finds **one of the four**
— 25% of the known-broken set, and one of the two broken projects in the active
solution. It is a genuine ratchet on the hole, in the same spirit as #863's
cross-boundary name count, and it would stop this boundary from *growing*. It
would not tell the owner whether `contrib/` builds.

## 5. The decision, for the owner

Three options, priced. This is a decision, not a task list.

**Option 1 — connect `contrib/` to the build gates now.**
Price: fix Cause A (1 line) and Cause B (3 projects × 2 edits), then hold 22
projects to a zero-warning `TreatWarningsAsErrors` build that has never been
held to one. The two known causes are cheap; the unknown residue is not, because
it is exactly the class a static pass cannot see. `Contrib.slnx` also pulls four
Windows-only projects that need a second runner and two workloads, so this is not
a one-line `ci.yml` edit. **This is the option whose price is currently
unknowable, and the one the brief most feared.**

**Option 2 — leave `contrib/` out, and record the hole once.**
Price: zero build minutes. What it does *not* do is protect the tree: #863's
ratchet only counts names declared on *both* sides, so duplication with both
copies inside `contrib/` — `MarkupCache` has 19 live call sites and two
byte-identical copies under `contrib/tui` — has no guard at all. The honest
version of this option is Option 2 **plus** the enumeration-only ratchet from §4,
which is the cheapest thing that makes the hole non-silent.

**Option 3 — measure once, then decide.**
`.github/workflows/contrib-dryrun.yml` in this change does exactly this: a
`workflow_dispatch`-only, never-gated job that builds each of the 26 `contrib/`
projects and prints a per-project pass/fail table into the job summary. It exits
0 by design, so a red table is a measurement rather than a broken build. One
dispatch converts this document's static floor into a compiled fact, at the cost
of one non-gating CI run.

**Recommendation, as an owner decision with a price: Option 3, then re-decide.**
The measurement says the *known* price of connecting is small and has a common
cause, which is good news and was not the working assumption. But "small known
price" is not "small total price", and the gap between them is precisely the part
no static pass can reach. One dispatch of a non-gating job closes that gap for
the price of one CI run, and it does so without touching `contrib/` and without
putting 279 uncompiled files behind a merge gate. If the table comes back with
only the two known causes, Option 1 becomes a small, well-understood change; if
it comes back with a third cause, the decision was never as cheap as it looked
and nothing has been spent to find out.

## What is not established here

Stated so the next reader does not have to guess which parts are load-bearing:

- **Not verified by compilation.** No project in `contrib/` was built. Cause A and
  Cause B are read off the source and the project graph and are CS0246 by
  construction; the other 22 projects are unclassified, not clean.
- **Not checked:** member and signature drift, deleted members, generic arity,
  analyzer and warning-as-error behaviour, and anything a method body requires.
  §4 bounds this class as invisible to enumeration; it does not measure it.
- **Not checked:** whether the 22 active projects *restore* on a clean runner, and
  whether any needs a workload the comment in `Contrib.slnx` does not mention.
- **One incidental observation, not a finding:** several `contrib/tests/Harbor.E2E.Tui.*`
  files contain the absolute path `/mnt/projects/Harbor-Harness/...` as a
  screenshot directory. It is a string literal, so it compiles. It is the kind of
  thing a dry run surfaces and a static pass does not.
