# Demo GIFs — generation and intentional updates

The README leads with four animated GIFs. They are not hand-made and they are not
committed by hand: they are recorded by the `demo-gifs` workflow
([`.github/workflows/demo.yml`](../.github/workflows/demo.yml)) from four VHS
tapes, normalised onto a fixed frame cadence, checked against a numeric baseline
manifest, and pushed straight back onto `dev`/`master`.

This document is the maintainer guide for **updating them on purpose**. It exists
because "a regenerated GIF appeared in a commit" is ambiguous: it can mean a
renderer change landed, or it can mean somebody re-recorded bytes with nothing
behind them. Those two situations want opposite responses, and the second one has
already cost this repository a rollback.

> **Scope.** This is about the demo GIFs in `assets/demo/`. The *golden baselines*
> (`*.golden.txt`, `*.verified.*`) are a different pipeline with a different
> regeneration route — see
> [`.github/workflows/goldens.yml`](../.github/workflows/goldens.yml) and
> [DEVELOPMENT.md](DEVELOPMENT.md). The two share a lesson and nothing else, so
> they are not merged in this document.

## The four GIFs and where they land

Each tape drives `harbor --demo`, which boots an **in-process mock LLM** and a
throw-away `HOME`. No API key is involved and none is needed;
[`demo/run-scene.sh`](../demo/run-scene.sh) unsets every `*_API_KEY` before the
CLI starts, so a tape that accidentally reached a live provider would fail rather
than record a model-dependent GIF.

| Tape | GIF | Scene / renderer | README slot |
|---|---|---|---|
| [`demo/hero.tape`](../demo/hero.tape) | `assets/demo/hero-compressed.gif` | `hero` on CellForge | the full-width lead image at the top of the README |
| [`demo/markdown.tape`](../demo/markdown.tape) | `assets/demo/markdown-compressed.gif` | `markdown` on CellForge | left cell of the two-up row |
| [`demo/approval.tape`](../demo/approval.tape) | `assets/demo/approval-compressed.gif` | `approval` on CellForge | right cell of the same two-up row |
| [`demo/plain.tape`](../demo/plain.tape) | `assets/demo/plain-compressed.gif` | `hero` on the plain renderer | left cell of the row below |

Three of the four drive the **CellForge** backend, which is a cell-diff renderer:
it owns the alternate screen and paints whole frames through a single backend
write, so no output pipe may sit between it and the terminal. `run-scene.sh`
therefore skips the scrub-and-pace stage for those three and gets the same
guarantees one level down — the demo prints no nondeterministic tokens at all, and
its playback holds each painted frame for `DEMO_CELLFORGE_STEP_MS`
([`demo/toolchain.lock`](../demo/toolchain.lock)). Only `plain.tape` goes through
the pipe.

**Frames are scratch, never deliverables.** `TuiDemoRecorder` writes
`assets/demo/frames/<scene>/`, and that directory is gitignored
([`.gitignore`](../.gitignore)). Do not commit it, do not regenerate it as a
deliverable, and do not treat its absence from a commit as something lost.

## What an "intentional update" actually is

This is the question the document exists to answer, so it is worth stating in a
form that cannot be misread. There are two cases, and they are **not** variants
of one situation:

- **Case A — the pixels were supposed to move.** You changed something that
  decides what the recorder draws: a renderer under `src/Harbor.Tui.**`, the
  terminal abstractions, the `harbor demo` command, a tape, the scrubber, the
  toolchain pins, or the frame pacer. The new GIF is **required**, and a red
  drift gate is the **correct signal** — it is the gate telling you the artifact
  it polices moved. Do not "fix" it by relaxing the gate.

- **Case B — the pixels moved with nothing behind them.** Only the GIF (or only
  `baseline.json`) changed. Nothing that can affect a recording changed with it,
  so the new bytes came from a machine or a moment, not from the repository.
  This is the case that is expensive: because `assets/demo/*.gif` are binaries,
  a merge that carries them conflicts, and the regenerated output has to be
  thrown away and re-recorded.

The trap is that case A and case B look **identical in a diff** — both are "a
GIF got bigger". What separates them is not the artifact, it is the *other* files
in the same commit. That is why the question is asked as a procedure and not as a
rule of thumb:

