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

Where did the content each author meant actually go?

| | count |
|---|---:|
| intended line found at **exactly one** place at HEAD | 36 |
| found at **several** places (needs a human to pick) | 7 |
| **not present at HEAD at all** | 21 |

And would a **symbol name** instead of a number have survived?

| | count |
|---|---:|
| an identifier named on the cited line still exists in the file | 58 (90%) |
| nothing named on that line survives — a human is needed regardless | 6 (9%) |

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