# Release checklist

> Owner runbook for cutting a Harbor release. A release is a `v*` tag. The
> [release workflow](../.github/workflows/release.yml) publishes it, so this
> page covers only what a human checks before the tag and verifies after it.
> For what each lane produces see [Daily and pre-release builds](./DAILY_BUILDS.md);
> for how users move to a new version see [Upgrading Harbor](./UPGRADE.md).

## Before the tag

- [ ] `dev` is green at the commit you are about to tag. The build is a
      zero-warning Release build, the test shards pass, and the docs gates
      (link and markdown lint) pass. Gate names and floors live in
      [CI gates](./CI_GATES.md).
- [ ] The AOT publish still works. The `aot-publish` job runs the same recipe
      the release uses and executes the published binary, with the warning
      baseline held by `harbor-gates aot-publish-gate`. See
      [AOT publish](./AOT_PUBLISH.md).
- [ ] The NuGet library set still packs and validates. Publishing to nuget.org
      is not wired, so this is a build check only. See
      [NuGet packaging](./NUGET_PACKAGING.md).
- [ ] The changelog section for the version exists in `CHANGELOG.md`, or the
      release notes will fall back to a bare commit list. Add one entry under
      `changelog/fragments/` for each user-visible change; the release notes
      extract the matching `## [version]` section from `CHANGELOG.md`.
- [ ] The version string reported by `harbor version` matches the tag you are
      about to push.

## Dry run the publish matrix

A manual dispatch never creates or mutates a Release. Use it to validate the
RID matrix before committing to a tag:

```bash
gh workflow run release.yml --ref dev
gh run list --workflow release.yml --limit 1
gh run view <run-id>
```

The dispatch publishes and smokes every RID, then preserves the archives as a
`harbor-release-dryrun` run artifact. The matrix ships `linux-x64`, `win-x64`,
`win-arm64`, `osx-arm64` and `osx-x64`. `linux-arm64` and `linux-musl-x64`
are deliberately excluded; the reasons are recorded at the top of the
workflow file. Every archive carries `providers/*.json` beside the binary,
because provider registration reads those files from disk and a bare binary
in a fresh directory registers no OpenAI-compatible provider.

## Cut the tag

```bash
git checkout dev && git pull --ff-only
git tag -s vX.Y.Z -m "Harbor vX.Y.Z"
git push origin vX.Y.Z
```

Pushing a `v*` tag starts the release workflow. It publishes each RID, creates
one archive per RID, writes `SHA256SUMS.txt`, generates release notes from the
changelog section plus the commit range since the previous tag, and creates
the GitHub Release. `workflow_dispatch` stops before Release creation, so a
tag is the only way to publish one.

## After the tag

- [ ] Every matrix leg succeeded. A leg that failed means no Release was
      created, because Release creation depends on all of them.
- [ ] The Release carries one archive per RID plus `SHA256SUMS.txt`.
- [ ] An extracted archive runs `Harbor.App.Cli --version` from a directory
      with no repository ancestor, and `providers` resolves from the files
      shipped beside the binary. This is the smoke the workflow already runs
      on the native legs; repeat it if you publish from a local archive.
- [ ] The release notes contain the version section, not just the commit
      list. If the section is missing, fix the `## [version]` heading in
      `CHANGELOG.md` and edit the Release notes in the GitHub UI. The workflow
      extracts that section verbatim, so a heading it cannot match is the
      usual cause.
- [ ] Announce the version and link the Release. Beta and source builds keep
      tracking `dev`, so state explicitly which lane the tag belongs to.

## Ownership

| Step | Owner | Where it runs |
| --- | --- | --- |
| Green `dev`, gates, AOT, pack | maintainer on duty | `ci.yml`, `docs.yml` |
| Changelog section | change author | `CHANGELOG.md`, `changelog/fragments/` |
| Dry run and tag | release owner | `release.yml` dispatch, local `git` |
| Release verification | release owner | GitHub Releases page |

## See also

- [Build & Publish Guide](./BUILD.md): local publish commands and options.
- [Daily and pre-release builds](./DAILY_BUILDS.md): beta, nightly and release lanes.
- [Upgrading Harbor](./UPGRADE.md): what an upgrade preserves and how to roll back.
- [Release engineering analysis](./RELEASE_ENGINEERING_ANALYSIS.md): the rationale behind the lanes.