> **Case A holds if and only if the commit carries at least one recording input
> that changed.** A commit carrying GIFs and nothing else is case B, whatever its
> message says.

Two corollaries that are easy to get wrong:

- **A red drift gate is not a request for a re-record.** On a push run the
  workflow records, gates and then commits back by itself; a maintainer who sees
  the red is looking at a gate that has already told the truth.
- **`baseline.json` is not a golden image.** It is a *numeric* reference — byte
  size, frame count, duration and a content-hash rollup — not a set of expected
  pixels. Nothing compares the GIF against it for visual equality, so re-recording
  the manifest never "confirms" a change was correct. Re-recording it is how you
  declare a change intentional, and it is a reviewed act, not a cleanup.

One fact to keep in mind while reading a gate report: **the manifest and the
committed artifacts are separate things, and they drift apart.** At the time of
writing, three of the four committed GIFs
(`hero`, `markdown`, `approval`) no longer match the `bytes`/`fileSha256` their
[`baseline.json`](../assets/demo/baseline.json) records; only `plain` does. That
is the expected consequence of a green push run — the gate passes, the GIFs are
committed, and the manifest is left alone because `record_baseline` defaults to
`false`. So "the manifest is behind the GIFs" is the normal state, and it is not
by itself evidence of a regression.

This is worth stating twice because it is the single easiest thing to get wrong,
and it was nearly got wrong in the other direction while writing
`tools/demo_repro.py check-manifest`. A check that requires the manifest to match
the committed GIFs sounds like the obvious missing gate. It is not: it would fail on
the documented happy path, on every green run that commits GIFs without
re-recording the manifest. What *can* be required is that the manifest is
well-formed — that every comparison the gate is about to make is defined at all.
See [What the drift gate compares](#what-the-drift-gate-compares).

## The procedure

Prerequisite for case A: your change must be a **recording input**. If it is not
in the list below, it cannot move the GIFs, and the trigger filter will not fire
for it either.

1. **Classify the commit before touching anything.** Run
   `git show --stat HEAD` (or `git diff --stat <base>...HEAD`) and ask the case-A
   question above. GIF bytes plus no recording input → stop, you are in case B.
2. **Change the input, not the artifact.** Edit the tape, the renderer, or the
   `harbor demo` code. Never hand-edit a `.gif`, and never commit a `.gif` that
   your commit does not explain.
3. **Let the workflow regenerate.** Push to `dev`. If the change is on the
   trigger surface the `demo-gifs` workflow records all four tapes, gates the
   result and commits the GIFs back with the subject
   `chore(demo): regenerate README GIFs [skip ci]`. **Pull after the run** — the
   GIFs arrive as a new commit on the branch, not in your working tree.
4. **Read the step summary before you read the diff.** The
   `Drift gate (size / frames / duration vs baseline)` step prints one table row
   per GIF and an explicit verdict list. That table is the evidence; the GIF bytes
   are not reviewable.
5. **If a GIF legitimately changed, re-record the manifest — deliberately:**

   ```bash
   gh workflow run demo.yml -f record_baseline=true
   ```

   This forces `enforce=false` for that run, so it can never both bless a change
   and pass on it; it refuses to write a partial manifest (all four GIFs must be
   present). The rewritten `baseline.json` comes back in the auto-commit.
6. **Say what you did in the PR**, using the two axes a reviewer cannot otherwise
   recover from a diff: which recording input changed, and which GIFs moved and by
   how much.

### A PR should not carry regenerated GIFs

This is the rule whose absence caused a real rollback. `demo-gifs` **deliberately
does not run on pull requests** — the workflow's own header says so, and gives the
reason: the run costs 8–14 minutes and its artifacts were never inspected in
review. Regeneration happens *after* merge, on the `dev` push.

So if your PR carries `assets/demo/*.gif`, the merge triggers a regeneration that
rediscovers the same change and rebases onto your GIFs. Those are binaries; Git
cannot merge them; the workflow aborts the rebase and the job goes red. Re-running
does not help, and force-pushing over the other side is explicitly not something
the workflow will do.

The one historical exception is PR #639, which changed the tapes, the toolchain
and the demo command *and* carried the regenerated GIFs in the same commit. That
works when the change set and its recording move together, which is exactly the
case-A shape — but it is a bulk migration, not a routine. For an ordinary
renderer change, push to `dev` and let the workflow do it.

**This rule is now a gate, not prose.** `ci.yml` lists `assets/demo/**` in *both*
of its `paths-ignore` blocks, so a PR that touches nothing but a GIF used to
trigger no `ci` run at all, and the push filter below does not cover the path
either — the rule was stated in three documents and enforced by none. The
`pr-no-gifs` job in [`demo.yml`](../.github/workflows/demo.yml) now runs on any
`pull_request` touching `assets/demo/**` and fails it outright. It reads the PR's
own file list (`git diff --name-only "$BASE_SHA" HEAD -- assets/demo/`, at
`fetch-depth: 0`) and fails loudly if the diff cannot be computed, rather than
reporting a PR it did not look at.

It is a `pull_request` trigger rather than another push path on purpose. The
recorder commits `assets/demo/` on every successful re-record, so adding the path
to `push` would make each run retrigger the next one; `[skip ci]` in its commit
subject is what prevents that today, and a suppression marker is a poor thing to
build a loop-prevention guarantee on when a trigger exists that cannot loop.

It checks **that** the recorded assets are untouched, not whether they are *good*.
Judging the pixels is the drift gate's job, on the merge.

### Trigger surface

| Fires on | Value |
|---|---|
| Branches | `dev`, `master` |
| `src/Harbor.Tui.**` | renderer changes |
| `src/Harbor.Terminal.Abstractions/**` | view/VM contract changes |
| `apps/Harbor.App.Cli/**` | the `harbor demo` command |
| `demo/**` | tapes, the scrubber, the installer, the toolchain pins |
| `tools/demo_repro.py` | the frame pacer the gate measures |
| `.github/workflows/demo.yml` | the workflow itself |

Two throttle layers sit in front of the recording. A `concurrency` group
(`demo-gifs-dev`, `cancel-in-progress`) keeps two runs from overlapping, and a
`gate` job skips the whole run when the last successful `dev` run finished less
than an hour ago. Manual dispatch always proceeds — the throttle is there because
merge velocity, not because a dispatch should be silently dropped.

The trigger list is not maintained by hand. `demo/**` and `tools/demo_repro.py`
were added by #427 because they were missing, and
[`DemoGifTriggerSurfaceTests`](../tests/Harbor.Architecture.Tests/DemoGifTriggerSurfaceTests.cs)
pins the coverage: every recording input must be matched by `on.push.paths`. The
failure mode it prevents is silent — an uncovered input lands on `dev` with no
workflow run, no gate and no red job, so case A quietly becomes case B.

## What the drift gate compares

[`assets/demo/baseline.json`](../assets/demo/baseline.json) records, per GIF:
byte size, dimensions, frame count, total duration, `uniqueFrames`, a whole-file
`fileSha256`, and `framesSha256` (a sha256 over every per-frame LZW payload
digest, in frame order). The gate walks the new GIF's block structure with a
stdlib reader — no decoder, no extra package.

| Check | Verdict | Default threshold |
|---|---|---|
| Size above baseline | **fail** | `+20 %` (`size_threshold_pct`) |
| Size above the absolute budget | **fail** | 5 MiB |
| Frames lost | **fail** | any negative delta |
| Duration changed | **fail** | `±500 ms` (`duration_tolerance_ms`) |
| Size grew, or frame count / duration moved under the limits | warn | — |
| No baseline entry for a GIF | warn ("new") | — |
| Missing or unreadable GIF, missing or unreadable manifest | **fail** | — |

**Both sides of every comparison are fresh.** `actual` is the GIF this run just
recorded; `baseline` is the manifest. No step compares a committed
`assets/demo/*.gif` against `baseline.json`, and none can: `Normalise frame pacing`
writes each fresh capture over the checked-out
`assets/demo/<name>-compressed.gif`, so by the time the gate runs, the workspace
copy *is* the recording. A `ℹ️ content` verdict therefore says "today's recording
differs from the last recording a human accepted" — which is what makes step 5 of
the procedure the right response — and **not** "the shipped GIF no longer matches
the manifest". The committed bytes are not an input to anything the gate
computes, so their divergence from `baseline.json` (the three-of-four drift
described under *What an "intentional update" actually is*) is outside every
verdict in the table. Treating it as a verdict is what turns a manifest that is
merely behind into a manifest that looks corrupt.

### What is reproducible, and what is not

The table above reads as though every field in the manifest were a number to
threshold. Measured over the **25 `demo-gifs` runs on `dev` from 2026-09-30 to
2026-10-01**, with one unchanged tape recorded 25 times, they split cleanly:

| Field | Distinct values in 25 runs | Verdict |
|---|---|---|
| `frames`, `durationMs` | `markdown` 204/17000 in 25/25, `approval` 264/22000 in 25/25, `plain` 132/11000 in 25/25, `hero` **bimodal**: 204/17000 in 23/25, 216/18000 in 2/25 | a constant of the toolchain — the only reproducible group |
| `bytes` | `hero` **22** distinct (287 428–303 650, a 5.64 % spread), `markdown` 19, `approval` 24, `plain` 3 | **not reproducible** |
| `framesSha256` | `hero` **23** distinct, `markdown` 19, `approval` 24, `plain` 3 | **not reproducible** |

Same input, same runner, same pinned toolchain, different bytes every time: the
encoder is not deterministic even though the pixels are. Two consequences, and they
are the reason this issue could not be closed by adding a threshold:

- **A size/quality drift gate on `bytes` would measure the encoder.** The +20 %
  limit happens to sit above the 5.64 % observed spread, so it does not fire on
  noise today — but the number it is comparing is not a property of the renderer,
  and widening it to catch a real regression would first have to swallow the noise.
- **`framesSha256` in the manifest is an identity for one accepted render, never an
  expectation for the next.** That is why the gate *enforces* frames and duration
  and only *reports* the hashes, and why "record-twice" exists as a separate opt-in
  (`reproducibility=true`) rather than as the gate's premise.

### The manifest is currently stale, and that is why every run is red

The manifest records `hero` at 216/18000 — the **2-in-25 minority** sample — and
`approval` at 312/26000 against a consistent 264/22000. The gate fails on any
negative frame delta and on a duration change outside ±500 ms, so those two entries
alone turn roughly 92 % of runs red. A red run skips the commit step, so the
README GIFs cannot refresh either: one stale manifest freezes both the gate and
the artifacts. Every `demo-gifs` run on `dev` has been red since 2026-09-29.

The repair is the documented one and it is **an owner action, not a code change** —
re-record from a real run so every field comes from one render rather than being
hand-assembled:

```bash
gh workflow run demo.yml -f record_baseline=true
```

Do not "fix" it by widening `duration_tolerance_ms` or re-ordering `frames` to
make the red go away. A red drift gate on a stale manifest is the gate telling the
truth about a reference that has not been re-declared since the recorder's output
moved.

### What *is* gated about the manifest

`python3 tools/demo_repro.py check-manifest` runs as its own step in the workflow
(`if: always()`), and it is deliberately **not** an equality check against the
committed GIFs — see above. It asserts that each comparison the drift gate is about
to make is *defined*:

| Manifest defect | What the drift gate does without this check |
|---|---|
| `gifs` map empty, or an entry dropped | every GIF becomes "no baseline" — a warning, and the run reads clean |
| `frames` / `bytes` / `durationMs` absent | `KeyError` — the step dies on a traceback, not a finding |
| `framesSha256` absent | reports "content changed" on **every** run, always |
| a digest that is not 64 lowercase hex chars | never matches anything, silently |
| `path` not in the tree | nothing to open |
| a non-positive or non-integer count | arithmetic or a percentage on a string |

Content-hash equality is reported in the table, not gated. Two dispatch inputs
change the posture, and both are logged into the step summary, so the run's mode
is always visible:

```bash
gh workflow run demo.yml                     # enforce (default)
gh workflow run demo.yml -f enforce=false    # annotate drift, never block
gh workflow run demo.yml -f record_baseline=true   # rewrite the manifest, report-only
gh workflow run demo.yml -f reproducibility=true   # record every tape twice, compare
```

## When the drift gate fails unexpectedly

Work down this list in order. Do not start by relaxing a threshold.

1. **Ask which case you are in.** An unexpected fail on a push you did not intend
   to re-record is the gate working. If the run was triggered by a merge, expect
   it.
2. **Prove the recording is reproducible** — this is the one check that separates
   "pixels changed" from "the machine was different today":

   ```bash
   gh workflow run demo.yml -f reproducibility=true
   ```

   It records every tape twice in the same job and fails unless the two passes
   agree on the frame envelope, every screen and the settled final screen. A
   failure here means the toolchain is not pinned tightly enough — go to step 4 —
   and **not** that the GIF needs re-recording.
3. **Confirm the toolchain pins are intact**
   ([`demo/toolchain.lock`](../demo/toolchain.lock)): vhs `0.11.0`, ttyd `1.7.7`,
   JetBrains Mono `2.304`, ffmpeg `7.0.2`, each with a sha256.
   [`demo/install-toolchain.sh`](../demo/install-toolchain.sh) verifies every
   download and fails if `fc-match "JetBrains Mono"` does not resolve to the pinned
   family. A font bump reflows every tape, and it is the single most common cause
   of an unexplained uniform size change across all four GIFs. The runner's ffmpeg
   version is printed into the step summary and recorded in the manifest, so a
   runner bump shows up as visible provenance rather than as silent byte drift.
4. **Bisect the tape.** Each tape's `Sleep` at the end has to cover the whole run
   (currently `DEMO_TAPE_TAIL_SECONDS=34`). A tail that is too short does not
   merely look worse — the recorder stops mid-scene and the GIF is silently
   missing most of its output, which the gate sees as **lost frames** and
   correctly fails. Note that a shortened tail also moves the duration, so one
   fault produces two findings.
5. **Check the frame budget.** `GIF_STATE_FRAMES=6`, `GIF_TAIL_FRAMES=24`,
   `GIF_FRAMES=432` as the hard ceiling. A scene that outgrows the ceiling fails
   loudly instead of being silently truncated.
6. **Only now** decide whether the gate's thresholds are wrong. Change them in the
   dispatch invocation or in the workflow defaults, and say which in the PR — a
   threshold raised to make a specific failure disappear is indistinguishable, six
   months later, from a threshold that was always too tight.

## When there are many goldens at once

The scenario: one merge legitimately moves all four GIFs, or a toolchain bump
moves every tape. The procedure does not change, only the batching.

- **Do not hand-commit sixteen regenerated binaries.** Let one push run produce
  them; they arrive as a single `chore(demo): regenerate README GIFs [skip ci]`
  commit.
- **Judge the *set*, not the files.** If all four moved together and you changed
  something global (the font pin, the pacer, the demo command), that is one
  change with four manifestations. If only some moved, you have two unrelated
  causes and have not finished diagnosing yet — go back to the bisect list.
- **Re-record the manifest once, not four times.** One
  `gh workflow run demo.yml -f record_baseline=true` rewrites all four entries and
  refuses a partial write. Four separate dispatches can interleave and leave the
  manifest describing a recording that never existed.
- **Expect the throttle.** A burst of merges inside an hour means later pushes are
  skipped, not that the run failed. If you need one now, dispatch manually — the
  gate job lets dispatch through unconditionally.

## How not to hide a regression

The temptation is always the same three moves. All three convert a caught problem
into an uncaught one, and the last one is the worst because it also destroys the
evidence.

- **Do not `-f enforce=false` a run you did not understand.** The relaxation is
  recorded in the step summary, but a relaxed run that nobody reads is a gate that
  looks present and does nothing. If you relax it, say so in the PR with the
  reason.
- **Do not re-record `baseline.json` to silence a failure you have not explained.**
  `record_baseline=true` forces report-only, so it cannot fail — which is exactly
  why it must never be the first thing you reach for. Re-baseline *after* the
  change is understood and deliberate, and put it in the same PR as the input that
  justifies it.
- **Do not widen a threshold to accommodate one recording.** `+20 %` and
  `±500 ms` are budgets for the whole set. A change that needs a bigger budget is
  a change that wants a look.
- **Do not force-push a recording over someone else's.** The workflow will not, by
  design: it rebases, and on a conflict it aborts and fails the job so a human
  decides. A green run that means "I threw the regenerated output away" is worse
  than a red run, because it reads as success and leaves the README stale with
  nothing recording that anyone noticed.
- **Do not "fix" a red guard by deleting its subject.** The trigger-coverage guard
  enumerates the recording inputs from the tree rather than from a list kept next
  to itself, precisely so it cannot be made green by editing the list.

## Troubleshooting

| Symptom | Cause | Where to look |
|---|---|---|
| Missing GIF, gate fails | the tape did not produce a capture | the `Record … demo GIF` step; `Output` in the tape |
| `lost frames` | the tape's final `Sleep` is shorter than the run | `DEMO_TAPE_TAIL_SECONDS` in the toolchain lock |
| All four GIFs grew | font or ffmpeg moved | `demo/toolchain.lock`; `fc-match "JetBrains Mono"` in the install step |
| One GIF grew, others flat | that tape's scene outgrew its budget | the `Normalise frame pacing` step's table; `GIF_FRAMES` |
| Wrong renderer in the output | the tape passes the wrong backend | `run-scene.sh <scene> <backend>` inside the tape |
| Blank or frozen frames | CellForge path broken — no pipe may sit between it and the tty | the tape header comment and `run-scene.sh`; do not add scrubbing to the CellForge tapes |
| `could not push regenerated GIFs after 5 attempts` | five lost races against `dev` | re-run; if it persists, the commit probably conflicts on the binaries |
| `rebase onto origin/… conflicts` | two GIF changes raced | a human resolves; re-record rather than force-pushing |
| Job skipped with `Last success … (<1h) — throttled` | the hourly throttle | wait, or `gh workflow run demo.yml` |

## Command reference

```bash
# CI — the normal route: push to dev and let the workflow regenerate.
gh workflow run demo.yml
gh workflow run demo.yml -f enforce=false            # annotate only
gh workflow run demo.yml -f record_baseline=true     # rewrite baseline.json
gh workflow run demo.yml -f reproducibility=true     # record twice and compare

# Local — record one tape by hand. Requires the pinned toolchain.
demo/install-toolchain.sh
vhs demo/hero.tape
python3 tools/demo_repro.py normalize \
  --input assets/demo/hero-raw.gif \
  --output assets/demo/hero-compressed.gif
python3 tools/demo_repro.py report --dir assets/demo

# Local — is baseline.json well-formed enough for the drift gate to mean
# anything? Does NOT assert it matches the committed GIFs: record_baseline
# defaults to false, so the manifest is expected to lag the artifacts.
python3 tools/demo_repro.py check-manifest

# Local — the in-process recorder behind HARBOR_DEMO=1. Needs a PTY plus
# ffmpeg; writes assets/demo/frames/<scene>/, which is gitignored scratch.
HARBOR_DEMO=1 dotnet run --project tests/Harbor.Tui.E2E.Tests -c Release
```

> **Not verified here.** The `HARBOR_DEMO=1` invocation is transcribed from
> [`TuiDemoRecorderTests.cs`](../tests/Harbor.Tui.E2E.Tests/TuiDemoRecorderTests.cs),
> whose doc comment still documents it as `dotnet test`. This repository's own
> guidance is that every suite runs as a plain executable via
> `dotnet run --project …`, and that `dotnet test` is not used because no CI job
> runs it — see
> [CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test)
> for what is actually known and what is not. The `dotnet run` form is what that
> guidance implies; the two are not reconciled here because doing so means
> changing a test project, which is outside this document. Use the `vhs` route
> above — it is what CI runs and what
> [`demo/hero.tape`](../demo/hero.tape) documents as the local equivalent.

## Known gaps

Stated rather than hidden, because a guide that claims more than it can deliver
is worse than a short one.

- **There is no PR-time A/B verdict, and building one honestly would be a lie.**
  The natural guard — "a PR that changes a GIF without changing a recording
  input is case B" — needs the changed-file list relative to the base ref, and CI
  checks out at depth 1, so a TUnit process cannot compute it. A check that
  claimed to answer it would be passing on an empty comparison. What is built
  instead is the half that *is* statically knowable: the trigger surface, which is
  what makes case A detectable at all. The review-time half stays a human
  judgement, and it is the first step of the procedure above for that reason.
- **The manifest tracks only numbers, not pixels.** A re-record that changes the
  image while preserving size, frame count and duration is not detected. That is
  inherent to a stdlib block-structure walker; visual regression would need a
  decoded-pixel comparison, which is a different tool and a different cost.
- **The `**` negation form (`!path`) is not modelled by the trigger guard.** No
  current filter entry uses it. Adding one would read as covering *less* than it
  does, which fails loud rather than silently passing.
