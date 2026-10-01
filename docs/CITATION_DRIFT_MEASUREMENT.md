# Citation drift in test prose: what `file:line` in a test actually costs

> Status (2026-10-01): measurement snapshot, on `dev` at `bb33437a`. A dated
> record, not a normative claim about the current tree. Every number below was
> produced by running `tools/check-doc-cites.py`; the rule is `TEST-CITE-DRIFT`
> and the population is `tests/**/*.cs`. The counts move when the tree moves,
> and one of the findings here is that they do.

Issue #947 asks one question that is an owner decision rather than a mechanical
one: **should a `file:line` in a test's prose be replaced by a symbol name?**
This document prices that decision. It does not make it — and it ships the guard
that keeps the decision honest in the meantime.

## The defect

A `file:line` written in a test's prose names a NUMBER. The number resolves,
the file exists, the line is not blank — and it is the wrong line. Nothing
red. The defect is that the citation is **falsifiable and unfalsified**: it
makes a checkable claim about a specific line, and no rule in this repository
was reading it.

`Status: normative` is the reason. It is a self-selector for **markdown**, and
a `.cs` file cannot carry a markdown banner, so every rule in
`check-doc-cites.py` was structurally outside this population. 236 citations,
zero coverage.

## What was measured, and what it changed

The cheap guard was written and measured before it was adopted, because the
issue had already established — from one hand-read case — that it could not
catch the class that matters.

Measured on `a7a40335`. **Every table on this page carries the commit it was
measured on**, and the populations differ between them: the tree moved under a
rebase between `a7a40335` and `bb33437a` with no test edited (see *The
measurement moved under a rebase* below), so a figure quoted without its commit
would already be stale by the time this document is read — which is the defect
this whole page is about, and the reason `DOC-COUNT-STALE` exists.

| | count |
|---|---:|
| prose citations in `tests/**/*.cs` | 236 |
| mechanically decidable (unresolved / past EOF / blank) | 22 |
| would stay silent | 214 |
| **provably wrong today** | **66** |
| mechanical rule **catches** | **4** |
| mechanical rule **misses** | **62** |

**Four of sixty-six.** A "points at a line with code" fence is green on
`AgentLoop.cs:91`, where the line has code and is `_providers = providers;`.
So the rule shipped is anchored on history instead: `git blame` the citing
line, read the target at the commit that wrote the citation and at HEAD, and
compare. That is what makes "provably wrong" a measurement rather than an
opinion.

The `4 of 66` here and the `87 / 66` of the red run below are **different
questions on the same population**: the 66 is the count of provably-wrong
citations, the 87 is the count of the guard's findings (66 drift + 21
mechanical, some overlapping). Both are on `a7a40335` / `bb33437a`
respectively and neither is a subset of the other.

## Both cases the issue named, reproduced by the rule

```
tests/Harbor.Architecture.Tests/TokenTrackingRatchet.cs
  L83: [TEST-CITE-DRIFT] AgentLoop.cs:91 — 99b73bfe wrote it over
       `_tokenTracker = tokenTracker;`; that line now reads `_providers = providers;`

tests/Harbor.Architecture.Tests/UiConfigDefaultsRule.cs
  L19: [TEST-CITE-DRIFT] CommonConfig.cs:127 — ffc40d61 wrote it over
       `public string DefaultProvider { get; init; } = "anthropic…`
       that line now reads `/// Default provider ID used on first launch / …`
  L92: [TEST-CITE-BLANK] apps/Harbor.App.Avalonia/ViewModels/ThemeSettingsViewModel.cs:74
       — the line is blank
