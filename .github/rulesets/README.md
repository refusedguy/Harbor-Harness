# Branch rulesets: the rules and why they are what they are

These two files are the declarative form of the repository's branch rules.
They are the source of truth for what GitHub enforces on `dev` and `master`,
and the reasoning for every field lives in this file.

| File | Branch | Ruleset id | What it is for |
|---|---|---|---|
| `dev.json` | `dev` | 25476493 | The integration branch. Every pull request lands here. |
| `master.json` | `master` | 25476506 | The release branch. Stricter. |

## Applying them

The API takes the file body verbatim, so the file in the repository and the
rule GitHub enforces are the same thing. The one exception is the `gate`
entry, which is explained in "What is applied right now".

```bash
# Create a ruleset that does not exist yet.
gh api --method POST repos/refusedguy/Harbor-Harness/rulesets \
  --input .github/rulesets/dev.json

# Replace the ruleset that already has that id.
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476493 \
  --input .github/rulesets/dev.json
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476506 \
  --input .github/rulesets/master.json
```

To read back what GitHub actually enforces:

```bash
gh api repos/refusedguy/Harbor-Harness/rulesets --jq '.[] | {id, name, enforcement}'
gh api repos/refusedguy/Harbor-Harness/rules/branches/dev
```

To remove a ruleset entirely:

```bash
gh api --method DELETE repos/refusedguy/Harbor-Harness/rulesets/25476493
```

The old ruleset `22332589` ("idk") is left in place, untouched and disabled.
It matches no branch, so it cannot affect a merge. It can be deleted with the
command above.

## The state before this change

Measured with the repository API on 2026-10-20, branch `dev` at `283a9dbe`:

```
GET /repos/refusedguy/Harbor-Harness/branches/dev/protection
  -> 404 {"message":"Branch not protected"}

GET /repos/refusedguy/Harbor-Harness/branches/master/protection
  -> 404 {"message":"Branch not protected"}

GET /repos/refusedguy/Harbor-Harness/rulesets
  -> one ruleset, id 22332589, name "idk", target "branch",
     enforcement "disabled", conditions.ref_name.include = [],
     rules = [deletion, non_fast_forward, pull_request]
```

The one ruleset that existed was inert three times over: its enforcement was
`disabled`, its `ref_name.include` list was empty so it matched no branch at
all, and its `pull_request` rule asked for zero approving reviews. Nothing
blocked a merge. Every gate this repository builds (the zero-warning build,
the architecture suite, the AOT publish gate, the public-API snapshots, the
test-project completeness gate) was advisory: it reported, and the merge
happened anyway.

The token used here is the owner's own (`refusedguy`, OAuth token with the
`repo` scope). The repository reports `admin: true` for that token, so the
API accepts writes to `/rulesets`. Both files above are applied.

## The required check list

Eight names. Every one of them is a name that GitHub reports on every pull
request head, which is the only property that matters: a required check
whose name never appears leaves the pull request waiting forever.

| Check | Workflow | What it catches |
|---|---|---|
| `gate` | `ci.yml` | The whole ci lane, in one always-reported name. It fails when a lane that the path filter says should have run did not pass. See "The gate job" below. |
| `public-api freeze` | `ci.yml` | A change to the frozen public surface that nobody re-signed with it. The job has no condition on it, so it reports on every pull request. |
| `checker self-test` | `docs.yml` | Proves the documentation checkers themselves still fail on broken input. |
| `internal links` | `docs.yml` | Broken internal links and anchors. |
| `markdown lint` | `docs.yml` | Markdown invariants. |
| `normative citations` | `docs.yml` | Documentation that cites a file, line or type that moved. |
| `test project coverage` | `docs.yml` | A test project that no CI run would execute. |
| `tree gates` | `docs.yml` | Every gate that needs nothing but the tree, in one command: `harbor-gates all`. It absorbed the `guarded project dependencies` and `xml-doc coverage` jobs, and it runs their gates with the same floors. |

Verify the list against a pull request head at any time:

```bash
SHA=$(gh pr view 1957 --repo refusedguy/Harbor-Harness --json headRefOid --jq .headRefOid)
gh api "repos/refusedguy/Harbor-Harness/commits/$SHA/check-runs?per_page=100" \
  --jq '.check_runs[] | "\(.name)|\(.conclusion)"'
```

### What is applied right now, and what lands with the pull request

The list above is the list in the two files, and it is the list to enforce
once the workflow change in this pull request is on `dev`. The `gate` job
does not exist on `dev` until then, and a required check whose name does not
exist blocks every pull request, so the rulesets are applied with the six
documentation gates only:

| | Required checks now | Required checks after the workflow change lands |
|---|---|---|
| `dev` (25476493) | the six documentation gates | `gate`, `public-api freeze` and the six documentation gates |
| `master` (25476506) | the six documentation gates | `gate`, `public-api freeze` and the six documentation gates |

One command per branch turns the `gate` on, and it is the same command as
"Applying them" above:

```bash
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476493 \
  --input .github/rulesets/dev.json
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476506 \
  --input .github/rulesets/master.json
```

The six documentation gates are reported on every pull request as soon as
the `docs.yml` trigger change in this pull request lands. Before that, a
pull request that touches none of the documentation paths (a change to
`tools/`, `samples/` or `providers/` only) does not report them, and the
interim list blocks it. That class is small and it disappears with the same
merge. The `gate` job reports on every pull request as soon as the `ci.yml`
trigger change lands, and so does `public-api freeze`, whose job has no
condition on it.

### What is deliberately not required, and why

| Check | Why not |
|---|---|
| `build`, `aot-publish`, `test/sample package set`, `perf-gate` | All of them skip themselves when the diff does not touch them, and the `gate` job already fails when one of them was supposed to run and did not. Requiring them separately would add nothing the gate does not already assert. |
| `test (core)`, `test (platform)`, `test (ui)`, `test (e2e)` | The name changes when the matrix is skipped. See "The test matrix has two names" below. |
| `test` | The same matrix, reported under its bare name when the job is skipped. It does not exist on a pull request whose matrix ran. |
| `changes` | The path filter is not a defect gate. The `gate` job asserts that it ran and succeeded. |
| `public-api snapshot (Abstractions)` | The old name of the public-API job. Open pull request #1944 renamed it to `public-api freeze` and deleted the second copy of it from `docs.yml`. The name settled on `public-api freeze` after that, so that is the required name. |
| `coverage` | Report-only by contract, with no thresholds. Open pull request #1923 also moves it off the pull request path. |
| `test duplication` | The gate runs with `--warn-only`, so it cannot fail and cannot gate anything. |
| `publish`, `publish to GitHub Pages` | Only run on `master` or on tags. |
| `docs site assembly` | Builds an artifact. It is not a gate. |
| `test-os (windows-latest)`, `test-os (macos-latest)` | `test-os.yml` only triggers on pull requests to `master`, never on pull requests to `dev`. It is also the single most expensive lane (19.5 runner-minutes) and issue #1787 is moving it to `push` and `schedule`. Requiring it would either hang every pull request or cost the whole budget. |

Two of the names that were required are GONE as jobs: `guarded project
dependencies` and `xml-doc coverage` were absorbed by the `tree gates` job of
`docs.yml`, which runs their gates (and the rest of the tree gates) with the
same floors, and the list names `tree gates` instead. A required check whose
name is never reported blocks every pull request forever, so the ruleset has
to be re-applied with the new name.

The consolidation has one consequence worth naming: `check-doc-facts` — the
repo-map count gate that was deliberately NOT required, because a count that
drifts on every added file is a nuisance rather than a defect — now runs
inside a required job, so a drifting count blocks a merge. The same is true
of `changelog-assemble --check` and of `check-local-merge-loss`, which the
`tree gates` job also runs. That is a policy change and it comes from the
consolidation, not from the gates: each of them was a gate before and each of
them was reported.

## A required check that never reports blocks the pull request forever

GitHub matches a required status check by exact check name. If the workflow
that produces the name does not run, the check never appears, the pull
request sits at "Expected - waiting for status to be reported", and it can
never be merged. Nothing times out.

Both gating workflows had a workflow-level path filter, so this was not a
hypothetical:

- `ci.yml` skipped the whole workflow when every changed file matched
  `**.md`, `docs/**`, `evals/tasks/**` or `assets/demo/**`.
- `docs.yml` skipped the whole workflow when no changed file matched its
  `paths:` list.

Pull request #1954, a documentation-only change, reported the twelve
`docs.yml` checks and none of the `ci.yml` ones. Pull request #1942, a
tooling change, reported every `ci.yml` name but no `checker self-test`, no
`internal links` and no `xml-doc coverage`. Requiring any name from the
missing workflow would have hung that whole class of pull requests.

Fix, in the same pull request as this file:

- `ci.yml` no longer filters `pull_request` at the workflow level. The
  job-level `changes` filter already decides which jobs run, so nothing
  changes about what executes. The heavy jobs now report "skipped" instead
  of not existing, and GitHub counts a skipped job as a successful check.
