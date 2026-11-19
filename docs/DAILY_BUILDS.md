# Daily and pre-release builds

Harbor does not publish a separate nightly package feed. Three GitHub Actions
workflows attach artifacts to their runs, and the freshest build of `dev` is
the source tree itself. This page says which lane produces what and how to
reach it, so a coverage report is never mistaken for a runnable CLI.

| Lane | Runs on | Artifact | Retention |
| --- | --- | --- | --- |
| [beta.yml](../.github/workflows/beta.yml) | every push to `dev` and manual dispatch | `harbor-cli-linux-x64` (tarball plus `SHA256SUMS.txt`) | 14 days |
| [nightly.yml](../.github/workflows/nightly.yml) | push to `dev`/`master`, a two-hour schedule, and manual dispatch | `coverage-cobertura` and `build-measurement` | workflow default |
| [release.yml](../.github/workflows/release.yml) | a tagged release | one archive per RID, `harbor-<rid>.*` | 7 days |

The beta tarball is the closest thing to a pre-release CLI: it is rebuilt on
every merge into `dev` and kept for 14 days. There is no signed nightly
installer.

## Where to find the artifacts

Open the Actions tab of the repository and choose a workflow by name:

- [beta runs](https://github.com/refusedguy/Harbor-Harness/actions/workflows/beta.yml)
  carry the Linux x64 CLI tarball and its checksum file.
- [nightly runs](https://github.com/refusedguy/Harbor-Harness/actions/workflows/nightly.yml)
  carry coverage and build-measurement reports.
- [release runs](https://github.com/refusedguy/Harbor-Harness/actions/workflows/release.yml)
  carry the tagged archives.

From the CLI, list the most recent runs with `gh run list --workflow beta.yml`
and open one with `gh run view <run-id>`. Verify a downloaded archive against
`SHA256SUMS.txt` before running it.

## What the nightly lane actually checks

The nightly workflow is a verification lane, not a distribution channel. It
runs the zero-warning Release build, collects report-only coverage, and writes
the analyzer and build measurements described in [CI gates](./CI_GATES.md).
Coverage has no threshold and no fail-on-number, so a green nightly says the
tree builds and its tests ran; it does not certify a downloadable binary.

The two-hour schedule skips a run when the newest commit on `dev` is younger
than an hour, because the push that merged it already covered that commit.

## Building the exact tip yourself

When you need a specific commit rather than the newest beta artifact, clone
the repository and build from source. [Getting started](./GETTING_STARTED.md)
covers the first build and [Build](./BUILD.md) covers the publish options.
Run `harbor version` after building to confirm which revision you have.

## Stability warning

Beta and source builds track `dev` and can break between merges. They receive
no support guarantee. Use a tagged release from the release lane when you need
a reproducible version, and keep the API freeze and gate rules in mind when
building a plugin against a pre-release CLI.
