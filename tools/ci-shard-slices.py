#!/usr/bin/env python3
"""Gate the build-output slices against the test shards (issue #1803's CI lane).

Stdlib only — no pip, no network, no dotnet build. Wired into the `build`
job of .github/workflows/ci.yml, and it guards ONE thing:

    every project the `test` job runs must be carried by the build-output
    artifact that job downloads.

WHY THIS FILE EXISTS

  The build job used to upload ONE artifact with the whole solution's
  binaries in it, and every consumer downloaded the whole thing. That is
  448 MiB handed to six jobs to run one shard each. The build job now
  uploads one artifact per consumer, and each shard's artifact carries
  only that shard's projects' output directories.

  The combined `solution-binaries` artifact that used to serve the
  `coverage` job is GONE: the coverage job moved to the nightly lane
  (.github/workflows/nightly.yml), where it builds the tree it measures
  rather than downloading it — a workflow cannot consume another
  workflow's artifacts. Which projects a CI run NAMES is still checked,
  by the test-project completeness gate (tools/check-test-projects.py),
  which reads the coverage list from the nightly workflow.

  That turns one list into two. The shard's project list lives in the
  `test` job's matrix; the slice's project list lives in the build job's
  upload step. They are the same list, written twice, 200 lines apart in
  one file, and NOTHING connected them: the first version of the split
  shipped with a shard list and a slice list that had drifted by one
  project, and the shard failed with

      Missing tests/Harbor.TestKit.Generators.Tests/bin/Release/net10.0/
      Harbor.TestKit.Generators.Tests.dll — did build artifact download fail?

  which points at the download, not at the list that caused it. A checker
  that stops checking is still green on every PR, so this one is the
  guard: it fails the build when the two lists disagree, and it fails
  LOUDLY (rather than silently passing) when it cannot read the file it
  is supposed to read.

WHAT IT CHECKS

  1. Every project in a shard's `projects:` list appears in that shard's
     slice (the upload step named `solution-binaries-<shard>`, or the one
     the shard's `artifact:` key names).
  2. The slices that run the CLI carry the CLI's own output directory
     (`apps/Harbor.App.Cli/bin/Release/net10.0/`). Two test helpers
     start the CLI from THERE, not from the test project's copy of the
     assembly: tests/Harbor.E2E.Framework/CliDriver.cs (the e2e shard)
     and tests/Harbor.Tui.CellForge.PtyTests/CellForgePtyScenarioBase.cs
     (the ui shard).
  4. A shard whose `mirror:` key names another shard runs the SAME
     project list as that shard. The arm64 spike (issue #1919) writes the
     four shard lists a second time, and the two copies are the only
     thing standing between the shards and a silent drift.
  5. A shard that runs on an arm64 runner gets a slice that carries the
     linux-arm64 NATIVE ASSETS as well as the linux-x64 ones. The build
     is RID-agnostic, so the arm64 assets exist in the build output; the
     upload globs pick them per RID. A slice that carries only the x64
     ones runs on x64 and fails on arm64 with a DllNotFoundException —
     a failure that names the missing library, not the missing glob.
  6. Every shard says WHICH RUNNER it runs on (`runner:`), because the
     job's `runs-on` is `${{ matrix.runner }}` — a shard without one has
     no runner at all, and, worse, check 5 would silently stop asking
     for the native assets that runner needs.

HOW IT READS THE FILE

  A regex over the workflow text, not a YAML parser: PyYAML is not on the
  runner image, and the shapes it looks for are fixed by this file's own
  formatting (`- shard: <name>` … `projects: >-` … one project per line,
  and `name: solution-binaries…` … `path: |` … one glob per line).

  If it cannot find what it expects, it says so and exits non-zero. A
  checker that quietly checks nothing is the failure mode this file
  exists to prevent.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parent.parent / ".github" / "workflows" / "ci.yml"

# The slices that start the CLI, and therefore need the CLI's output
# directory. Keep in sync with the two helpers named in the docstring.
CLI_CONSUMING_SLICES = ("solution-binaries-ui", "solution-binaries-e2e")
CLI_OUTPUT_DIR = "apps/Harbor.App.Cli/bin/Release/net10.0"


def read_shard_lists(text: str) -> dict[str, dict]:
    """shard name -> what the matrix entry says about that shard.

    The entry carries four things:

      * `projects` — the projects the shard runs.
      * `mirror`   — the shard whose project list this one must equal. The
        arm64 spike (issue #1919) runs the SAME shards as the x64 `test`
        job, so its lists are written a second time; `mirror` names the
        list they must agree with, and this checker is what makes the
        second copy safe to maintain.
      * `artifact` — the build-output slice the shard downloads. It
        defaults to `solution-binaries-<shard>`, but the arm64 spike
        downloads the SAME slice as its x64 twin (the build produces one
        set of binaries), so it names it explicitly.
      * `runner`   — the runner the shard runs on. A shard on an arm64
        runner needs the arm64 native assets in its slice.
    """
    shards: dict[str, dict] = {}
    for match in re.finditer(r"- shard: (\S+)\n(.*?)(?=\n\s+- shard: |\n\s+steps:)", text, re.S):
        shard, block = match.group(1), match.group(2)
        lines = block.splitlines()
        start = next((i for i, ln in enumerate(lines) if ln.strip().startswith("projects:")), None)
        if start is None:
            continue
        projects: list[str] = []
        for line in lines[start + 1:]:
            if line.strip() and line.startswith(" " * 10):
                projects.append(line.strip())
            elif projects:
                break
        if not projects:
            continue
        entry: dict = {"projects": projects}
        for line in lines[:start]:
            option = re.match(r"\s+(mirror|artifact|runner):\s*(\S+)\s*$", line)
            if option:
                entry[option.group(1)] = option.group(2)
        shards[shard] = entry
    return shards


def read_slices(text: str) -> dict[str, dict[str, set[str]]]:
    """artifact name -> {output directory: the kinds of file its globs carry}."""
    slices: dict[str, dict[str, set[str]]] = {}
    pattern = re.compile(
        r"name: (solution-binaries(?:-[a-z0-9-]+)?)\n\s+path: \|\n((?:\s{12,}.*\n)+)")
    for match in pattern.finditer(text):
        artifact, body = match.group(1), match.group(2)
        carried: dict[str, set[str]] = {}
        for line in body.splitlines():
            line = line.strip()
            if not line or line.startswith("#") or line.startswith("!"):
                continue
            glob = re.match(r"((?:tests|apps|src)/.+?/bin/Release/net10\.0)/(.*)$", line)
            if not glob:
                continue
            base, tail = glob.group(1), glob.group(2)
            if tail == "*.*":
                kind = "files"
            else:
                # The native assets are per-RID: an x64 runner loads the
                # linux-x64 ones and an arm64 runner the linux-arm64 ones,
                # so the RID is part of what the slice carries. A slice
                # that carries only linux-x64 native assets runs on x64 and
                # fails on arm64 with a DllNotFoundException, and the
                # failure names the missing library, not the missing glob.
                native = re.fullmatch(r"runtimes/linux-([^/]+)/native/\*", tail)
                kind = f"native:{native.group(1)}" if native else tail
            if tail == "*/Harbor.*.resources.dll":
                kind = "satellite"
            carried.setdefault(base, set()).add(kind)
        slices[artifact] = carried
    return slices


# The kinds of file a project's slice must carry: the project's own
# assemblies and JSON, the linux-x64 native assets, and Harbor's own
# satellite assembly.
REQUIRED_KINDS = {"files", "native:x64", "satellite"}

# The arm64 runners (issue #1919) load the linux-arm64 native assets, so a
# slice that an arm64 shard consumes must carry those too.
ARM_REQUIRED_KINDS = {"native:arm64"}


def main() -> int:
    if not WORKFLOW.is_file():
        print(f"::error::workflow not found: {WORKFLOW}")
        return 1
    text = WORKFLOW.read_text(encoding="utf-8")

    shards = read_shard_lists(text)
    slices = read_slices(text)

    if not shards:
        print("::error::no shard lists found in the `test` job's matrix — "
              "this checker cannot see what it guards")
        return 1
    if not slices:
        print("::error::no build-output slices found in the `build` job — "
              "this checker cannot see what it guards")
        return 1

    problems: list[str] = []

    for shard, entry in sorted(shards.items()):
        projects = entry["projects"]
        artifact = entry.get("artifact") or f"solution-binaries-{shard}"
        carried = slices.get(artifact)
        if carried is None:
            problems.append(f"the `{shard}` shard has no `{artifact}` upload step")
            continue

        # The arm64 spike (issue #1919) writes the shard's project list a
        # second time. `mirror` names the shard whose list it must equal,
        # and this is the check that keeps the two copies from drifting:
        # a project added to one and not the other would run in one shard
        # and not the other, and nothing else in CI would say so.
        mirror = entry.get("mirror")
        if mirror:
            source = shards.get(mirror)
            if source is None:
                problems.append(
                    f"the `{shard}` shard mirrors `{mirror}`, which is not a shard — "
                    f"the mirror name does not match any matrix entry")
            elif source["projects"] != projects:
                only_here = [p for p in projects if p not in source["projects"]]
                only_there = [p for p in source["projects"] if p not in projects]
                problems.append(
                    f"the `{shard}` shard's project list differs from the `{mirror}` shard's "
                    f"it mirrors — only in `{shard}`: {only_here}; only in `{mirror}`: {only_there}")

        # The runner is not optional. The job's `runs-on` is
        # `${{ matrix.runner }}`, so a shard without one has no runner at
        # all — and, worse, the check below would silently stop asking for
        # the native assets that runner needs.
        runner = entry.get("runner")
        if not runner:
            problems.append(
                f"the `{shard}` shard does not say which runner it runs on — the job's "
                f"`runs-on` is `${{{{ matrix.runner }}}}`, so the entry needs a `runner:` key")
            continue

        required = set(REQUIRED_KINDS)
        if "arm" in runner:
            required |= ARM_REQUIRED_KINDS

        for project in projects:
            base = f"tests/{project}/bin/Release/net10.0"
            if base not in carried:
                problems.append(
                    f"`{project}` is in the `{shard}` shard's project list but not in the "
                    f"`{artifact}` slice — the shard would fail with 'Missing …dll'")
            else:
                missing_kinds = required - carried[base]
                if missing_kinds:
                    problems.append(
                        f"the `{artifact}` slice does not carry {sorted(missing_kinds)} for "
                        f"`{project}` — the shard would fail with 'Missing …dll'")

    for artifact in CLI_CONSUMING_SLICES:
        carried = slices.get(artifact)
        if carried is None:
            problems.append(f"the `{artifact}` upload step is missing")
        elif CLI_OUTPUT_DIR not in carried:
            problems.append(
                f"the `{artifact}` slice does not carry `{CLI_OUTPUT_DIR}` — the CLI is "
                f"started from there by the e2e and PTY helpers")
        else:
            # The ui and e2e slices are consumed by an arm64 shard as well
            # (issue #1919), and the CLI is started from there by the PTY
            # helpers — so the CLI's own output directory must carry the
            # arm64 native assets too.
            missing_kinds = (REQUIRED_KINDS | ARM_REQUIRED_KINDS) - carried[CLI_OUTPUT_DIR]
            if missing_kinds:
                problems.append(
                    f"the `{artifact}` slice does not carry {sorted(missing_kinds)} for "
                    f"`{CLI_OUTPUT_DIR}` — the CLI would not start")

    if problems:
        print("::error::the build-output slices and the test shards disagree:")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    print(f"ok: {len(shards)} shards, {len(slices)} slices — every one of them carried")
    return 0


if __name__ == "__main__":
    sys.exit(main())