```

The second is the **self-reproducing** class the issue called most expensive,
and the rule reports it from both directions at once: the prose of the repair
now sits where the code used to be. The fix ate its own citation, and nothing
in the tree said so.

## The anchor must be the citing LINE, not the citing FILE

This is the one methodological finding worth carrying forward.

Anchoring on "the commit that last touched the citing file" reports **32**.
Anchoring on "the commit that wrote this line" reports **66**. The difference is
`TokenTrackingRatchet.cs` — the issue's own headline case. `e46e0048` re-touched
that file a day *after* `99b73bfe` wrote the citation, so a file-anchored
baseline takes the already-stale state as its reference and reports the citation
as correct, forever.

A measurement that anchors on the wrong baseline is not conservative. It is
wrong in the direction that hides the bug.

## Where the count is distorted, and in which direction

The standing expectation from #870 and the night-shift audits is that an issue's
count **understates** reality. Here it does not — and the reason is worth
recording, because the two distortions have opposite causes.

```
issue says                        245 citations in 68 files
the rule measures                236 citations in 66 files
raw regex matches, any context   270 in 71 files
  of which in comment prose      247 in 66 files
  of which in STRING LITERALS    21   <- test data, not claims
  of which in code                2
```

**The count is overstated, and by construction.** 245 is a count of *matches*;
the population of *claims* is smaller, because some matches are a test's own
fixture data. `PanelExtractorsTests.cs:358` builds the string
`"src/a.cs:12 CS0246: type not found"` to feed a parser — a synthetic compiler
diagnostic, quoted as data. Counting it as a citation would have the rule
reporting a test's input back at the test.

So both distortions are live and they differ by mechanism:

- **understatement** (the usual case) comes from the author not looking —
  matches get missed, and the issue's prose then reads low;
- **overstatement** here comes from the tool matching too much — string
  literals and code are not claims, and a naive regex counts them anyway.

The rule strips string literals before matching (`comments_only`) and asserts
that exclusion in two self-tests, one of which checks the count is **zero**
rather than merely unreported — because a rule that excluded everything would
pass the same test.

There is a second, smaller overstatement in the same direction: 9 of the 87
findings are `TEST-CITE-MISSING` / `TEST-CITE-AMBIGUOUS` on **project-relative
paths** (`Commands/ModelCommand.cs:81-86`, `ViewModels/TuiViewModels.cs:117`).
Those files exist. The resolver's `PROJECT_ROOTS` tries `src/ apps/ tests/ tools/`
and cannot produce `apps/Harbor.App.Cli/Commands/…`. This is #944's blind spot
reproduced in a new subject — the rule both works and lies — and it is a known
false positive of the guard, counted here rather than waived.

## The measurement moved under a rebase, with no test edited

```
on a7a40335    85 findings over 236 citations
on bb33437a    87 findings over 235 citations
```

`#966` landed between them and edited files that tests cite. Nothing in
`tests/` changed. A rule whose count only moves when a test changes would not
have caught the issue; this one counts, and the count moved because the tree
did.

## What it costs

This is a standing tax, not a one-time bill. A line inserted above a cited one
changes what the number means, so the gate must be re-run at every substantive
edit near a citation — and it will be red again, by design, each time. That is
the honest price of catching the class the mechanical fence cannot see, and it
is the reason the mechanical fence's `4 of 66` was measured rather than
assumed sufficient.

Both numbers are stated because either alone is the wrong decision: `4/66` makes
the cheap guard look useless, `236 citations` makes the expensive one look
unavoidable. The repair profile is what separates them — see below.

## Repair profile of the 66, and the three options

Where did the content each author meant actually go? (on `a7a40335`)

| | count |
|---|---:|
| intended line found at **exactly one** place at HEAD | 36 |
| found at **several** places (needs a human to pick) | 7 |
| **not present at HEAD at all** | 21 |

And would a **symbol name** instead of a number have survived? (on `a7a40335`)

| | count |
|---|---:|
| an identifier named on the cited line still exists in the file | 58 (90%) |
| nothing named on that line survives — a human is needed regardless | 6 (9%) |

Both tables partition the same 66, and they answer different questions. The
second decides **option A** — *would a symbol name have held*. The first
decides **the repair** — *where did the intended line actually go*, and only 36
of 66 can be placed mechanically. A citation can be in the "a symbol would hold"
half and still need a human to place, and most of them do: 58 would survive a
symbol rename, but only 36 can be renumbered without judgement.

**That 90% is the whole decision.** A `Type.member` citation survives an insert
above it by construction, and would have held for 58 of the 66. It is the
correct repair. It is also a rewrite of every citation in the population, and
**that is the owner's call, not this issue's** — hence `Refs`, not `Fixes`.

