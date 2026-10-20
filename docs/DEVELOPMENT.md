# Development Guide

## Prerequisites

- .NET 10 SDK (https://dot.net)
- Git
- Any modern IDE (VS Code, Rider, Visual Studio)

## Getting started

```bash
git clone --recurse-submodules https://github.com/refusedguy/Harbor-Harness.git
cd Harbor-Harness
dotnet build
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1
```

## Project structure

See [docs/ARCHITECTURE.md](./ARCHITECTURE.md) for the full layout.

## Build commands

```bash
# Build all projects
dotnet build

# Build a specific project
dotnet build src/Harbor.Application
dotnet build apps/Harbor.App.Avalonia

# Build Release configuration
dotnet build -c Release

# Clean build artifacts
dotnet clean

# Disk-space-constrained sandbox / CI: clear bin/obj before a full build
find . -type d \( -name bin -o -name obj \) -not -path '*/node_modules/*' -exec rm -rf {} +
```

## Test commands

> **Known limitation:** `dotnet test` is not used in this repo — every CI job
> runs each test project as a plain executable, one at a time:
> `dotnet run --project tests/<Project> -c Release --no-build -- --minimum-expected-tests 1`.
> The earlier wording here ("discovers ZERO tests … the `dotnet test` → MTP
> bridge exits 5 with a silent discovery error — verified per project on SDK
> 10.0.302") was **wrong**: exit 5 is the MTP *invalid command-line arguments*
> code and a genuine zero-discovery run exits 8, so exit 5 was never evidence
> of zero discovery. See [CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test)
> for the two real causes and for what is still unverified.

```bash
# Build everything first (tests use --no-build)
dotnet build

# Run a specific test project (known-good pattern)
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --minimum-expected-tests 1
dotnet run --project tests/Harbor.Plugins.Runtime.Tests -c Release --no-build -- --minimum-expected-tests 1

# Run one test class — TUnit uses --treenode-filter, not --filter (forwarded after --)
dotnet run --project tests/Harbor.Abstractions.Tests -c Release --no-build -- \
  --treenode-filter "/*/*/IdentifiersTests/*"

# Detailed output
dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- --output Detailed

# Enforce layer-dep rules (architecture tests)
dotnet run --project tests/Harbor.Architecture.Tests -c Release --no-build -- --minimum-expected-tests 1
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) v1.61.0 (pinned in `Directory.Packages.props`; the Microsoft Testing Platform runner version arrives transitively via TUnit and is not pinned here). Test files are in `tests/<Project>.Tests/`.

### Known test status

- **Pre-existing Avalonia 12 headless failures** (`MarkdownRenderer_SetMarkdown_DoesNotThrow`, `CodeBlock_Default_Code_IsEmpty`, `TypewriterStreamingText_CanSet_Text` — "Stack empty" in `SetInheritanceParent`), plus an occasional flaky pair `ChatView_Inflates` / `TryGet_ReturnsNullForUnregistered`. Not Harbor bugs — see ROADMAP backlog.
- **IPC named-pipe event-stream tests on Linux** (`Harbor.Ipc.Tests`) self-skip unless `HARBOR_IPC_EVENTSTREAM=1` is set; some timing flakes remain.

## Documentation checks

Two stdlib-only Python 3 gates (no pip install, no network, no dotnet) run on
every markdown change, in CI and locally:

```bash
./tools/check-md-links.py          # do internal links + #anchors resolve?
./tools/md-lint.py                 # do the files obey the markdown invariants?
./tools/check-md-links.py --self-test   # prove the checker still fails on a bad link
./tools/md-lint.py --self-test          # prove the lint still fails on bad input
```

A third stdlib-only gate asks a different question, about the architecture rule
tree rather than about markdown: **may an architecture rule anchor on a line
number?** A `file:line` is a coordinate, and every line inserted above it moves
the coordinate, so the anchor stops naming what its author meant. The
architecture tree was converted to symbolic anchors (`Type.Member`, plus the
condition that makes the site the one being talked about) in #1728, and this
gate is what keeps it converted.

```bash
./tools/check-arch-anchors.py          # may an arch rule point at a line number?
./tools/check-arch-anchors.py --self-test   # prove the gate still fails on a planted anchor
```

The anchors it excuses are the ones where the number **is** the checked fact,
and each one carries a reason in
[`tools/arch-anchor-exceptions.txt`](../tools/arch-anchor-exceptions.txt) — a
ratchet in both directions, so an exception the tree no longer produces is red.

`check-md-links.py` resolves every relative link, `#anchor` and
`path/File.cs:123` reference in the tracked markdown and reports the ones that
do not exist; external URLs are skipped on purpose (network flakiness is worse
than no gate). `md-lint.py` checks encoding (LF, no BOM), heading structure
and fences; its header lists the rules it enforces **and** the markdownlint
rules it deliberately does not, each with the measured hit count that ruled it
out.

Relative links break silently: a doc that renames or moves is still valid
markdown, it just sends the reader to a 404.

### It IS in CI now — `.github/workflows/docs.yml`

`ci.yml` lists `**.md` and `docs/**` in `paths-ignore`, so a docs-only PR never
starts a build. That is why the markdown gates live in a second workflow
instead of `ci.yml` (issue #509): `docs.yml` triggers on `**.md` plus the gate
scripts themselves and runs three jobs in ~30s with no .NET SDK.

| Job | What it asserts |
|---|---|
| `checker self-test` | both gates still **fail** on fixtures that are known-broken |
| `internal links` | `check-md-links.py --min-files 250 --min-refs 600` |
| `markdown lint` | `md-lint.py --min-files 250 --min-lines 60000` |
| `test duplication` | `check-test-dup.py --min-files 800 --min-methods 9000` (#1778) |
| `duplication (jscpd)` | `check-dup-jscpd.py` — the second duplication detector, over `src/` + `apps/` + `tests/` (#1847) |
| `reflection JSON` | `check-test-json-reflection.py` — no test serializes through the reflection resolver (#1822) |
| `local squash-merges dropped nothing` | `check-local-merge-loss.py` — no local squash-merge dropped content that is still missing |

Three things make this a real gate rather than decoration:

1. **The failure path is tested.** `--self-test` builds a throwaway git repo
   containing a document with a missing link, a bad anchor and a lint error,
   then asserts the exit code *and* the diagnostic. A checker edited until it
   matches nothing goes red here instead of reporting "clean" forever.
2. **A narrowed scan is a failure.** The `--min-*` floors are set ~20% below
   today's numbers (311 files, 770 link refs, 74109 lines). Scanning zero
   files, or a tenth of them, exits 1 — a script that examined nothing cannot
   report that everything is fine. The floors are a ratchet: a PR that really
   removes documents lowers them in the same diff, where a reviewer sees it.
3. **No allow-list.** There is no set of files to skip and no
   `continue-on-error`. A broken link in a docs-only PR is a red check.

Verify it end to end before trusting it: add a link to a file that does not
exist, `git push`, and watch the `internal links` job fail on the PR.

> **Do not "fix" this by editing `ci.yml`.** Removing `paths-ignore` would
> rebuild the whole solution for a typo in a paragraph, and `docs.yml` would
> still be the only thing linting markdown. The two workflows are meant to
> coexist.

### Local squash-merges

A pull request that touches `.github/workflows/` cannot be merged through the
GitHub API with this repository's token: the token has no `workflow` scope, so
the API refuses the merge. Those pull requests were merged LOCALLY instead —
`git merge --squash` onto `dev`, then `git push origin HEAD:dev` — with the
conflicted files resolved by hand.

The resolution kept the YAML valid and the build green, and it dropped lines.
The conflicted file was taken WHOLESALE from the pull request branch, and the
branch was older than `dev`, so everything `dev` had added to that file since
the merge base went with it. Measured on dev's history, the gate below found
six files: `CHANGELOG.md` (three sections), `tools/README.md` (one table row),
`.github/workflows/docs.yml` (one self-test step), `docs/ANALYZERS.md` (one
section) and `.github/workflows/ci.yml` (the absolute `--results-directory` and
two reporting steps).

```bash
./tools/check-local-merge-loss.py            # is anything still missing?
./tools/check-local-merge-loss.py --self-test # prove the checker still fires
```

The gate reads the history and asks, for every local squash-merge, two
questions about each file the pull request touched:

1. is every line the pull request ADDED still in the tree?
2. is every line `dev` had before the merge that the pull request did not
   remove still in the tree?

The pull request's own diff is `merge-base..<head>`, never the merge commit's
diff: the merge commit's diff is against `dev` and says nothing about what the
pull request intended. The head SHA is in the merge commit's own body, so the
gate needs no network; the CI job fetches `refs/pull/<n>/head` for the merges
it names.

A line the pull request removed is not a loss. A file the pull request deleted
is not a loss. A line an ordinary commit removed on purpose is not a loss.
Content that came back in another form is not a loss, and those are named, one
per line with a reason, in [`tools/merge-loss-exceptions.txt`](../tools/merge-loss-exceptions.txt).

### Who owns which markdown fact

Two gates touch `src/**/README.md`, and they are deliberately disjoint:

| Gate | Asserts | Runs in |
|---|---|---|
| `ReadmeCoverageTests` ([tests/Harbor.Architecture.Tests](../tests/Harbor.Architecture.Tests)) | every `src/**/*.csproj` has a README; packable READMEs carry the six sections of [docs/standards/README-template.md](./standards/README-template.md) | the dotnet build (core shard) |
| `tools/check-md-links.py` | every link and anchor in those READMEs resolves | `docs.yml` |
| `tools/md-lint.py` | encoding, heading and fence structure | `docs.yml` |

`ReadmeCoverageTests` never parses a link, and neither Python gate looks at the
template sections — so a failure always names exactly one gate to fix. Keep it
that way: adding a link check to `ReadmeCoverageTests`, or a section check to
`md-lint.py`, would give two gates an opinion about the same fact, and two
gates that can disagree are worse than one.

### The gate policy — why the gates are python, and what the compiler takes

[docs/CI_GATES.md](./CI_GATES.md) is the policy for the python tools in
`tools/`: why they are python (a gate has to run where the SDK and the build
are not, which is a docs-only pull request and the pull request that breaks
the build), what the compiler takes over instead (a check that needs a type or
a symbol is a Roslyn analyzer in the build, as the public-API freeze became
in #1845), what every gate owes (a self-test with a planted control, a
ratchet whose floor only goes down, a deterministic exit, a message that names
the violator), and what a gate must not do (measure time, duplicate another
gate, require dotnet).

The page carries a table of every python tool CI runs, and
`tools/check-gate-policy.py` is the gate over that table: it reads the table,
the scripts and the workflow files, and fails when they disagree. So the
policy is not a paragraph a reviewer has to remember.

```bash
./tools/check-gate-policy.py --self-test   # each rule still fires
./tools/check-gate-policy.py --min-gates 35
```

A new `tools/check-*.py` needs the "why python" line in its header, a row in
that table, and a self-test step in `docs.yml`. The checker says which of the
three is missing.

## Test duplication gate — issue 1778

`tools/check-test-dup.py` finds duplicated **test bodies** in `tests/` and
fails when a NEW one appears. It is the mandatory tail of epic #1808: that
epic removes ~21 000 lines of copy-pasted test code, and without this gate the
copy-paste comes straight back.

```bash
./tools/check-test-dup.py --report        # census + every duplicate group, exit 0
./tools/check-test-dup.py --self-test     # prove the gate still fails on a planted duplicate
./tools/check-test-dup.py --update        # regenerate the baseline after the programme lands
./tools/check-test-dup.py --new-since origin/dev   # only groups touching files this branch changed
```

**How it matches.** Every method body in `tests/**/*.cs` (minus `bin/`, `obj/`
and `tests/Harbor.Architecture.Tests/`, which belongs to programme #1724) is
lexed, then normalised: comments dropped, string literals → `S`, char literals
→ `C`, numeric literals → `N`, identifiers → `I`. Bodies with the same
normalised shape form a group. A group only counts as duplication when its
members also share at least half their **identifier vocabulary** — two bodies
can be shaped alike and have nothing to do with each other, and a coincidence
is not a duplicate. The thresholds are `--min-lines 6` (a body must carry six
code lines; a three-line assert helper is a legitimate small overlap) and
`--min-similarity 0.50`.

**Why it is in baseline mode.** The tree carries duplication today: the census
in `tools/test-dup-baseline.json` records 228 groups and 4 232 removable code
lines. A gate that is red on the current tree is a gate nobody lands, so the
gate fails only on duplication that is NEW relative to that baseline or that
GREW past the copy count the baseline recorded. The comparison is on the
**members** of a group — the paths and names of the methods in it — and never
on the text of their bodies: editing a duplicated test changes its text, and
an edit is not new duplication. The baseline only ever goes **down**: as
issues #1769–#1777 land, `--update` lowers it, and the floor tightens with
it. Zones other programmes own are exempted explicitly, with a reason and an
expiry date, and an expired exemption fails the gate.

## The second duplication detector — issue 1847

`tools/check-dup-jscpd.py` is a **second** detector. It does not replace
`check-test-dup.py`, and it is not a second copy of it: the two engines
answer different questions, and the answers only partly overlap.

```bash
./tools/check-dup-jscpd.py --run                  # download the pinned binary, scan, compare
./tools/check-dup-jscpd.py --self-test            # prove the gate still fails on an empty scan
./tools/check-dup-jscpd.py --update               # re-measure the baseline after the tree changes
./tools/check-dup-jscpd.py --run --baseline-ref origin/dev   # add the new-clone delta
```

**What it is.** jscpd 5.4.1 (MIT, a self-contained Rust binary) matches
duplicated **token windows** rather than duplicated method bodies, over the
whole maintained C# surface: `src/`, `apps/` and `tests/`, minus `bin/`,
`obj/`, `contrib/` (unmaintained, not compiled by CI), `samples/`, `tools/`,
`build/`, `analyzers/` and `tests/Harbor.Architecture.Tests/` (the zone of
programme #1724). The thresholds are in [`.jscpd.json`](../.jscpd.json):
`--min-tokens 100`, `--min-lines 6`, `--mode weak` (comments are prose, not
code) and the `exact` kind, so a clone is a block that was copied byte for
byte.

**What it finds that the python gate cannot.** The python gate slices a
METHOD BODY out of the file, so duplication that is not a method body is
invisible to it: a duplicated record declaration, a duplicated nested class, a
duplicated interface implementation, a duplicated file header. And the python
gate scans `tests/` only, so the product code has no duplication gate at all.

**The measured comparison** (both tools on the same tree, `origin/dev`):

| | files | clones / groups | duplicated lines | wall |
|---|---|---|---|---|
| `check-test-dup.py` (`tests/` only) | 1017 | 206 groups | 2987 | 14.0 s |
| `check-dup-jscpd.py` (`src/` + `apps/` + `tests/`) | 1971 | 286 clones | 8353 | 1.2 s |

Over the test tree the two overlap on 1965 lines: 30.0% of the python gate's
duplicated lines, 23.3% of jscpd's. 37 of the python gate's 206 groups sit
entirely inside a jscpd clone, 25 partly and 144 not at all. Neither is a
subset of the other — jscpd flags 94 files the python gate does not, and the
python gate flags 113 files jscpd does not. jscpd is also an order of
magnitude faster, which is why it is affordable to run on every PR that
touches the tree.

**Why the rule is a total and not jscpd's own `--fail-on-new-clones`.** jscpd
ships a baseline gate, and issue #1847 proposed it. It was measured on 13 real
commits of `dev` before it was ruled out, and it does not work on this tree:
the baseline is keyed on a **content hash** of each clone, so editing a
duplicated body changes the hash and the edit is reported as new duplication.
Commit `8bde0944` changed one line in each of two files that already
duplicated each other, and jscpd reported 4 new clones — 3 of them pairs that
were already clones BEFORE the commit. The gate was red on 1 of the 13 commits
with the `exact` configuration and on 3 with the `renamed` one, and every one
of those was ordinary work that added no duplication. The python gate was
written to avoid exactly this: it compares the **members** of a group, never
the text of the bodies.

So the rule here is the aggregate one the python gate also has
(`DUP-GROWTH`): the tree's duplicated **lines** must not exceed the number
`tools/jscpd-baseline.json` records. Editing a duplicated body does not
change that total; adding a copy of it does. Measured on the same 13
commits, the total moved only when the tree really changed, and it did not
move at all when a duplicated body was edited — which is the property the
fingerprint baseline lacks.

**The rules.**

| Rule | What it does |
|---|---|
| `DUP-FLOOR` | the scan analysed fewer than 1700 files — a scan that measured nothing must not be green |
| `DUP-GROWTH` | the tree's duplicated lines are above the baseline's |
| `DUP-BASELINE-STALE` | the tree's duplicated lines are below the baseline's: the tree got cleaner, and `--update` lowers the baseline |
| `DUP-MEASUREMENT` | the config or the tool version moved since the baseline was written, so the two numbers are not comparable |
| `DUP-NEW-CLONES` | the clones that are new relative to the base ref — reported, and blocking only with `--fail-on-new-clones`, because the number is edit-sensitive |

The floors are a ratchet: 1971 files are analysed today and the floor is
1700, because the programme is about to REMOVE test code, and a floor set at
the measured count would go red on the removal PRs themselves. A PR that
really removes files lowers it in the same diff, where a reviewer sees it.

**The binary.** It is fetched from the release, sha256-pinned per platform
inside the tool, and it is a build-time tool: it never reaches the product and
no `PackageReference` is added, so the dependency gate does not see it. The
report is uploaded as the job's artifact — it is the work list the next
duplication ticket needs, with the file, the line range and the size of every
clone.

## Reflection-JSON gate — issue 1822

`tools/check-test-json-reflection.py` answers one question about every
`JsonSerializer` call under `tests/`: **does it name a source-generated type
info, or does it take the reflection resolver?**

```bash
./tools/check-test-json-reflection.py --report     # the census, exit 0
./tools/check-test-json-reflection.py --self-test  # prove the gate still fails on a planted call
./tools/check-test-json-reflection.py             # the gate itself, exit 1 on a violation
```

**Why it exists.** The product side of System.Text.Json already rides source
generation — 21 `JsonSerializerContext`s live under `src/` (#1565, #1779). The
TEST side did not: 62 calls in `tests/` took the reflection resolver, while
22 sibling calls in the same files already rode `JsonlCodecContext.Default.*`,
`RemoteJsonContext.Default.*` or `McpJsonSerializerContext.Default.*`.

The cost is not only CPU. A reflection call is **not checked at compile
time**: a misspelled member name compiles, serializes to a DIFFERENT key, and
the test then asserts on the wrong field and stays green. A source-generated
context names the type and its members, so the compiler sees them. That is
the false-green class this gate exists to close.

**How it decides.** The scan reads `tests/**/*.cs` (minus `bin/`, `obj/` and
`tests/Harbor.Architecture.Tests/`, which is programme #1724's zone), strips
comments and string literals, and keeps every `JsonSerializer.Serialize`,
`Deserialize`, `SerializeToElement` and `SerializeToUtf8Bytes` call. A call
passes when its arguments mention `…Context.Default`, `…Context.Options`,
`…Context.JsonOptions` or `GetTypeInfo(` — the four shapes a source-generated
type info takes in this tree.

**Exceptions are named, justified, and never silent.**
`tools/test-json-reflection-exceptions.txt` holds one exception per line:

```
<path relative to the repo root> | <text that must appear in the call> | <reason>
```

The file is a **ratchet**: an exception the tree no longer produces fails the
gate (`EXCEPTION-STALE`), so a closed hole cannot stay blessed. It can only
shrink.

## Package-set checks (test and sample projects)

`tools/check-test-packages.py` answers one question about every project under
`tests/` and `samples/`: **which packages may it reference, directly or
transitively, and is any banned one among them?**

It exists because nothing else answered it (issue #1882). `BannedSymbols.txt`
bans `N:Newtonsoft.Json`, but that file is an RS0030 analyzer input: it covers
source code only, and RS0030 is switched off for `tests/` and `samples/` in
`Directory.Build.props`, so a *transitive* dependency could put Newtonsoft into
a test project's `bin/` with no rule noticing. `tools/check-abstractions-contract.py`
covers four `src/` projects, `tools/aot-publish-gate.py` covers `src/` and
`apps/`, and the rule in `AGENTS.md` is prose.

```bash
dotnet restore Harbor.slnx        # the gate reads what the restore resolves
./tools/check-test-packages.py --min-projects 43 --min-declared 40 --min-packages 1400
./tools/check-test-packages.py --self-test   # prove the gate still fails on a banned package
./tools/check-test-packages.py --report      # the census, per project
```

**How it reads the graph.** Two sources, and the difference matters. The
`<PackageReference>` elements of the `.csproj` are the *direct* half and need
nothing but the file. `obj/project.assets.json` — the graph NuGet writes during
*restore* — is the *transitive* half: every package in the closure, its
version, and the exact asset paths that will be copied into the output
directory. No build is involved and no network is needed.

**What "banned" means.** The banned names come from `BannedSymbols.txt`, not
from a second list: an `N:` entry names a namespace, and the package that
ships it is the namespace itself (`N:Newtonsoft.Json` bans the package
`Newtonsoft.Json` and the assembly `Newtonsoft.Json.dll`). To ban another
package, add its `N:` entry there.

**Exceptions are named, justified, and never silent.**
`tools/test-package-exceptions.txt` holds one exception per line:

```
<package or assembly name> | <project glob> | <reason> | <owner>
```

Every field is mandatory — an exception with no reason is reported as
`EXCEPTION-UNJUSTIFIED`, and one that matches nothing is reported as
`EXCEPTION-STALE`, so the file cannot accumulate permissions that no longer
apply.

**The Newtonsoft case.** It is deliberately **not** an exception. The
repository bans Newtonsoft outright, so the treatment of a package that drags
it in is to **drop that package**, not to except it. Measured on
`dev@69fb5c80`: `NetArchTest.Rules` 1.3.2 (already referenced by
`tests/Harbor.Architecture.Tests`) declares exactly one dependency,
`Mono.Cecil` (its nuspec says 0.11.3; the repository declares the package
itself and pins 0.11.6 since #1865), and does not bring Newtonsoft — and if
it ever did, the layering rules have a zero-dependency reflection fallback
already
(`tests/Harbor.Architecture.Tests/LayerDependencyTests.cs`).
`TngTech.ArchUnitNET` 0.13.4, proposed in #1841, *does* declare
`Newtonsoft.Json` 13.0.4; if that proposal is accepted with the package, this
gate goes red and the answer is either to not take the package or to record,
in the exception file, why a test-only Newtonsoft is worth having. What must
not happen is the package arriving quietly.

**In CI.** Two places, because the question has two answers:

| Job | What it asserts |
|---|---|
| `test/sample package set` (`ci.yml`) | the resolved package graph of every test and sample project — `check-test-packages.py --min-projects 43 --min-declared 40 --min-packages 1400`, after `dotnet restore` |
| `build` (`ci.yml`, one step after the solution build) | the output directories the build produced — `check-test-packages.py --output-only --min-projects 43 --min-output-dirs 40` |

The first answers "which packages may this project pull in, directly or
transitively" from the graph NuGet resolved. The second answers the issue's
literal question — "is the forbidden DLL in the output directory" — against
the bytes the build actually produced. They are separate mechanisms: a file
can reach an output directory without being a package in the graph.

The `test/sample package set` job is triggered by the `packages` path filter
in the `changes` job: a `.csproj` under `tests/`/`samples/`,
`Directory.Build.props`, `Directory.Packages.props`, `BannedSymbols.txt`, the
gate, or its exception file — that is every way the package set of a test
project can move.

## Running the CLI

```bash
# Set API key
export ANTHROPIC_API_KEY=sk-ant-...

# Run interactive REPL
dotnet run --project apps/Harbor.App.Cli

# One-shot prompt
dotnet run --project apps/Harbor.App.Cli -- ask "What is 2+2?"

# List providers
dotnet run --project apps/Harbor.App.Cli -- providers

# List models
dotnet run --project apps/Harbor.App.Cli -- models
dotnet run --project apps/Harbor.App.Cli -- models anthropic

# Show help
dotnet run --project apps/Harbor.App.Cli -- help
```

## Configuration

### Provider configs

Located in `providers/` (project-local) or `~/.harbor/providers/` (user-global):

```jsonc
{
  "id": "openrouter",
  "displayName": "OpenRouter",
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "OPENROUTER_API_KEY",
  "modelsUrl": "https://openrouter.ai/api/v1/models"
}
```

### API keys

Set via env vars (conventional names):

```bash
export ANTHROPIC_API_KEY=sk-ant-...
export OPENAI_API_KEY=sk-...
export OPENROUTER_API_KEY=sk-or-...
export DEEPSEEK_API_KEY=...
export GROQ_API_KEY=gsk_...
# etc.
```

### Default model

```bash
export HARBOR_MODEL=anthropic/claude-sonnet-4-20250514
# or
export HARBOR_MODEL=openai/gpt-4o
# or
export HARBOR_MODEL=openrouter/anthropic/claude-3.5-sonnet
```

## Common development tasks

### Add a new builtin tool

1. Create `src/Harbor.Tools.Builtin/Tools/<Name>/<Name>Tool.cs` (sealed class, implements `ITool`).
2. Register in `src/Harbor.Hosting/Modules/ToolsCatalog.cs` —
   `tb.AddTool(lf => new YourTool(lf.CreateLogger<YourTool>()));`.
3. Add a rule to `PermissionRuleset.Default`
   (`src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`).
4. Add tests in `tests/Harbor.Tools.Builtin.Tests/YourToolTests.cs`.
5. `dotnet build && dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build`.

See [TOOLS_CATALOG.md §5](./TOOLS_CATALOG.md#5-building-your-own-tool--webfetchtool-walkthrough) for the full walkthrough.

### Add a new LLM provider (JSON-only)

Create `providers/<name>.json` and set env var. Done.

### Add a new test

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.YourNamespace.Tests;

public class YourTests
{
    [Test]
    public async Task Method_State_ExpectedResult()
    {
        var result = SomeMethod();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("expected");
    }
}
```

### Add a new project

1. Create `src/Harbor.<Name>/` directory.
2. Create `.csproj` file referencing `Harbor.Abstractions` (and `Harbor.Application` / `Harbor.Registries` if needed).
3. Add `GlobalUsings.cs` if helpful.
4. `dotnet sln add src/Harbor.<Name>/Harbor.<Name>.csproj`.
5. Create corresponding test project in `tests/Harbor.<Name>.Tests/`.

## Workflows — end-to-end recipes

### Workflow: add a feature end-to-end (example: `time` tool)

Покажем все 7 шагов на примере добавления `time` tool (возвращает текущее UTC время).

#### Step 1: Create the tool

```bash
mkdir -p src/Harbor.Tools.Builtin/Tools/Time
cat > src/Harbor.Tools.Builtin/Tools/Time/TimeTool.cs << 'EOF'
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

public sealed class TimeTool : ITool
{
    private readonly ILogger<TimeTool> _logger;
    public TimeTool(ILogger<TimeTool> logger) => _logger = logger;

    public ToolName Name => ToolName.Create("time");
    public string DisplayName => "Time";
    public string Description => "Returns the current UTC time in ISO-8601.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "time: Get current UTC time";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

    public JsonDocument ParameterSchema { get; } =
        JsonDocument.Parse("""{"type":"object","properties":{}}""");

    public Result ValidateArguments(JsonElement args) => Result.Success();

    public Task<ToolResult> ExecuteAsync(
        JsonElement args, ToolContext context, CancellationToken ct = default)
    {
        _logger.LogDebug("Time tool invoked");
        var now = DateTimeOffset.UtcNow.ToString("O");
        return Task.FromResult(ToolResult.Success(now, new { iso8601 = now }));
    }
}
EOF
```

#### Step 2: Register in DI

Edit `src/Harbor.Hosting/Modules/ToolsCatalog.cs` (`CreateToolRegistry`):

```csharp
internal static ToolRegistry CreateToolRegistry(
    HarborCompositionContext ctx, IMcpRegistry mcpRegistry, IAgentRegistry agentRegistry)
{
    var registry = new ToolRegistry();
    var tb = new ToolRegistryBuilder(registry, ctx.LoggerFactory);
    // ... existing tools
    tb.AddTool(lf => new TimeTool(lf.CreateLogger<TimeTool>()));  // ← NEW
    registry.Freeze();
    return registry;
}
```

#### Step 3: Write tests

```bash
cat > tests/Harbor.Tools.Builtin.Tests/TimeToolTests.cs << 'EOF'
using Harbor.Tools.Builtin;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class TimeToolTests
{
    [Test]
    public async Task Execute_Returns_Current_Utc_Time()
    {
        var tool = new TimeTool(NullLogger<TimeTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var ctx = new ToolContext(
            SessionId: "test", MessageId: "test", CallId: "test", Agent: "code",
            Abort: CancellationToken.None, Messages: Array.Empty<AgentMessage>(),
            ReportProgress: (_, _) => Task.CompletedTask,
            Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));

        var result = await tool.ExecuteAsync(args, ctx);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("T");   // ISO-8601 has "T"
    }

    [Test]
    public async Task ValidateArguments_Accepts_Empty()
    {
        var tool = new TimeTool(NullLogger<TimeTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsTrue();
    }
}
EOF
```

#### Step 4: Build

```bash
$ dotnet build
  Harbor.Tools.Builtin succeeded.
  Harbor.App.Cli succeeded.
  Harbor.Tools.Builtin.Tests succeeded.
  0 Warning(s)  0 Error(s)
```

#### Step 5: Run tests

```bash
$ dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- \
    --treenode-filter "/*/*/TimeToolTests/*"

Passed: 2
Failed: 0
Skipped: 0
Duration: ~1.2s
```

#### Step 6: Manual smoke test

```bash
$ export KILO_API_KEY=klo_...
$ export HARBOR_MODEL=kilocode/tencent/hy3:free
$ dotnet run --project apps/Harbor.App.Cli -- ask "What time is it?"
[tool_execution_start] id=tc_1 tool=time args={}
[tool_execution_end]   id=tc_1 ok=true
The current UTC time is 2026-07-16T14:23:45.1234567Z.
```

#### Step 7: PR checklist

```bash
# 0 warnings, 0 errors
dotnet build -c Release

# Affected test projects pass (per-project executables — the form CI runs; `dotnet test` is not used)
dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- --minimum-expected-tests 1

# Code review checklist (see CLAUDE.md §Code review checklist)
# - [ ] CancellationToken threaded through
# - [ ] Result<T> for expected failures
# - [ ] No LINQ on hot path
# - [ ] Tests added
# - [ ] XML doc comments on public APIs
```

Commit:

```bash
git add src/Harbor.Tools.Builtin/Tools/Time/ tests/Harbor.Tools.Builtin.Tests/TimeToolTests.cs \
        src/Harbor.Hosting/Modules/ToolsCatalog.cs
git commit -m "feat: add 'time' builtin tool returning current UTC time"
```

### Workflow: debug a failing test

Пример: тест `EventBusTests.Publish_DeadSubscriber_Removed` падает с
`Assert.That(deadCount).IsEqualTo(1) → got 0`.

#### Step 1: Reproduce in isolation

```bash
$ dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- \
    --treenode-filter "/*/*/EventBusTests/Publish_DeadSubscriber_Removed" \
    --output Detailed

Starting test execution, please wait...
TUnit ... Publish_DeadSubscriber_Removed FAILED.
  Expected: 1
  But was:  0
  at EventBusTests.Publish_DeadSubscriber_Removed() in /path/tests/Harbor.Core.Tests/EventBusTests.cs:line 42

Failed: Publish_DeadSubscriber_Removed
Passed: 0  Failed: 1  Skipped: 0  Duration: 0.8s
```

#### Step 2: Read the test

```csharp
// tests/Harbor.Core.Tests/EventBusTests.cs
[Test]
public async Task Publish_DeadSubscriber_Removed()
{
    var bus = new InMemoryEventBus();
    int deadCount = 0;
    bus.Subscribe(async (e, ct) => { deadCount++; throw new InvalidOperationException("boom"); });
    bus.Subscribe(async (e, ct) => { /* healthy */ await Task.CompletedTask; });

    await bus.PublishAsync(new AgentStartEvent("s", Array.Empty<AgentMessage>()), default);

    await Assert.That(deadCount).IsEqualTo(1);   // ← got 0
}
```

#### Step 3: Add diagnostic output

```csharp
[Test]
public async Task Publish_DeadSubscriber_Removed()
{
    var bus = new InMemoryEventBus();
    int deadCount = 0;
    int healthyCount = 0;
    bus.Subscribe(async (e, ct) => { deadCount++; throw new InvalidOperationException("boom"); });
    bus.Subscribe(async (e, ct) => { healthyCount++; await Task.CompletedTask; });

    await bus.PublishAsync(new AgentStartEvent("s", Array.Empty<AgentMessage>()), default);

    TestContext.Current.TestOutputWriter.WriteLine($"dead={deadCount} healthy={healthyCount}");
    await Assert.That(deadCount).IsEqualTo(1);
}
```

Re-run:

```
dead=0 healthy=1
```

The dead subscriber was never called — looks like `PublishAsync` short-circuited
*before* the throwing handler. Read `InMemoryEventBus.PublishAsync`
(`src/Harbor.Registries/Events/InMemoryEventBus.cs`):

```csharp
public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default)
{
    var snapshot = _subscriptions;
    if (snapshot.IsEmpty) return;
    // ...
}
```

Wait — `snapshot.IsEmpty` returns true if there are 0 subscribers. We have 2
subscribers. Let me check the `Subscribe` method:

```csharp
public void Subscribe(Func<AgentEvent, CancellationToken, Task> handler)
{
    ImmutableInterlocked.Update(ref _subscriptions,
        arr => arr.Add(new Subscription(handler, typeof(AgentEvent).IsAssignableFrom)));
}
```

Ah — `typeof(AgentEvent).IsAssignableFrom` is `false` because we passed a handler
`Func<AgentEvent, ...>` but the Subscription filter checks if `typeof(AgentEvent)`
is assignable from itself, which should be true. Let me check the Subscription
record more carefully... 

Actually, looking at the test failure (`dead=0`), the throwing handler was
**not called** — but `healthy=1` means the healthy one was called. So `PublishAsync`
*did* dispatch... but to only one subscriber.

Look again at the snapshot:

```csharp
var snapshot = _subscriptions;
```

This reads the `ImmutableArray<Subscription>`. If the second `Subscribe` call
hasn't been observed yet (race), we'd see only one. But this is a unit test, no
concurrency. So `ImmutableInterlocked.Update` *should* be working.

Let me add more logging:

```csharp
TestContext.Current.TestOutputWriter.WriteLine(
    $"subscriptions count = {bus.GetType().GetField("_subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bus)}");
```

Output:

```
subscriptions count = System.Collections.Immutable.ImmutableArray<Subscription>[1]
```

Only 1 subscriber registered! That's the bug. Looking at the Subscribe code:
the `Subscription` constructor signature was changed to take a single arg
(removing the second bool). The test still uses the old call site.

#### Step 4: Add the assertion / fix

Fix the test:

```csharp
bus.Subscribe(async (e, ct) => { deadCount++; throw new InvalidOperationException("boom"); });
bus.Subscribe(async (e, ct) => { /* healthy */ await Task.CompletedTask; });
// ^ both calls now use the same single-arg Subscribe, both register.
```

Re-run:

```bash
$ dotnet run --project tests/Harbor.Core.Tests -c Release --no-build -- \
    --treenode-filter "/*/*/EventBusTests/Publish_DeadSubscriber_Removed"
Passed: 1  Failed: 0
```

#### Step 5: Regression test

Add an assertion that BOTH subscribers are called (the regression):

```csharp
await Assert.That(healthyCount).IsEqualTo(1);   // both should be called
```

### Workflow: profile a hot path

Пример: `ToolRegistry.ResolveTools` замедлился после изменения. Покажем как
сделать BenchmarkDotNet benchmark и прочитать результаты.

#### Step 1: Write the benchmark

```csharp
// tests/Harbor.Benchmarks/ToolRegistryBenchmark.cs
using BenchmarkDotNet.Attributes;

namespace Harbor.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, invocationCount: 10_000)]
public class ToolRegistryBenchmark
{
    private ToolRegistry _registry = null!;
    private AgentName _agent = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new ToolRegistry();
        var tb = new ToolRegistryBuilder(_registry);
        tb.AddTool<ReadTool>();
        tb.AddTool<WriteTool>();
        tb.AddTool<BashTool>();
        tb.AddTool<GlobTool>();
        _registry.Freeze();
        _agent = AgentName.Create("code");
    }

    [Benchmark]
    public IReadOnlyList<ToolDescriptor> ResolveTools_NoPermission()
        => _registry.ResolveTools(_agent.Value, null);

    [Benchmark]
    public IReadOnlyList<ToolDescriptor> ResolveTools_WithPermission()
        => _registry.ResolveTools(_agent.Value, PermissionRuleset.Empty);

    [Benchmark]
    public Result<ITool> GetTool_Frozen()
        => _registry.GetTool(ToolName.Create("read"));
}
```

#### Step 2: Run

```bash
$ cd tests/Harbor.Benchmarks
$ dotnet run -c Release

// BenchmarkDotNet=v0.14.0
// Job: Throughput invocationCount=10000

| Method                       | Mean      | Error    | StdDev   | Allocated |
|----------------------------- |----------:|---------:|---------:|----------:|
| ResolveTools_NoPermission    |  0.42 µs  | 0.005 µs | 0.004 µs |     144 B |
| ResolveTools_WithPermission  |  0.48 µs  | 0.006 µs | 0.005 µs |     144 B |
| GetTool_Frozen               |  0.18 µs  | 0.002 µs | 0.002 µs |      24 B |
```

#### Step 3: Read the results

- `Mean` — среднее время на 1 invocation.
- `Error` — half of 99.9% confidence interval.
- `StdDev` — standard deviation across iterations.
- `Allocated` — bytes allocated per invocation (GC pressure).

`GetTool_Frozen` = 0.18 µs, allocated 24 B — это `Result<ITool>` struct boxing
into `object?[]` for the `Result.Failure<ITool>` path? Let's check...

Actually 24 B is the `ToolDescriptor` allocation in `ResolveTools`. For `GetTool`
frozen, it should be 0 bytes allocated. The 24 B is the `Result<ITool>` returning
a reference to existing tool — that's `ITool` reference + `Result`'s error string
storage. Hmm, actually `Result.Success(tool)` should not allocate.

Profile with `dotnet-counters`:

```bash
$ dotnet tool install -g dotnet-counters
$ dotnet-counters monitor -n Harbor.Benchmarks \
    --counters System.Runtime[System.Runtime]
```

Watch `gen-0-heap-size` and `alloc-rate` while running. If `alloc-rate` is high,
we're allocating more than expected.

#### Step 4: Compare to baseline

If a previous run gave `0.18 µs` and now it's `0.55 µs`, that's a 3× regression.
Bisect with git:

```bash
git bisect start
git bisect bad HEAD
git bisect good v0.2.0
# run benchmark after each bisect step
```

#### Step 5: Document the result

Add to `docs/BENCHMARKS.md`:

```markdown
| Operation | Mean | Allocated | Notes |
|---|---|---|---|
| ToolRegistry.GetTool (frozen) | 0.18 µs | 24 B | baseline |
| ToolRegistry.ResolveTools (4 tools, no perm) | 0.42 µs | 144 B | includes ToolDescriptor[] |
```

### Workflow: contribute a plugin

> See [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) for the full guide.

Краткий workflow:

#### Option A: Roslyn .cs plugin (recommended, v0.3+)

```bash
# 1. Write a .cs file with both plugin class + tool class
cat > ~/.harbor/plugins/hello.cs << 'EOF'
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;

public sealed class HelloPlugin : IToolPlugin
{
    public string Name => "hello";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 3, 0);
    public string Description => "Says hello";
    public void Initialize(PluginContext context) { }
    public void RegisterTools(IToolRegistryBuilder b) => b.AddTool<HelloTool>();
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class HelloTool : ITool
{
    public ToolName Name => ToolName.Create("hello");
    public string DisplayName => "Hello";
    public string Description => "Returns a hello message";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "hello: Say hello";
    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
    public JsonDocument ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
    public Result ValidateArguments(JsonElement args) => Result.Success();
    public Task<ToolResult> ExecuteAsync(JsonElement a, ToolContext c, CancellationToken ct = default)
    {
        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "world" : "world";
        return Task.FromResult(ToolResult.Success($"Hello, {name}!"));
    }
}
EOF

# 2. Restart Harbor — the plugin runtime compiles it on startup (see log line
#    «Loaded N CS plugin(s)» in ~/.harbor/logs/harbor-cli-*.log)
dotnet run --project apps/Harbor.App.Cli
```

#### Option B: DLL plugin (samples/plugins/)

См. [PLUGIN_DEVELOPMENT.md §Migration from DLL](./PLUGIN_DEVELOPMENT.md#migration-from-samplespluginscsproj-dll-to-pluginscs-roslyn).
Note: `~/.harbor/plugins/` only scans `*.cs` — compiled DLLs must be registered
by host code or served out-of-process via `src/Harbor.Plugins.Host`.

#### Option C: MCP server

Если ваш инструмент уже MCP-сервер, Harbor оборачивает его через builtin `mcp`.
Опишите сервер в `~/.harbor/mcp.json` (или `<project>/.harbor/mcp.json`):

```jsonc
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "${harborHome}"]
    }
  }
}
```

Детали: [TOOLS_CATALOG.md §9](./TOOLS_CATALOG.md#9-mcp-integration--adding-an-mcp-server).

### Workflow: profile a memory leak

If RSS grows over time on a long-running session:

```bash
# 1. Run Harbor with the problematic session
HARBOR_TUI=plain dotnet run --project apps/Harbor.App.Cli -- ask "Long task..."

# 2. In another terminal, find the Harbor process
ps aux | grep Harbor.App.Cli

# 3. Take a heap dump
dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -n Harbor.App.Cli -o harbor.dump

# 4. Analyze with PerfView or dotnet-heapstat
dotnet-heapstat harbor.dump | sort -k 2 -n -r | head -20
# Top types by retained bytes
```

Look for growing counts of: `AssistantMessage`, `ToolCallPart`,
`ImmutableArray<AgentMessage>`, `Subscription`.

Common leak causes in Harbor:
- Plugin subscribing to `IEventBus` on each tool call (forgetting to unsubscribe).
- `ConcurrentDictionary<sessionId, T>` growing without cleanup (e.g. TodoWritePlugin).
- Streaming `StringBuilder` not returned to pool (forgot `using var`).

## Code style

Enforced by `.editorconfig` and analyzers (`Roslynator`, `SonarAnalyzer`).

- 4-space indentation.
- File-scoped namespaces.
- `var` only when type is obvious.
- `async`/`await` everywhere, no `.Result`/`.Wait()`.
- `ConfigureAwait(false)` in library code.
- XML doc comments on public APIs.
- Treat warnings as errors.

See [CLAUDE.md](../CLAUDE.md) for full conventions.

## Debugging

### In VS Code

```bash
# Launch config (.vscode/launch.json)
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Harbor CLI",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/apps/Harbor.App.Cli/bin/Debug/net10.0/Harbor.App.Cli.dll",
      "args": ["ask", "Hello"],
      "cwd": "${workspaceFolder}",
      "console": "internalConsole",
      "stopAtEntry": false,
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-..."
      }
    }
  ]
}
```

### Print debugging

For console output in tests:
```csharp
TestContext.Current.TestOutputWriter.WriteLine($"Debug: {value}");
```

For console output in CLI (when TUI is not active):
```csharp
Console.Error.WriteLine($"Debug: {value}");
```

## Release process

(Planned — not yet implemented)

1. Update version in `Directory.Build.props`.
2. Update `CHANGELOG.md`.
3. Tag: `git tag v0.X.0 && git push --tags`.
4. CI builds NativeAOT binaries for all RIDs.
5. Publish to NuGet as `dotnet tool install harbor`.

## Performance profiling

```bash
# Build Release
dotnet build -c Release

# Run with dotnet-counters
dotnet tool install -g dotnet-counters
dotnet-counters monitor -n harbor --counters System.Runtime

# Heap dump
dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -n harbor
```

## Principles checklist

Перед PR прогоните код через этот чек-лист. Полный разбор каждого пункта — в [docs/CODE_PRINCIPLES_AUDIT.md](./CODE_PRINCIPLES_AUDIT.md).

### OOP / SOLID

- [ ] **SRP**: класс делает одну вещь. Если класс > 300 строк или > 8 public методов — подозрение.
- [ ] **OCP**: новые типы добавляются через `interface` + новую реализацию, не через `switch` на существующем типе.
- [ ] **LSP**: подкласс / interface-impl ведёт себя идентично базовому контракту (включая thread-safety).
- [ ] **ISP**: интерфейс не заставляет реализовывать неиспользуемые методы. Если в impl 5+ `throw new NotSupportedException()` — разделить интерфейс.
- [ ] **DIP**: зависимости только через `Harbor.Abstractions` интерфейсы, не через конкретные классы.
- [ ] **Sealed** по умолчанию. Открытый для наследования класс — явный design decision.
- [ ] **Encapsulation**: после конструирования объект в валидном состоянии. Никаких `sessionId = "" // populated by caller`.
- [ ] **No runtime service location** (#63): бизнес-логика берёт зависимости через ctor. `GetRequiredService` / `GetService` вне ctor допустимы только с коротким комментарием-обоснованием:
  - factory-лямбды DI-модулей и композишн-рут (`Program.cs`, `*Module.cs`, `Use*` extensions) — idiomatic MS DI;
  - optional-зависимости с graceful fallback (`GetService` + null-handling, никогда `GetRequiredService`);
  - framework-created инстансы, которым нельзя дать DI: панели получают типизированный пакет `PanelServices` (ctor-параметр `PanelContext`), который композишн-рут собирает **один раз** через `PanelServices.FromContainer` (#470 — в per-frame контракте больше нет `IServiceProvider`); XAML-`ViewModelLocator`, контракт `ITuiRenderer.RunInteractiveAsync(agent, host)`.

### GoF

- [ ] Паттерн выбран осознанно, не "потому что модно". Registry ≠ Builder ≠ Factory.
- [ ] Strategy без mutating state. Если strategy держит state — это либо параметризованный singleton, либо неправильно выбранный паттерн.
- [ ] Observer (`IEventBus`) — subscribers не бросают исключения (event bus их не ловит, кроме `InMemoryEventBus.RemoveDeadSubscriptions`).

### FP

- [ ] **Pure functions**: `ChatAppReducer.Reduce` — эталон. Если функция mutates — это должно быть явно (mutating cache, pool, state).
- [ ] **Immutability**: доменные модели — `record` / `record struct`. `with` для модификации.
- [ ] **No side-effects in render path**: `ChatScreen.Render` не должен мутировать `UiState` — только читать.
- [ ] **No fire-and-forget**: `_ = SomeAsync()` — запрещён, кроме случаев с `.ContinueWith(OnlyOnFaulted)`.

### ROP

- [ ] Все public APIs, которые могут ошибиться — возвращают `Result<T>` / `Result`.
- [ ] **Никогда** не бросать исключение на expected failure (file not found, invalid arg, provider not registered).
- [ ] **Никогда** не вызывать `.Value` на `Result` без проверки `IsSuccess` (это краш).
- [ ] `.Bind()` / `.Map()` / `.Ensure()` — предпочтительнее `if (result.IsFailure) return ...`.
- [ ] Ошибки не теряются: `null` вместо `Result` — запрещён в новых API.

### Performance

- [ ] Hot path (`AgentLoop.RunAsync`, `StreamAsync`, `Render`, `Dispatch`) — без `LINQ`, без `new StringBuilder()`, без `string.Split`.
- [ ] `ArrayPool<T>.Shared.Rent` + `using var rented = ...` для временных буферов.
- [ ] `StringBuilderPool.Rent` для конкатенации.
- [ ] `FrozenDictionary` для read-only коллекций, построенных один раз.
- [ ] `NonBlocking.ConcurrentDictionary` для write-heavy concurrent state.
- [ ] `for (int i = 0; i < arr.Length; i++)` вместо `foreach` на hot path (избегает enumerator allocation).
- [ ] `ConfigureAwait(false)` в library code.
- [ ] `ILogger.IsEnabled(LogLevel.Trace)` guard перед `LogTrace` с expensive args.
- [ ] `CancellationToken` пробрасывается во все async методы.

### Low-level / байтоебля

- [ ] `Utf8JsonReader` вместо `JsonDocument.Parse` на hot path (SSE-чанки, JSONL-строки).
- [ ] `JsonSerializerContext` (source-gen) вместо reflection-based `JsonSerializer.Serialize<T>`.
- [ ] `Span<T>` / `ReadOnlySpan<T>` + `IndexOf` вместо `string.Split`.
- [ ] `string.Create(Length, state, (span, s) => ...)` вместо `string.Format` / interpolation на hot path.
- [ ] `Interlocked.CompareExchange` для lock-free CAS (если реально узкое горлышко).
- [ ] Pooled buffer clear перед return (`Array.Clear(arr, 0, count)`), чтобы не держать references alive.

### Concurrency

- [ ] `ILlmClient` и `ITool` impls — thread-safe для concurrent calls.
- [ ] Mutable instance state в singleton-сервисе — либо `lock`, либо `Interlocked`, либо per-call state в local'ах.
- [ ] `Channel<T>` для producer-consumer, `ImmutableArray<T>` + `ImmutableInterlocked.Update` для atomic snapshots.
- [ ] `async`/`await`, не `ContinueWith` (кроме fire-and-forget с `OnlyOnFaulted`).

### NativeAOT

- [ ] `JsonSerializer.Serialize/Deserialize<T>` — только через `JsonSerializerContext` source-gen.
- [ ] No reflection: `Type.GetProperties()`, `Activator.CreateInstance` — запрещены в Core/Storage/Providers.
- [ ] No `Assembly.Load` / `AssemblyLoadContext` collectible (использовать out-of-process plugins).
- [ ] `dotnet build -c Release` — 0 IL2026 warnings. **Только для 4 проектов с `IsAotCompatible=true`** (`Harbor.DesignSystem`, `Harbor.Ui.Framework.Rendering`, `Harbor.Tui.CellForge`, `Harbor.Tui.CellForge.Engine`): IL2026 выдаёт trim/AOT-анализатор, а он запускается лишь там, где проект сам opt-in, либо при publish с `PublishAot=true` — а такого publish не делает ни один workflow в `.github/workflows/`. `Harbor.App.Cli` (единственный проект, который публикуется как AOT) в эту проверку не входит, так что галочка здесь не утверждает ничего о поставляемом бинарнике. Запрет динамического кода проверяется отдельно и реально enforce-тся (`RS0030` по `BannedSymbols.DynamicCode.txt`, #626/#1844); publish-гейт — #413.

## SpectreTUI development (contrib)

Если меняете `contrib/tui/Harbor.Tui.SpectreTui/` — обязательно прочитайте [docs/SPECTRE_TUI_DEEP_DIVE.md](./SPECTRE_TUI_DEEP_DIVE.md):
архитектура render-loop, layout tree, scroll conventions, и квесты из opencode/kilocode/pi-agent (diff-view, slash-completion, file-tree).
Проект живёт в contrib с sprint-2 и собирается через `contrib/Contrib.slnx`; при этом дефолтная CLI-сборка
референсит альтернативные рендереры через `HarborWithSpectreTui` (включён по умолчанию). Вторая интерактивная
оболочка — `src/Harbor.Tui.CellForge/` (opt-in: `HARBOR_TUI=cellforge`, см. README проекта).

## Troubleshooting


### Build fails with NU1603

Package version mismatch. Run `dotnet restore --force` and check `Directory.Build.props`.

### Tests fail with timeout

Check if `GetScrollback_ReturnsRecentEvents` is hanging — it's skipped by default due to blocking channel read. Other tests should be fast (<1s each).

### Provider not found

- Check `providers/<name>.json` exists.
- Check `id` field is lowercase alphanumeric.
- Run `dotnet run --project apps/Harbor.App.Cli -- providers` to see what's loaded.

### API key not found

- Check env var name matches `authEnvVar` in provider config.
- Conventional names: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, etc.
- Run `echo $ANTHROPIC_API_KEY` to verify.

## Dependency & advisory policy

GitHub Actions and NuGet versions are managed by Dependabot
(`.github/dependabot.yml`): weekly on Monday, max 10 open PRs per ecosystem.
NuGet `minor`+`patch` updates are grouped into one PR; GitHub Actions updates
arrive as one PR per action (no grouping — majors need individual review).

### Version-pinning policy

- **GitHub Actions: pin to the major version tag** (`actions/checkout@v7`,
  `actions/setup-dotnet@v6`). Never pin `latest`/branch names; never add a
  new action without a major tag. SHA-pinning is intentionally NOT used —
  Dependabot tracks tags, and every workflow diff stays human-reviewable.
- **Major bumps (vN → vN+1) always need human review**: check the action's
  release notes for breaking changes (e.g. `upload-artifact@v4` dropped
  implicit `if-no-files-found` behavior in the past). Minor/patch bumps of
  trusted `actions/*`, `github/*`, and `dorny/*` actions may be merged after
  green CI without deep review.
- **NuGet: versions pinned centrally** in `Directory.Packages.props`
  (Central Package Management; see `docs/CENTRAL_PACKAGE_MANAGEMENT.md`).
  Per-project `<PackageReference>` entries stay version-less. Dependabot bumps
  `<PackageVersion>` entries there. Transitive pinning
  (`CentralPackageTransitivePinningEnabled`) keeps deep dependencies locked.

### Review cadence

- Dependabot PRs land on Monday; triage them within the week — stale action
  PRs accumulate conflicts and hide real advisories.
- Monthly: `dotnet list package --vulnerable` locally and confirm CI's
  advisory report (below) is clean or has tracked suppressions.

### NuGet audit advisories (NU190x)

NuGet audit runs on every restore via the SDK default; advisories surface as
`NU190x` build warnings (and fail `master`/tag builds through `--warnaserror`).
CI additionally runs a non-blocking vulnerable-packages report in the `build`
job (`dotnet package list --project Harbor.slnx --vulnerable
--include-transitive --no-restore`, `continue-on-error: true`) so the
advisory list is visible without breaking the build.

The report runs only when the dependency graph can have changed — the
`changes` job's `deps` filter fires on `Directory.Packages.props` or
`Directory.Build.props` — or on a manual dispatch. A code-only PR cannot
move an advisory, so it does not pay for the scan (42 s measured, of which
36.5 s is the NuGet advisory API round trip). `--no-restore` is there
because the noun-first `dotnet package list` restores first as of .NET 10,
and the solution was already restored by the build step above it.

The step is report-only, so it also guards itself: a scan that did not
complete, or that produced no per-project result, is reported as an error
annotation instead of being read as "no advisories".
Known-accepted advisories must be suppressed per-project with a justification
comment — never globally. Current example: `MessagePack` NU1902/NU1903
(typeless-deserialization path; IPC uses it only on locally-trusted
same-machine transport) suppressed in `Harbor.Ipc.*` with a comment in
`Directory.Packages.props`.

### Analyzer licences

A build-time analyzer is a dependency like any other, and the licence question
is asked the same way: read the licence that the pinned version ships, then
decide whether the use is inside it.

**An analyzer does not reach the product.** Every analyzer package in this
repository is referenced with `PrivateAssets=all` and
`ExcludeAssets="runtime"` (`Directory.Build.props`), so nothing it contributes
is copied into a Harbor binary or into a consumer's dependency graph: the
analyzer assemblies are consumed by the compiler, and the one analyzer package
that also carries a runtime assembly (`Excubo.Analyzers.Annotations`) has that
asset excluded. What its licence has to permit is the build, not what Harbor
ships.

**Read the licence in the nupkg, not the registry field.** The version to read
is the pinned one. `SonarAnalyzer.CSharp` declares its licence as a FILE
rather than as an SPDX expression: the nuspec says
`<license type="file">licenses\LICENSE.txt</license>`, and that file is the
licence. Some packages in the tree declare no licence in the nuspec at all, so
the registry field alone does not always answer the question. LGPL is not a
disqualifier for a build-time analyzer either: `SecurityCodeScan` was rejected
on activity and duplication, not on the licence (`docs/ANALYZERS.md`).

**The one source-available package, and its AI clause.**
`SonarAnalyzer.CSharp` 10.29.0.143774 ships the "SONAR Source-Available
License v1.0" (last updated November 13, 2024). It grants a copyright and a
patent licence "for any Non-competitive Purpose", and it defines that term as
any purpose except three:

- (a) providing a product or service with the same or substantially similar
  functionality as SonarQube;
- (b) competing with SonarQube;
- (c) employing, using or engaging artificial intelligence technology that is
  not part of the Program to ingest, interpret, analyze, train on or interact
  with the data the Program provides, or to engage with the Program in any
  manner.

How those land on Harbor:

- (a) and (b) do not bite. Harbor is an AI coding harness, not a code quality
  platform, and it offers nothing that substitutes for SonarQube.
- (c) does not reach the product. The "Program" is the analyzer's own
  contribution and the data it provides are its diagnostics; the Harbor
  runtime consumes neither. The analyzer runs inside the compiler at build
  time, the package never reaches a binary, and no Harbor code reads a
  diagnostic.
- (c) does reach the dev workflow, on the widest reading. The diagnostics are
  in this repository's build log, and an AI agent working on the repository
  reads the build log. On a literal reading, that is AI interacting with data
  the Program provided.

**The decision, and what it is not.** The package stays (`docs/ANALYZERS.md`,
the `SonarAnalyzer.CSharp` row). The reason this is not a licence clearance
is stated rather than hidden: reading (c) as a ban on AI-assisted development
that uses a Roslyn analyzer would make the licence unusable for the audience
SonarSource ships it to, so the plausible reading is the narrow one, about AI
over Sonar's own analysis data and engine. That is a reading of a licence, not
a legal opinion, and the owner may take a different one. If the owner does,
the way out is measured: the package contributes 0 findings to `src/` and
`apps/` today (476 sites, every one under `tests/`), so removing it costs the
product tree 447 preventive rules and the test tree the 116 sites that are
still worth reading, and it costs the shipped binary nothing.

**When you add an analyzer, the licence is part of the check.** Read the
`license` element of the nuspec inside the nupkg, and read the file it points
at when it is a file. LGPL-3.0 and source-available licences are not
disqualifiers for a build-time analyzer. A release that has been abandoned for
years, and a rule family this tree already has, are.

### Auto-merge (explicitly deferred)

Dependabot auto-merge is NOT enabled. Patch/minor auto-merge for trusted
actions stays an open option (issue #25) once merge-queue + required-checks
stabilize; until then every Dependabot PR merges manually after green CI.

## Disciplinary rules

Spec 17 [agentic product direction](./specs/17-agentic-product-direction.md)
§6 rules the machinery must honour: the agent does not author its own
acceptance criteria, does not weaken the checks, and does not accept its own
work. The five rules:

- Architecture matrix: changing an architecture rule is a product decision,
  not plumbing — see `### Architecture matrix changes` under Contributing
  below (edge, why, alternative, permanent vs exception with a removal
  condition). Matrix + code ship in the same commit.
- Retry/flaky: a retry or quarantine is allowed only registered (tracking
  issue + hypothesis + visible first-pass + watched counts + not permanent)
  — see `### Flaky-test / Retry policy` under Contributing below.
  A concurrency-test flake is a suspected product race first: disprove the
  race before blaming the runner.
- Goldens: a blessed snapshot needs the expected visual change explained
  with before/after; snapshots alone never gate a state-management
  migration (add semantic tests: dedup, clean session switch, subscription
  disposal, reconnect contract, approval/cancellation race).
- Live-model E2E is a separate signal, not a gate (provider, limits,
  determinism). A live failure is tracked, never deleted from reporting,
  and never blocks deterministic tests.
- Owner report: result → evidence (commands, CI, before/after, regression
  test) → limits → rule changes as a separate block → recommendation +
  rollback. The agent proposes neither the criteria nor the acceptance.

## Contributing

1. Fork the repo.
2. Create a branch: `git checkout -b feature/my-feature`.
3. Make changes following [CLAUDE.md](../CLAUDE.md) conventions.
4. `dotnet build` and per-project test runs (`dotnet run --project tests/<Project> -c Release --no-build`) — must pass with 0 errors.
5. Commit with conventional commits: `feat: add X`, `fix: Y`, `docs: Z`.
6. Open a PR.

### Architecture matrix changes

Changing `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs` (or any
arch rule) is a product decision, not plumbing — state in the PR:

```text
Edge added/removed:
Why necessary:
Why the old boundary cannot hold:
Alternative considered:
Sensitive-capability access expanded: yes/no
Permanent rule or migration exception (removal condition + tracking issue):
```

Matrix + code ship in the same commit. A green CI on changed rules proves
consistency with the new rules, not that the change was wise. Temporary
exceptions require a linked issue and a verifiable removal condition.

### Flaky-test / Retry policy

`[Retry(n)]` or quarantining a test is allowed only when ALL hold:

- the flake is registered (tracking issue linked in a comment);
- there is a cause or working hypothesis written down;
- first-pass results stay visible (no silent green);
- first-pass success / retry counts are watched;
- the state is not permanent (fix or proper quarantine follows).

Concurrency-test flake = suspected product race first: disprove the race
before blaming the runner. Same for goldens: a blessed snapshot needs the
expected visual change explained with before/after; snapshots alone never
gate a state-management migration (add semantic tests: dedup, clean session
switch, subscription disposal, reconnect contract, approval/cancellation race).

Unbounded retries, retry-until-green, and permanent retries without review
are forbidden. Live-service errors are tracked separately from deterministic
tests but never deleted from reporting. Retry on side-effecting tests only
with guaranteed state reset or proven attempt independence.

### Test-infrastructure changes

Changes to CI, permission policy, evaluation criteria, or critical tests
must be separately visible and approved — never buried inside a feature
diff. «Updated snapshots» must not silently mean «fixed the bug».

## License

MIT — see [LICENSE](../LICENSE).