- `docs.yml` no longer filters `pull_request` at the workflow level either.
  Measured on run 39426596955, the eleven jobs take 5 to 13 seconds each
  (67 seconds for the citation gate), so about 2.3 runner-minutes and 67
  seconds of wall clock per pull request, against a baseline of 45.3
  runner-minutes and 19.5 minutes of wall clock. The gates are read-only and
  idempotent, so running them when their inputs did not change costs
  minutes and finds nothing new. That is the price of being able to
  require them.

The `push` trigger of both workflows keeps its filter. A push has no
required-check semantics to satisfy.

## The test matrix has two names

The `test` job in `ci.yml` is a matrix over four shards, and GitHub reports
it under one name when the matrix ran and under a different name when the
job was skipped by its `if:` condition:

| Situation | Check names GitHub reports |
|---|---|
| The matrix ran | `test (core)`, `test (platform)`, `test (ui)`, `test (e2e)` |
| The matrix was skipped | `test` |

Pull request #1938 reports the four shard names and no bare `test`.
Pull requests #1942, #1949 and #1923 report the bare `test` and none of the
four shard names.

Neither set can be required on its own. Requiring the four shard names hangs
every pull request where the matrix was skipped, because GitHub waits for
check runs that will never be created. Requiring the bare `test` hangs every
pull request where the matrix ran, because no run carries that name.

The `gate` job exists for the same reason, and for the reason above.

## The gate job

One job, always reported, one name. It is the check the merge policy looks
at, and it is the only required check from the `ci` workflow.

```yaml
  gate:
    name: gate
    needs: [changes, build, test, aot-publish, test-packages, perf-gate]
    if: ${{ always() }}
```

It reads the path filter and decides whether the lane outcomes are correct.
Its failure modes, all of them tested against a simulated `needs` payload:

| Situation | Outcome |
|---|---|
| The path filter failed, so no lane ran at all | Fails. Every other check would report "skipped", which GitHub counts as success, so a broken filter would let anything through. |
| The diff touches the .NET tree and `build` did not pass | Fails. |
| The diff touches the .NET tree and a test shard did not pass | Fails. |
| The diff touches the .NET tree and `aot-publish` did not pass | Fails. |
| The diff changes a package set and the package-set gate did not pass | Fails. |
| The diff touches the UI tree and `perf-gate` did not pass | Fails. |
| The diff touches none of them and every lane skipped | Passes. That is the filter working, not a hole. |
| A dependency bump, where every lane skips on purpose | Passes, and says so in the log. Every lane's condition excludes `dependabot[bot]`, so that skip is the intended behaviour, not a disagreement. |

The job results are read through `toJSON(needs)` rather than
`needs.<id>.result`, because two of the job ids contain a hyphen
(`aot-publish`, `test-packages`, `perf-gate`) and the expression language
reads a hyphen as a subtraction.

The gate does not depend on the public-API job, because that job's id moved
in open pull request #1944 and a `needs:` entry naming a job that does not
exist makes the whole workflow invalid, so the gate would stop reporting at
all. The public-API check is a required check in its own right instead, and
its job has no condition, so it reports on every pull request.

## The evidence

Two classes of pull request, measured against the ruleset with the list
above. The documentation-only pull request was a scratch one, created for
the measurement and closed again; the code one is this pull request.

| | Code pull request #1957 | Documentation-only pull request |
|---|---|---|
| `gate` | `failure` | `success` |
| `build`, `test`, `aot-publish`, `test/sample package set`, `perf-gate` | ran or skipped per the filter | `skipped`, the filter says the .NET tree was not touched |
| The six documentation gates | `success` | `success` |
| Every required context present on the head | yes, none missing | yes, none missing |
| Verdict with the list above | `MERGEABLE / BLOCKED`, the gate caught a failing build | `MERGEABLE / CLEAN` |

The gate is not a rubber stamp: on the code pull request it failed in three
seconds with the exact reason, while the pull request was otherwise green.

```
gate: changes.dotnet=true changes.packages=false changes.ui=false
gate: build=failure test=skipped aot-publish=skipped test/sample package set=skipped perf-gate=skipped
##[error]gate: the diff touches the .NET tree but 'build' finished as 'failure'.
```

The build that failed there is not a defect of that pull request. Its diff
touches two workflow files and three documentation files, and the failure is
a compile error in `tests/Harbor.Tools.Builtin.Tests/McpOAuthTests.cs` and
`apps/Directory.Build.props`, both of which are on `dev` itself and fail
there in the same way (the `dev` push run 39430304498 fails with the same
two errors). The repository is red for another change, and that is a
separate piece of work.