Three options, with what each costs:

| option | catches | cost |
|---|---|---|
| **A. symbol name instead of line number** | 58 of 66 by construction; the other 6 need a human either way | rewrite ~236 citations across 66 files; no standing tax afterwards |
| **B. the history-anchored gate (shipped here)** | 66 of 66 | a standing re-run tax at every edit near a citation; 87 findings to answer once |
| **C. mechanical fence only** (resolves / not EOF / not blank) | **4 of 66** | near-zero, and green on the exact bug this issue is about |
| **D. forbid `NNN` in test prose** | n/a | removes 62 undetected failures **and** the 4 diagnostics; a red run no longer points anywhere |

A and B are not exclusive. B is the thing that keeps A honest: a symbol
citation can rot too, and B is what notices.

## The rule went blind in CI on its first run, and said nothing

This is the most important result on the page, and it came from CI rather than
from the worktree.

The guard passed locally on every run: **87 findings**. Its first CI run
reported **21** — and no error, no warning, exit 1 for a smaller reason. The
gap was not a flakiness and not a scope difference:

```
worktree (full history)    87 findings   66 TEST-CITE-DRIFT
CI (actions/checkout)      21 findings    0 TEST-CITE-DRIFT
```

`actions/checkout` defaults to `fetch-depth: 1`. In a depth-1 clone `git blame`
attributes every line to the shallow boundary commit, so "what did this citation
mean when it was written" becomes HEAD compared with HEAD and is true by
construction. The mechanical classes — unresolved, past EOF, blank — never touch
history and kept working, which is exactly what made it dangerous: **the gate
stayed green on the one rule that catches the class, and the smaller number
looked like a legitimate result.**

`4 of 66` was the measurement that justified building the rule. Had the anchored
comparison been built on `git log` and measured only in a worktree, the shipped
artefact would have been a rule that catches 4 of 66 in practice, wearing a
docstring that says 66.

Two fixes, because either alone is insufficient:

- `docs.yml` sets `fetch-depth: 0` on the cites job — necessary, because
  without history there is nothing to compare against;
- `repo_is_shallow()` makes the gate **refuse** on a truncated clone
  (`TEST-CITE-NO-HISTORY`), stating that the anchored findings are *absent,
  not zero*.

The second is the one that matters. A missing checkout option is a bug someone
fixes; a rule that reports a smaller number with the same confidence as a larger
one is a defect that survives review, because the output is always plausible.
`#905`'s lesson — the rule both works and lies — applied one level up to the
rule itself, and `fetch-depth` was the trigger.

Both are self-tested: `--self-test` builds a `file://` shallow clone of its own
fixture and asserts the gate goes red with `TEST-CITE-NO-HISTORY` and says
"absent, not zero". The `file://` is load-bearing — git ignores `--depth` on a
plain local-path clone, so the first version of the case asserted nothing and
passed vacuously.

## Appendix: the red run, verbatim

Captured with the guard in place and **nothing repaired** — exit code 1,
`=== VIOLATIONS (87 in 33 documents) ===`. The same command on the parent
commit prints `OK` and exits 0, and **zero** of the 87 carry a `DOC-*` code: all
of them are the population no rule in this repository had ever read.

```
         66  TEST-CITE-DRIFT
          9  TEST-CITE-MISSING
          7  TEST-CITE-BLANK
          5  TEST-CITE-AMBIGUOUS
          0  past EOF
```

Five files, chosen because between them they cover all four codes and both of
the shapes the issue named by hand.

### `TokenTrackingRatchet.cs` — the issue's first named case, verbatim

```
L83: [TEST-CITE-DRIFT] AgentLoop.cs:91 — 99b73bfe wrote it over
     `_tokenTracker = tokenTracker;`; that line now reads `_providers = providers;`
```

The rule did not need to be told this was wrong, and it did not need a human to
confirm it. `git blame` names the commit that wrote the sentence, that commit's
`AgentLoop.cs` had `_tokenTracker` on line 91, and HEAD does not.

### `UiConfigDefaultsRule.cs` — the self-reproducing case

