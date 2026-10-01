# What the Kilocode gateway actually serves — the probe that settles #937

- Status: **dated measurement, not normative** (see the cites-gate note below)
- Date: 2026-10-01, against `dev` = `21db0290`
- Question: #937 — which free model does `providers/kilocode.json` name, and is
  that model one the provider serves?
- Verdict: **the declared default is not served. The live evals path names a
  model that is. `tencent/hy3:free` is absent from the catalogue; `kilo-auto/free`
  is present and free.**

This file records what was checked and what was **not**. It does not change the
value. That decision is the owner's; §5 says why the evidence is not enough to
make it here, even though it is enough to say the current value is wrong.

## §1. The probe — one unauthenticated GET

The endpoint is the one the product itself uses: `providers/kilocode.json`
declares `modelsUrl: https://api.kilo.ai/api/gateway/models` and
`modelsPath: "data"`. No key was sent; no CLI was run; no repository code was
executed.

```
$ curl -sS -o /tmp/kilo_models.json -w '%{http_code} %{size_download}B' \
      https://api.kilo.ai/api/gateway/models
200 494120B
```

**The catalogue is public.** No `KILO_API_KEY` was required and none was
available. This is the single most consequential fact in this file, and it
contradicts the premise under which #937 was filed: the fork was resolvable
without a key, on the first attempt, at any time since the endpoint was
published.

The response carries 397 models under `data`. Every `id` below was read out of
that one response and nothing else.

## §2. What the catalogue says

| id asked about | in the catalogue? | what the catalogue says about it |
|---|---|---|
| `tencent/hy3:free` | **no** | — |
| `hy3:free` (bare half) | **no** | — |
| `tencent/hy3` | yes | **paid**: `prompt 1.3e-7`, `completion 5.3e-7` |
| `tencent/hy3-preview` | yes | **paid**: `prompt 1.8e-7`, `completion 6.0e-7` |
| `kilo-auto/free` | **yes** | `isFree: true`, every price field `0`, ctx 256000 |

**The `:free` suffix on hy3 is gone, and hy3 itself is no longer free.** That is
the substantive finding, and it is stronger than "the id was renamed": the
`tencent/` family is present in the catalogue but priced per token, so
`tencent/hy3:free` is not a stale spelling of something that still works for
free — the free tier of that model is gone. A reader who runs
`export HARBOR_MODEL=kilocode/tencent/hy3:free` today gets a 404 on the model
id, and one who "fixes" it by dropping `:free` gets a **paid** model, which
breaks the "$0, no card" promise every document makes.

`kilo-auto/free` is present and unambiguously free (`isFree: true`, all-zero
pricing). It is a **router**, not a fixed model: its `description` reads
"Rotates through available free models. Limited capability and no credits
required", and `autoRouting.models` lists four upstream ids
(`dots-studio/dots-3-note-preview:free`, `nvidia/nemotron-3-ultra-550b-a55b:free`,
`poolside/laguna-s-2.1:free`, `stealth/space-bunny-alpha`).

**14 of the 397 models carry a `:free` suffix**, so `:free` is a live convention
on this gateway, not a retired one. The absence of `tencent/hy3:free` is a fact
about that model, not about the naming scheme.

## §3. The history, which sides with the catalogue

```
$ git log -1 --format='%h %ad %s' --date=short de9fb741
de9fb741 2026-09-09 evals: switch live model to kilocode/kilo-auto/free (hy3 dead)

$ git log -1 --format='%h %ad %s' --date=short 15eef9da
15eef9da 2026-09-29 fix(#649): AGENTS.md назвал не ту бесплатную модель в пяти местах
```

`de9fb741` changed **two** files: `AGENTS.md` and `evals/profiles/local.json`.
Both went to `kilo-auto/free`, and the commit subject says why in three words:
**hy3 dead**.

`15eef9da`, three weeks later, reverted **only `AGENTS.md`** — five lines, one
file — back to `tencent/hy3:free`, on the stated premise that
`providers/kilocode.json` is the source of truth. It did not touch
`evals/profiles/local.json`, which therefore still says `kilo-auto/free`. The
fork was created by a partial revert of a commit whose message was a
measurement.

So the two values have opposite provenance:

- `tencent/hy3:free` — asserted since the initial commit, **never verified**,
  and now contradicted by the provider's own catalogue.
- `kilo-auto/free` — chosen by someone who wrote down that hy3 was dead, on the
  live evals path, and **confirmed present and free** by the catalogue today.

## §4. What the repo currently asserts, and where

Measured on `dev` = `21db0290`.

