# Writing an analyzer rule in this repository

This document is the repository's answer to one question: where does the file
go, and what has to be true of it, so that a rule written from this document
passes the gates.

It is not a Roslyn tutorial. Roslyn's own documentation explains the API. This
document explains the conventions of THIS repository, which differ from the
Roslyn defaults in ways that are not guessable and that cost a pull request
each the first time somebody met them.

Read it before writing the first rule. Read the non-vacuity section before
writing the first test.

## Why this guide exists

The pilot that put the first rules here into Roslyn analyzers showed where
writing a rule in this repository actually trips you up. Three of those traps
are worth writing down, because each one costs a pull request of its own.

**Placement is not free.** A new file in the wrong place breaks gates that have
nothing to do with the rule it implements. A new assembly in the architecture
test's output directory changes a measured baseline that another rule compares
as an exact string, so an unrelated rule goes red.

**A diagnostic id is the switch.** The reason a rule gets its own id is that
`.editorconfig` configures severity by id. A rule that cannot be switched off
without switching off its neighbours gets switched off together with them.
That is the whole reason the ids are one per rule.

**A rule that cannot fail is worse than no rule.** A rule that matches nothing
is indistinguishable from a rule that is broken, and a broken rule is believed.
Every rule here therefore carries controls that prove it can fail, and the
controls are part of the rule, not an optional extra.

Each of the first ten rules of the second wave would otherwise rediscover
these. This document is what makes them one shape instead of ten.

This document is a separate file rather than a section of `ANALYZERS.md`
because the two have different readers and different jobs. `ANALYZERS.md` is
the policy home: which analyzers are adopted, at what severity, and why, and
it is a table-heavy document that changes whenever a package moves. This
document is the procedure you follow before you write a rule, and it changes
when the conventions change, which is much rarer. A reader deciding whether to
adopt a package wants the first; a contributor writing a rule wants the
second. `ANALYZERS.md` links here.

## Which kind of rule are you writing

There are three substrates, and the invariant decides which one you need.

| The invariant is about | Substrate | Home |
|---|---|---|
| a file (its path, its presence, a csproj entry, a line of text) | source scan | `tests/Harbor.Architecture.Tests/` |
| a symbol (a type, a call, a resolved property, a discarded value) | Roslyn analyzer, run in-process | `tests/Harbor.Architecture.Tests/Roslyn/` |
| the same, enforced on every build | build-time analyzer | `src/Harbor.Arch.Analyzers/` |

The decision rule in one sentence: if the fact you need is a type, write an
analyzer; if it is a line of text, write a source scan.

### What each substrate buys and costs

A source scan needs no Roslyn dependency and no semantic model. It costs a walk
of the tree, and the architecture test project pays that walk once per rule,
which is why it is the slowest test project in the repository: issue #1791
measured 738 s of the repository's 1 128 s of test time inside it.

A source scan also sees things an analyzer cannot see at all: where a file
lives, whether a directory exists, what a csproj declares, whether a line of
text names an API. What it cannot do is grade spelling. `Assembly.Load`
written with spaces, behind a using alias, or fully qualified is a different
token, and a source scan needs a comment stripper and a near-miss control for
each spelling it does not recognise.

A Roslyn analyzer grades the symbol. `Assembly.Load` is one fact whatever the
spelling, and the analyzer sees the declaring project, because one compilation
is one assembly. That is the fact a file walk cannot recover, and it is why
the text rule stating the namespace-ownership invariant needed a third
sub-rule to work around its own blindness. The analyzer path pays for itself
once: it parses the tree once per project and shares the compilation between
every rule that uses it, which is what the pilot measured.

The trade-off is not "analyzer good, scan bad". It is that a source scan sees
files and an analyzer sees types. A rule that needs a file is a source scan. A
rule that needs a type is an analyzer.

When the choice is not obvious, write the source scan and say in the rule's
header why the analyzer was not used. A rule that states its own limit is a
rule a reader can trust.