A documentation-only pull request must be measured against a head that
already carries the workflow change, because the `gate` job only exists
there. The measurement used a temporary ruleset on this branch with exactly
the list above, and the temporary ruleset was deleted again afterwards.

## A skipped check is not a blocking check

The question that decides whether `aot-publish` can be required: does a
required check whose conclusion is `skipped` block the merge?

GitHub's documentation says no. From "Using conditions to control job
execution": "A job that is skipped will report its status as 'Success'. It
will not prevent a pull request from merging, even if it is a required
check."

The repository's own API answers the same question, and the answer was
measured rather than quoted. On pull request #1923, whose head reports
`aot-publish=skipped` and `build=failure`, the `dev` ruleset was temporarily
narrowed to one required check at a time:

| Required check | Its conclusion on #1923 | `mergeStateStatus` |
|---|---|---|
| `aot-publish` | `skipped` | `MERGEABLE / UNSTABLE`, that is, not blocked |
| `build` | `failure` | `MERGEABLE / BLOCKED` |

Same pull request, same ruleset, two different conclusions. A skipped
required check does not block. A failing one does.

So the AOT gate does not need an always-running job of its own. The `gate`
job already asserts that the lane which should have run did run, which is
the same class of check with a wider reach. And the AOT gate still gates:
the path filter that decides whether it runs includes `src/**`,
`harbor-gates aot-publish-gate` and `.github/aot-warning-baseline.txt`, so a
change to the AOT surface always turns it on, and when it is on it compares
the published binary against the committed record. On a pull request that
does not touch the AOT surface it is skipped, it reports success, and there
is nothing to check anyway.

## The one follow-up step

The step that is still open is applying the whole list, which needs the
`gate` job on `dev`, which needs the workflow change in this pull request:

```bash
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476493 \
  --input .github/rulesets/dev.json
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476506 \
  --input .github/rulesets/master.json
```

There is a second one, and it has an ORDER. The `tree gates` job of
`docs.yml` absorbs the `guarded project dependencies` and `xml-doc coverage`
jobs, so the two files name `tree gates` where they named those two. The
ruleset that is applied RIGHT NOW still names the old two, and a required
check whose name is never reported blocks every pull request forever. So the
rulesets are re-applied AFTER the change is on `dev` — before it, the name
`tree gates` does not exist yet, and applying the new list would block every
pull request in exactly the same way.

```bash
# AFTER the pull request that removes the two jobs is on `dev`:
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476493 \
  --input .github/rulesets/dev.json
gh api --method PUT repos/refusedguy/Harbor-Harness/rulesets/25476506 \
  --input .github/rulesets/master.json
```

## "Require branches to be up to date"

This is the `strict_required_status_checks_policy` field. It is off on `dev`
and on for `master`.

The setting makes the branch head contain the current base tip before the
merge. When `dev` moves, every open pull request stops satisfying it, so every
open pull request has to rebase and re-verify. The cost is not the rebase, it
is the CI that the rebase triggers: one full pull request run per open pull
request per merge.

With 20 or more open pull requests and the merge rate this repository runs at
during a night shift, that is on the order of 166 extra CI runs per hour. At
the measured baseline of 45.3 runner-minutes per pull request run, 166 runs
per hour is 125 runner-hours per hour. The setting does not make the gates
stronger on `dev`, it makes them unaffordable, and the cheapest way for the
gates to be ignored is for them to be unaffordable.

`master` moves a few times a month, so the same setting there costs one
rebase per open pull request every few weeks, and it buys a real guarantee
that the release branch was tested against its own tip. It is on.

When the CI budget changes, the cheap way to get the `dev` guarantee is a
merge queue: it re-tests the pull requests that are about to merge against
the live base, one batch at a time, instead of making every open pull request
rebase. Add it with a `merge_queue` rule in the ruleset, after the merge
queue is enabled in the repository settings.

## Pull requests, reviews, and administrators

`required_approving_review_count` is 0 on `dev` and 1 on `master`.

The reason is that a GitHub approval is a thing a person does and Dependabot
cannot do it. With a count above 0 on `dev`, every automated dependency bump
would sit unmergeable until a human clicked approve, ten at a time, weekly.
Nobody reviews pull requests in this repository, so a count above 0 on `dev`
would be a gate nobody can pass. On `master` the count is 1 because the
release branch should have a person in the loop, and a dependency bump
destined for `master` is rare enough that the tax is acceptable.

The other pull request settings:

- `dismiss_stale_reviews_on_push: true` drops approvals when new commits are
  pushed, so an approval covers the code that was reviewed and not the code
  that was replaced.