| site | value | notes |
|---|---|---|
| `providers/kilocode.json:6` | `tencent/hy3:free` | `defaultModel`; since the initial commit |
| `src/Harbor.Application/Configuration/ConfigSections.cs:13` | `kilocode/tencent/hy3:free` | `FallbackModel` — what a default install with no `HARBOR_MODEL` actually gets |
| `AGENTS.md` ×5, `README.md` ×3 | `hy3:free` | the reader-facing claims |
| `docs/GETTING_STARTED.md` ×8, `docs/DEVELOPMENT.md`, `docs/PROJECT_STATUS.md`, `samples/ide-client/README.md`, `apps/Harbor.App.Avalonia/README.md` | `hy3:free` | |
| `apps/Harbor.App.Avalonia/Program.cs:31` | `hy3:free` | the `--help` banner |
| `evals/profiles/local.json:2,9` | **`kilo-auto/free`** | live evals path |
| `docs/EVALS.md:52`, `CHANGELOG.md:279` | **`kilo-auto/free`** | `CHANGELOG` is a historical record — see §6 |

Two test-side pins also name it, and they are the ones that make the move
deliberate rather than mechanical: `DefaultModelIdentityTests.ExpectedDefaultModel`
(`tests/Harbor.Config.Tests/`) and the `Declared` literal in
`DefaultModelDocClaimTests.Matcher_AcceptsAProseShapeThatAbbreviatesTheModelPath`.
Both are meant to fail when the value moves. That is the design working.

`providers/kilocode.json`'s `$schema` points at `../schema/provider.schema.json`,
which **does not exist in the tree** (`find` returns nothing). Pre-existing,
unrelated to #937, recorded here only because it means the provider files have
no schema validation standing behind them.

## §5. Why this file does not change the value

The evidence is one-sided about the current value: `tencent/hy3:free` is not
served. But "this id is dead" is not the same question as "this id should
replace it", and three things are unresolved:

1. **Whether a rotating router is the right default at all.** `kilo-auto/free`
   picks a different upstream model per request. Every document in this repo
   shows expected output — `142↑ 87↓`, a context bar, a status line — that a
   router will not reproduce run to run. That is a product decision about what
   the docs are allowed to promise, and it is not derivable from a catalogue.
2. **Whether `$0` is still the promise.** It is, for `kilo-auto/free`. But the
   router's own description carries a warning: *"Prompts may be logged by the
   upstream provider and used to improve their services. Not suitable for
   production or sensitive data workloads."* Whether Harbor's default should
   be a model with that property is the owner's call, and the docs that would
   have to say so are the same ones being changed.
3. **The evidence is one unauthenticated response, one moment, one region.** It
   is strong and it is fresh, and it is still a snapshot. The owner's `KILO_API_KEY`
   would confirm it against the authenticated catalogue in one request.

So: the current value is **disproved**; the replacement is **not chosen**. §6 says
what is being done instead.

## §6. What is being done instead of choosing

Not "fix the docs", which is what #649 did and which is why this fork exists.

1. **A machine-checkable note of the fork, next to the value.** `AGENTS.md`
   already carries one (`## E2E testing`); this file is the dated evidence
   behind it, with the catalogue response and the git history, so the claim
   "unverified" can be replaced by "disproved on 2026-10-01" without a live key.
2. **A guard that fails when the declared default stops being served.** That
   check needs the catalogue, so it cannot be a CI gate — see the note in
   `tests/Harbor.Architecture.Tests/DefaultModelDocClaimTests.cs` about what
   that file's reference can and cannot be.
3. **The E2E label.** `AGENTS.md` calls Kilocode the "E2E-verified" provider.
   Verified against what? `HARBOR_E2E` gates exactly one test in this repo, and
   it is an MCP process test (`tests/Harbor.Tools.Builtin.Tests/McpProcessClientStartInfoTests.cs:114`),
   not a provider test. **No test in `tests/` asserts that any provider serves
   any model.** The label is a manual run that nothing re-checks.

## §7. Not verified

- **No authenticated request was made.** The catalogue may differ for a keyed
  client. Unverifiable here — `KILO_API_KEY` is empty and running the CLI is
  out of scope.
- **No completion was attempted** against either id. Presence in a catalogue is
  not reachability, quota, or rate limit. The evals workflow already documents
  the free tier as rate-limited (`docs/EVALS.md`).
- **`kilo-auto/free`'s four `autoRouting.models` were not resolved.** Whether
  each upstream id still resolves is a second question this probe does not
  answer.
- **Nothing was run.** No `dotnet`, no test project, no CLI, no repository
  script. Every `file:line` above was read from the tree and will be re-checked
  by `tools/check-doc-cites.py` in CI. This document is deliberately **not**
  marked `Status: normative`, so that gate does not apply to it — the ADR-012
  banner explains why that marker is a claim about machine-checkability, and
  this file would rather not make it.