```
L19: [TEST-CITE-DRIFT] CommonConfig.cs:127 — ffc40d61 wrote it over
     `public string DefaultProvider { get; init; } = "anthropic…`
     that line now reads `/// Default provider ID used on first launch / when the u…`
L20: [TEST-CITE-DRIFT] CommonConfig.cs:150 — ffc40d61 wrote it over
     `public string StorageBackend { get; init; } = "";`
     that line now reads `/// <para>`
L92: [TEST-CITE-BLANK] apps/Harbor.App.Avalonia/ViewModels/ThemeSettingsViewModel.cs:74
     — the line is blank
```

Three of the four drift into **doc comments**. That is the fingerprint of the
class: an XML doc block inserted above a property pushes the property down, and
the citation lands on the documentation instead of the declaration. It is also
why a "the line has code" fence is useless here — a `///` line has text on it.

`L92` is reported **twice**, once per code, and both are true: the line is blank
AND it no longer holds what its author meant. Overlapping codes are intentional
rather than deduplicated — a reader who came for one of them is told about the
other at the same time.

### `ModifierGateFamilyTests.cs` — nine findings, one commit

```
L17: [TEST-CITE-DRIFT] ComposerController.cs:173 — d4997e4b wrote it over
     `if ((mods & (KeyModifiers.Ctrl | KeyModifiers.Meta | KeyM…`
     that line now reads `if (mods.AcceptsTypedChar())`
L24: [TEST-CITE-DRIFT] ApprovalGateView.cs:254 — d4997e4b wrote it over
     `if (key.Modifiers != KeyModifiers.None)`
     that line now reads `if (!key.Modifiers.IsUnmodified())`
L25: [TEST-CITE-DRIFT] TreeView.cs:269 — d4997e4b wrote it over
     `|| key.Modifiers != KeyModifiers.None)`
     that line now reads `|| !key.Modifiers.IsUnmodified())`
```

`d4997e4b` (#824) replaced a hand-written modifier test with
`AcceptsTypedChar()` / `IsUnmodified()` extension calls across five files, and
every citation in that header block moved with them. This is the shape a
whole-file mechanical sweep would repair as nine unrelated edits; they are one
commit.

### `SessionStoreFailureTextParityRules.cs` — where the refactor's prose moved past the numbers

```
L24: [TEST-CITE-DRIFT] MemorySessionStore.cs:69 — 29dbb315 wrote it over
     `return Task.FromResult(Result.Failure($"Message '{message…`
     that line now reads `return Task.FromResult(Result.Failure(SessionStoreErrors.…`
```

`L35` is the case the issue called worse than a wrong line:
`MemorySessionStore.cs:146` now reads `}`. A citation pointing at a closing
brace cannot be spotted by eye — the reader is sent somewhere with nothing in it.

### `ProjectorThreadSafetyTests.cs` — the smallest drift, stated plainly

```
L4: [TEST-CITE-DRIFT] apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs:130
    — 9917daed wrote it over `services.AddSingleton<DefaultUiProjector>();`
    that line now reads `//`
```

The file exists. Line 130 exists. It is not blank. It is not past EOF. It holds a
comment marker, and every mechanical question anyone would think to ask about it
answers "fine". Only "did it mean this?" has an answer, and it is no.

## Perimeter

Three issues touch `tests/Harbor.Architecture.Tests`; this one is distinct from
both others and shares nothing with them.

- **#877 (closed).** `SourceScan` cannot see `tests/`, by name and
  unconditionally, and that exclusion is **deliberate** — #893 measured it,
  declared it, and did not fix it. This rule does not widen `SourceScan`.
- **#950 (open).** The reflection inventory's universe is the test bin
  directory, so it contains the test assembly. That is about **which
  assemblies a rule may see**. This rule reads **no assembly**: it reads
  `git blame` output and text files, so it adds nothing to the inventory and is
  not affected by it.
- **#947 (this one).** A `file:line` in a test's **prose**, and the number it
  names.

The boundary in one line: #950 is about *what the instrument may see*; #947 is
about *what a sentence claims*. Neither adds a path to the other's scan.