## Where the rule lives

Three placements, each with a price.

| Placement | What it costs |
|---|---|
| `tests/Harbor.Architecture.Tests/Roslyn/` | nothing. This is where an in-process analyzer belongs today. |
| a new test project, `tests/Harbor.Architecture.Analyzers/` | the new assembly lands in the architecture test's `bin` directory, and `ScanUniverseRule.NonProductMembers_MatchTheMeasuredBaseline` compares that inventory as an exact string. Somebody else's baseline has to move. |
| `src/Harbor.Arch.Analyzers/` | a row in `FullLayerMatrixTests.OutOfScopeAssemblies` with a reason, a declaration in `FullLayerMatrixTests.SourceGeneratorProjects`, and the src-assembly count stated in `docs/ARCHITECTURE_LAYERS.md` §5.4. Three files outside the rule, each with a reason that is not yours to write. |

The pilot chose the first, and the choice is the right default for any rule
that can fire on existing code. The reason is the order of operations. A
build-time analyzer runs on every build of every product project, and this
repository builds with `TreatWarningsAsErrors` turned on. A rule that fires on
code that is already written turns the build red before anybody has read the
rule, and the cheapest repair for a red build is to switch the rule off.

### The promotion path

Promotion is a file move, not a rewrite. A `DiagnosticAnalyzer` is an ordinary
class, and the same class runs in both places: `Compilation.WithAnalyzers`
runs it in-process against a compilation the test builds, and the compiler can
load the very same class as an analyzer later.

To promote:

1. Move the analyzer to `src/Harbor.Arch.Analyzers/`, a `net10.0` project with
   `Microsoft.CodeAnalysis.CSharp` and `PrivateAssets=all`, exactly like
   `src/Harbor.CodeGen`.
2. Add the project to `FullLayerMatrixTests.OutOfScopeAssemblies` with its
   reason, so `EnforcerIntegrityTests.SrcProjects_AreAllClassified` accepts
   it.
3. Reference it from the product projects with
   `<OutputItemType>Analyzer</OutputItemType>` and declare it in
   `FullLayerMatrixTests.SourceGeneratorProjects`.
4. Give the rule a severity row in `.editorconfig`, starting at `suggestion`.