- `require_last_push_approval: false` keeps an approval valid across the
  last push. With nobody reviewing, the stricter setting only produces pull
  requests that nobody can unblock.
- `required_review_thread_resolution: true` requires every review thread to be
  resolved.
- `require_code_owner_review: false` stays false because the repository has
  no `CODEOWNERS` file. Adding one is worth doing for the review workflow
  itself, but requiring a code owner review before one exists would block
  every pull request that touches the owned paths, with nobody able to
  satisfy it.
- `allowed_merge_methods` keeps both `squash` and `merge`. The night shift
  merges with `--squash`, and `tools/pr-ops.sh` merges with `--merge`;
  allowing both keeps neither path broken. Narrow the list if the owner
  decides on a single history style.
- `deletion` and `non_fast_forward` stop a force push and a branch deletion
  on the protected branch.

`require_extra_approval_for_unattributed_changes` was in the first draft of
these files and has been removed. It is not part of the documented ruleset
schema for the `pull_request` rule, it had no meaning that could be
verified, and it was one of the two things that declined the night shift's
pushes.

## The bypass actor

```json
{ "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" }
```

plus the owner by id:

```json
{ "actor_id": 40535288, "actor_type": "User", "bypass_mode": "always" }
```

**The administrator bypass must stay. A hotfix must always be mergeable.**
The `pull_request` rule requires every change to reach `dev` through a pull
request, so a direct push is a violation and GitHub declines it with "push
declined due to repository rule violations". The night shift merges pull
requests that touch `.github/workflows/*.yml` with a local squash merge and a
direct push, because the GitHub merge API refuses to merge a pull request
that changes a workflow file when the token lacks the `workflow` scope, and
the token in use does not have it. `gh pr merge --admin` is the same lever
from the other direction.

There is no GitHub App in this repository:
`GET /repos/refusedguy/Harbor-Harness/installation` answers 401 "A JSON web
token could not be decoded", so there is no installation id and no app id to
use as an `Integration` actor. The only identity that can push to this
repository is the owner, whose SSH key is the git remote's authentication
(`git@github.com:refusedguy/Harbor-Harness.git`). Actor id 5 is the
repository's `admin` role, which in a repository with one collaborator is
that same identity.

The cost of the bypass is real and it is worth stating plainly: with the
administrator as an `always` bypass actor, the owner's own merges are not
subject to the required checks. The night shift's own policy in
`tools/pr-ops.sh` (`pr-merge-if-green`, which refuses on any `fail` or any
`pending` row) is what still stands between a red pull request and a merge
in the owner's name. Every other identity, Dependabot included, is bound by
the ruleset.

Two ways to get the checks binding for the owner's merges as well, both
one-command changes:

1. **Grant the `workflow` scope and use the merge API for everything.**
   `gh auth refresh -s workflow` adds the scope to the token, the GitHub
   merge API can then merge workflow-touching pull requests, the night shift
   stops needing the local squash and push path, and the bypass mode can go
   back to `pull_request` (or the actor can be removed). This is the
   recommended route: it keeps one merge path and keeps the gates binding.
2. **Give the push path its own identity.** A deploy key added to the
   repository (`POST /repos/refusedguy/Harbor-Harness/keys`) can be a
   `DeployKey` bypass actor with `bypass_mode: always`. The night shift
   pushes with that key, and the owner's token merges stay gated by the
   required checks.

## What this does to the night shift

The night shift merges with `gh pr merge --squash --delete-branch`, using the
owner's token.

- A merge is refused by GitHub until the required checks are green. That is
  the point of the change. The loop's own green-check policy was already
  doing this, but it was a convention inside one script rather than a
  repository rule, and nothing stopped a merge made by any other route.
- The review requirement does not stop the loop. The owner's token is the
  bypass actor, and the count on `dev` is 0 anyway.
- The delete of the feature branch after the merge still works. The
  `deletion` rule protects `dev` itself, not the head branch of the pull
  request.
- With "require branches to be up to date" off on `dev`, the loop does not
  have to rebase and re-verify before every merge. That is the cost the CI
  economics measured, and it is the reason the setting is off.
- **A pull request whose head commit predates a gate does not report that
  gate.** The check name does not exist on that commit, so the pull request
  waits for a status that will never arrive, and it needs a rebase (or any
  new commit) before it can merge. This applies to the `gate` job from the
  moment the workflow change lands, and to `test project coverage`, which was
  added recently. The night shift should update the branch before merging
  rather than treat the pull request as stuck.