**This path has been walked once.** The first two rules to be promoted are
`HARCH001` and `HARCH002` (#1842), and the project, the classification, the
wiring and the severity rows are what this section describes. What the walk
added to this document is the answer to a question the pilot could not answer:
the rules are wired through `src/Directory.Build.props` and
`apps/Directory.Build.props` rather than through every project file, because a
reference repeated in sixty project files is a reference that sixty project
files can forget. The wiring is read by the build, and it is asserted by a
test, so "the analyzer is enabled" is a checked statement and not a hope.

The severity row of a promoted rule follows the ladder in
[the severity section](#severity) below: `suggestion` first, then the
findings are fixed or baselined, then `warning`, then `error`. A rule whose
findings are already known to be empty — because the text rule it replaces
still gates the same tree, and the differential agrees with it — may start one
step further up, and the row says which step it started at and why.

### The package

`Microsoft.CodeAnalysis.CSharp` is already pinned in `Directory.Packages.props`
for `src/Harbor.CodeGen`, so the architecture test project adds a
`PackageReference` with no version and no new entry in the central package
management file. The dependency gate over the four guarded projects does not
cover the architecture test project, so nothing there changes.

## What the rule may read

The rule lives in a test project, and the test tree may do things the product
may not.

Reading product assemblies by reflection is allowed in tests, and the layering
rules depend on it: `LayerDependencyTests` walks the referenced assemblies of
every product project, `PresentationCapabilityRules` walks their IL with
Mono.Cecil, and `ScanUniverseRule` reads the assemblies the test host has
loaded. A rule in `tests/` is not bound by the product conventions.

Two conventions bind the product, and one of them binds a promoted rule.

**The dynamic-code family is banned in `src/` and `apps/`**, except under
`src/Harbor.Plugins.*`. The compiler enforces it, through the `RS0030`
diagnostic over `BannedSymbols.DynamicCode.txt`. Reflection in general is not
banned: string-based member access over a `JsonElement` is a JSON key, not a
CLR member, and the classification that would be needed to rule on that form
is not done.

**A promoted analyzer is shipped code.** Once the analyzer lives in `src/`, it
may not use the dynamic-code family, and it may not reflect over the product
assemblies, because the product does not exist yet when the analyzer runs. That
is the reason promotion is a separate, deliberate step and not a toggle.

**Every `src/` project must be classified.** `FullLayerMatrixTests` puts each
one on the layer matrix or in `OutOfScopeAssemblies` with a reason, and
`EnforcerIntegrityTests.SrcProjects_AreAllClassified` fails when a project is
on neither list.

## The diagnostic

### One id per rule, and one rule per id

The id is the switch. `.editorconfig` sets severity with
`dotnet_diagnostic.<ID>.severity`, and a pragma silences one id. A rule that
shares its id with another rule cannot be demoted, disabled or suppressed
without doing the same to its neighbour, and a rule that cannot be turned off
on its own is a rule that gets turned off with the family.

The id is also what a reader greps for. `HARCH001` means one rule.

Ids must be unique across the whole tree, not only within one folder.

> **The id rule has teeth, and it needed them.** Two analyzers in the
> architecture test project declared the same id (`HARCH003`) until
> 2026-10-20: the unobserved-Task rule and the unguarded-`Result` rule.
> Neither could be demoted without demoting the other, which is the whole
> failure this rule exists to prevent. The unguarded-`Result` rule is
> `HARCH004` now, and `NamespaceOwnershipControlTests` fails if any id is ever
> declared by two analyzers again.

### The descriptor

Build the descriptor positionally. The argument names of
`DiagnosticDescriptor`'s constructors are not a contract this repository can
check, and a named argument that turns out to be spelled differently is a
build break in a project that cannot be built locally.

```csharp
private static readonly DiagnosticDescriptor Rule = new(
    "HARCH004",                       // id. Severity is configured by id.
    "Dynamic code is forbidden in shipped code",
    "'{0}' loads or manufactures code at run time, which is invisible to NativeAOT",
    "Architecture",                   // the category every rule here reports under
    DiagnosticSeverity.Warning,
    true,                             // isEnabledByDefault
    "AGENTS.md requires NativeAOT-readiness: no reflection emit, no Assembly.Load.",
    null,                             // helpLinkUri
    []);                              // customTags
```

The nine arguments are, in order: id, title, message format, category,
default severity, enabled by default, description, help link, custom tags.

**The message names the symbol.** `'{0}'` is the symbol the rule is about, and
the message format starts with it. A failure message that says what to do and
not what it found costs the reader a trip to the file.

**The category is `Architecture`.** That is what makes the
`dotnet_diagnostic.<ID>.severity` rows read as one table instead of several.

### The rule does not know about the exceptions

An analyzer sees one compilation. A perimeter is a statement about a set of
projects, and an exception is a statement about a site. Both belong to the
caller, which owns the scope and the gate.

So the analyzer reports everything it sees, and the caller decides what counts.
The pilot's `HARCH001` reports every dynamic-code site, including the plugin
family, and the test filters by project, exactly the way a text rule filters
its own baseline.

The exception is different when it is a property of the symbol itself. "Do not
flag the value types in `System.Reflection.Emit`" describes the type, so it
belongs in the rule. "Do not flag the plugin family" describes the project, so
it belongs in the caller. Getting this backwards is how a rule becomes
unportable to build time, where the set of projects is different.

### Waivers

**One site.** `#pragma warning disable HARCH004 // the reason`, the same form
`BannedSymbols.txt` uses for `RS0030`. The reason is not optional, and a
pragma without a reason is indistinguishable from a suppression somebody forgot
to remove. `AGENTS.md` forbids blanket pragma disables.

**One rule.** A `dotnet_diagnostic.HARCH004.severity` row in `.editorconfig`.
The file already carries a row for every adopted analyzer family, so the
mechanism exists; a new rule adds a row.

## Severity

Severity is a decision with a reason, and the reason lives in a table.

**One row per diagnostic id.** `docs/ANALYZERS.md` states one row per package
at the top, and one row per id inside each family's table, because that is the
granularity a reader needs: a row that says only "warning" tells a reader
nothing they could not have guessed. A rule written here needs its own family
table with one row per id, each row saying what the rule catches and why the
severity is what it is. The family block states the reason once in prose and
the rows under it carry the rule's own short reason.

**The severity table lives where the diagnostic lives.**
`docs/ANALYZERS.md` §[Where the severity table lives](./ANALYZERS.md#where-the-severity-table-lives)
is the authority on which file carries which row: `.editorconfig` for a
diagnostic reported on a source tree, `Directory.Build.globalconfig` for one
reported on an additional file. A severity written anywhere else reaches
nothing.

**The reason is part of the row, not a comment above it.** A comment above a
row is invisible to every tool that reads the table, so the reason is lost the
moment the next person reformats the file. The row format is in
`docs/ANALYZERS.md` §[The row format](./ANALYZERS.md#the-row-format).

**A new rule is never enabled on the tree without fixing or baselining its
findings.** This is the rule that decides the order of operations. A rule
promoted to build time at `error` fails every build that touches a file it
flags, and the failure lands on whoever pushed next. The ladder is:

1. `suggestion`. The rule runs and reports, and the findings are collected.
   Nothing fails. This is where a promoted rule starts.
2. Fix the findings, or baseline them with a reason per row.
3. `warning`. Visible in the build log, and promoted to an error by
   `TreatWarningsAsErrors`.
4. `error`, once the tree is clean.

One rule at a time, and the severity change lands in the same commit as the
fix. `docs/ANALYZERS.md` §[How the baseline is lowered](./ANALYZERS.md#how-the-baseline-is-lowered)
is the procedure.

A rule that is armed over the whole tree on the day it is written is a rule
that gets switched off. Where a rule cannot be armed over the whole tree yet,
arm it over a named perimeter with a reason per entry, and say so in the
caller, which is where the perimeter belongs.

## Non-vacuity

A rule that matches nothing is indistinguishable from a rule that is broken,
and a broken rule is worse than no rule because it is believed. Every rule
here carries controls that prove it can fail.

### The three controls

**A planted offender.** Synthetic source that the rule must report. It is
compiled and graded by the real matcher, not by a copy of the matcher.

**A planted near-miss.** Synthetic source that the rule must stay silent on,
and it must be a separate source and a separate compilation. The near-misses
are the shapes the rule is supposed to leave alone: prose that names the
banned API, a member access that is not the banned one, a value that looks
like the banned one but is not.

**A differential against the rule it replaces.** When a rule replaces a text
rule, the two are compared over the live tree, in both directions. The new rule
must see every file the old rule sees, and must see the ones the old rule
cannot. The comparison uses the old rule's own detector, never a copy of it,
because a second implementation of the same patterns agrees with itself and
proves nothing.

The old rule then survives as a non-gating oracle. It gates nothing; it exists
so the differential can run.

### The live gate has to be live

A gate over the live tree that reports nothing is not the same report as a
gate that looked at nothing. Two controls close that gap:

**The planted offender is compiled into the gated project.** The control
source is added to the compilation the gate grades, so a rule that stopped
seeing that project's files reports the planted offender and fails. Without
this, "no findings" and "the analyzer saw nothing" are the same sentence.

**The discovery floor.** The scope must yield at least a stated number of
files, and must contain named files the rule is about. A scope rooted at a
typo finds zero files and then passes everything.

### Exemptions carry reasons, and stale exemptions fail

Every tolerated violation states why it is tolerated, in the row itself. A row
without a reason is an oversight, and the reason is what makes it a decision
rather than a leak.

`ExemptionReason.cs` is the one place that asks whether a tolerated row states
a reason, and it asks three questions: the reason is not blank, it is not the
tracking issue URL again (a URL says where the debt is tracked, not why it is
tolerated, so it belongs in its own column), and it is at least 40 characters
long. `later`, `TODO`, `see above` and `n/a` all pass a blank check and all
say nothing.

An exemption that no longer has a violation behind it is not an exception any
more. It is a permission for a problem that no longer exists, and it fails.

### The rules about the rules

`EnforcerIntegrityTests.cs` is the layer that watches the other rules, and it
is where a new rule should look for the shape of its own controls:

| Test | What it catches |
|---|---|
| `RepositoryInventory_IsDiscoverable` | a csproj-walking rule silently passing because the repository root was not found |
| `RuleTargetAssemblyNames_AllExist` | vacuous targets, where a typo or a deleted project in a forbidden list satisfies the rule forever |
| `SrcProjects_AreAllClassified` | a project in `src/` that is on no list at all |
| `Matrix_AllowedEntries_AreLive` | allowed-set rot, where a permitted edge nobody uses stays in the matrix |
| `DocumentedExceptions_AreScopedToNamedFiles` | a project-wide exception hiding new violations |
| `DocumentedExceptions_AllHaveReasons` | a reasonless, accidental exception |

The vacuity trap is the one to remember. `NotHaveDependencyOn("X")` and a
reflection-based "find references to X" both treat a name that resolves to
nothing as a satisfied constraint, so a typo'd or deleted name reads as a
passing rule.

## The checklist

Before a rule is proposed, every line of this should be answerable.

1. The invariant is about files or about symbols, and the substrate matches.
2. The rule lives in `tests/Harbor.Architecture.Tests/`, or the promotion cost
   is stated and accepted.
3. The id is unique across the tree, and it names one rule.
4. The descriptor is built positionally, the category is `Architecture`, and
   the message names the symbol.
5. The rule reports everything it sees. The perimeter and the exceptions are in
   the caller, and each exception has a reason.
6. A planted offender is reported.
7. A planted near-miss, in its own source and its own compilation, is silent.
8. If the rule replaces a text rule, the differential runs in both directions
   against the old rule's own detector, and the old rule survives as a
   non-gating oracle.
9. The live gate is proven live: a planted offender compiled into a gated
   project is reported.
10. The scope has a discovery floor and names the files the rule is about.
11. Every tolerated violation states a reason, and a stale tolerance fails.
12. The rule is not armed over the tree without its findings fixed or
    baselined, and the severity row says why.
13. `docs/ANALYZERS.md` has a row for the id, with the reason, in the family's
    table.

## See also

- [ANALYZERS.md](./ANALYZERS.md) is the policy home: which analyzers are
  adopted, at what severity, and why.
- [ARCHITECTURE_LAYERS.md](./ARCHITECTURE_LAYERS.md) §5 is the catalogue of
  architecture rules, §5.5 is the rules about the rules, and §5.8 is the
  reflection convention.
- The `Roslyn/` folder of the architecture test project holds the analyzers
  that exist today, and each analyzer's header explains why it is shaped the
  way it is.
- `NamespaceOwnershipControlTests.cs` is the control set this guide was
  validated against: the planted offender, the planted near-misses, and the
  uniqueness of every diagnostic id in the tree.
- The `Roslyn/` folder of `tests/Harbor.Architecture.Tests/` is where an
  in-process analyzer goes; `UnobservedTaskRuleTests.cs` is the fullest
  example of the control set a rule is expected to carry.
