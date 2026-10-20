#!/usr/bin/env python3
"""Make the source mtimes tell the truth, so MSBuild can skip what did not change.

Stdlib only — no pip, no network, no dotnet build. Wired into the `build`
job of .github/workflows/ci.yml (issue #1927), right after the build-output
cache is restored.

WHY THIS FILE EXISTS

  Git does not store mtimes. `actions/checkout` therefore writes every
  source file with the CURRENT clock, and MSBuild's `CoreCompile` skips a
  project only when its output is newer than every input. A restored
  output is older than "now", so a checkout that keeps the checkout
  clock makes ALL 104 projects look stale and MSBuild recompiles the
  whole solution — the cache buys nothing.

  Setting every file to one old timestamp is not the fix either: MSBuild
  would then also skip the files the pull request actually changed, and
  the build would silently produce the OLD binaries. That is a stale
  build, which is worse than a slow one.

WHAT IT DOES

  The build-output cache is keyed by the commit it was built from, so the
  restored cache knows its base commit (the workflow reads it out of the
  cache key it matched). For a tree checked out at HEAD:

    * a file whose CONTENT differs from the cached base gets the current
      time — MSBuild rebuilds exactly it, and through the project graph
      its dependents;
    * a file whose content is IDENTICAL to the cached base gets the base
      commit's time, which is older than the restored outputs — MSBuild
      skips it.

  The first rule keeps the build honest, the second rule is what makes the
  cache pay off.

EXIT CODES

  0  the mtimes are normalised (or there was nothing to normalise)
  1  something went wrong that is not the cache's fault
  3  the cached base could not be resolved, so the changed set is unknown.
     The caller MUST discard the restored outputs and rebuild from
     scratch: a cache whose base is unknown cannot be told apart from a
     stale one, and a stale build is worse than a slow build.
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
import tempfile
import time

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_UNTRUSTED_BASE = 3


def _git(repo: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ("git", *args),
        cwd=repo,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )


def _tracked_files(repo: str) -> list[str]:
    """The tracked paths, minus the gitlinks (submodules are not files)."""
    proc = _git(repo, "ls-files", "-s", "-z")
    if proc.returncode != 0:
        raise RuntimeError(f"git ls-files failed: {proc.stderr.strip()}")
    paths: list[str] = []
    for entry in proc.stdout.split("\0"):
        if not entry:
            continue
        # "<mode> <sha> <stage>\t<path>"
        meta, _, path = entry.partition("\t")
        if not path:
            continue
        mode = meta.split(" ", 1)[0]
        if mode == "160000":  # a gitlink: a submodule, not a file
            continue
        paths.append(path)
    return paths


def _stamp_outputs(repo: str, when: float) -> int:
    """Stamp every file under an obj/ or bin/ directory with `when`.

    The restored outputs carry the timestamps they had when the cache was
    built. MSBuild compares an output against its other inputs, and the
    analyzer assemblies in the NuGet folder are among them: a restored
    NuGet folder is extracted at the start of the run, so the analyzers are
    NEWER than the outputs the cache carries, and every project looks stale
    again. Stamping the outputs with the current time puts them back on top
    of those inputs.

    Returns the number of files stamped.
    """
    stamped = 0
    for dirpath, dirnames, _ in os.walk(repo):
        outputs = [d for d in dirnames if d in ("obj", "bin")]
        # Do not walk into the output trees twice.
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin") and d != ".git"]
        for name in outputs:
            for root, _subdirs, files in os.walk(os.path.join(dirpath, name)):
                for entry in files:
                    try:
                        os.utime(
                            os.path.join(root, entry), (when, when), follow_symlinks=False
                        )
                        stamped += 1
                    except (NotImplementedError, OSError):
                        pass
    return stamped


def normalise(repo: str, base: str, now: float, dry_run: bool) -> tuple[int, str]:
    """Set every tracked file's mtime. Returns (exit code, report)."""
    resolved = _git(repo, "rev-parse", "--verify", "--quiet", f"{base}^{{commit}}")
    if resolved.returncode != 0 or not resolved.stdout.strip():
        # The commit the cache was built from is not in this clone — the branch
        # that produced it was deleted, or the cache outlived it. GitHub serves
        # a commit by SHA, so one shallow fetch brings back the only thing
        # that is needed: the TREE.
        fetched = _git(repo, "fetch", "--no-tags", "--depth=1", "origin", base)
        resolved = _git(repo, "rev-parse", "--verify", "--quiet", f"{base}^{{commit}}")
        if resolved.returncode != 0 or not resolved.stdout.strip():
            return EXIT_UNTRUSTED_BASE, (
                f"the cached base {base} is not a commit in this clone "
                f"({fetched.stderr.strip() or 'fetch failed'}) — the changed set "
                "is unknown, so the restored outputs cannot be trusted"
            )

    stamp = _git(repo, "log", "-1", "--format=%ct", base)
    if stamp.returncode != 0 or not stamp.stdout.strip():
        return EXIT_UNTRUSTED_BASE, f"cannot read the committer time of {base}"
    base_time = int(stamp.stdout.strip())

    diff = _git(repo, "diff", "--name-only", "-z", "--no-renames", base, "HEAD")
    if diff.returncode != 0:
        return EXIT_UNTRUSTED_BASE, (
            f"git diff {base} HEAD failed: {diff.stderr.strip() or 'no output'}"
        )
    changed = {path for path in diff.stdout.split("\0") if path}

    touched_now = 0
    touched_base = 0
    missing = 0

    # The outputs first, and the changed sources a second later than them:
    #
    #   * the outputs are stamped NOW, so they are newer than every other
    #     input MSBuild compares them against (the analyzer assemblies in the
    #     NuGet folder above all) and the unchanged projects are skipped;
    #   * a CHANGED source is stamped one second after the outputs, so it is
    #     newer than the output that was built from the old version of it —
    #     otherwise MSBuild would skip it and the build would be stale;
    #   * an UNCHANGED source is stamped with the base commit's time, which
    #     is older than the outputs, so MSBuild skips it.
    outputs_stamped = 0
    if not dry_run:
        outputs_stamped = _stamp_outputs(repo, now)

    for path in _tracked_files(repo):
        full = os.path.join(repo, path)
        if not os.path.lexists(full):
            missing += 1
            continue
        stamp_for_file = (now + 1.0) if path in changed else base_time
        if not dry_run:
            try:
                os.utime(full, (stamp_for_file, stamp_for_file), follow_symlinks=False)
            except (NotImplementedError, OSError):
                os.utime(full, (stamp_for_file, stamp_for_file))
        if path in changed:
            touched_now += 1
        else:
            touched_base += 1

    report = (
        f"base={base} base_time={base_time} changed={len(changed)} "
        f"rebuilt={touched_now} skipped={touched_base} missing={missing} "
        f"outputs_stamped={outputs_stamped} now={int(now)}"
    )
    return EXIT_OK, report


def self_test() -> int:
    """Prove the two rules on a synthetic repository."""
    with tempfile.TemporaryDirectory(prefix="harbor-mtimes-") as tmp:
        env = dict(os.environ, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null")
        subprocess.run(["git", "init", "-q", tmp], check=True, env=env)
        subprocess.run(["git", "-C", tmp, "config", "user.email", "t@example.com"], check=True)
        subprocess.run(["git", "-C", tmp, "config", "user.name", "T"], check=True)

        unchanged = os.path.join(tmp, "unchanged.cs")
        changed = os.path.join(tmp, "changed.cs")
        with open(unchanged, "w", encoding="utf-8") as fh:
            fh.write("// unchanged\n")
        with open(changed, "w", encoding="utf-8") as fh:
            fh.write("// v1\n")
        subprocess.run(["git", "-C", tmp, "add", "-A"], check=True)
        subprocess.run(["git", "-C", tmp, "commit", "-qm", "base"], check=True)
        base = subprocess.run(
            ["git", "-C", tmp, "rev-parse", "HEAD"], check=True, capture_output=True, text=True
        ).stdout.strip()

        with open(changed, "w", encoding="utf-8") as fh:
            fh.write("// v2\n")
        subprocess.run(["git", "-C", tmp, "commit", "-qam", "change"], check=True)

        # A restored output: a file under an obj/ directory.
        os.makedirs(os.path.join(tmp, "obj", "Release", "net10.0"), exist_ok=True)
        output = os.path.join(tmp, "obj", "Release", "net10.0", "Thing.dll")
        with open(output, "w", encoding="utf-8") as fh:
            fh.write("binary")

        base_time = int(
            subprocess.run(
                ["git", "-C", tmp, "log", "-1", "--format=%ct", base],
                check=True, capture_output=True, text=True,
            ).stdout.strip()
        )
        # "now" stands in for the moment of the checkout, which is later than
        # the commit the cached outputs were built from.
        now = float(base_time + 1000)
        code, report = normalise(tmp, base, now, dry_run=False)
        if code != EXIT_OK:
            print(f"self-test: normalise failed: {report}", file=sys.stderr)
            return 1

        got_changed = os.stat(changed).st_mtime
        got_unchanged = os.stat(unchanged).st_mtime
        got_output = os.stat(output).st_mtime
        if int(got_changed) != int(now) + 1:
            print(
                f"self-test: the changed file has mtime {int(got_changed)}, "
                f"expected {int(now) + 1} (one second after the outputs)",
                file=sys.stderr,
            )
            return 1
        if int(got_unchanged) != base_time:
            print(
                f"self-test: the unchanged file has mtime {int(got_unchanged)}, "
                f"expected the base commit's time {base_time}",
                file=sys.stderr,
            )
            return 1
        if int(got_output) != int(now):
            print(
                f"self-test: the restored output has mtime {int(got_output)}, "
                f"expected {int(now)}",
                file=sys.stderr,
            )
            return 1
        if got_unchanged >= got_output:
            print("self-test: the unchanged source is not older than the output", file=sys.stderr)
            return 1
        if got_output >= got_changed:
            print("self-test: the output is not older than the changed source", file=sys.stderr)
            return 1

        # An unknown base must NOT be silently accepted.
        code, _ = normalise(tmp, "0" * 40, now, dry_run=True)
        if code != EXIT_UNTRUSTED_BASE:
            print(f"self-test: an unknown base returned {code}, expected {EXIT_UNTRUSTED_BASE}", file=sys.stderr)
            return 1

    print("self-test: ok")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=".", help="the repository root (default: the cwd)")
    parser.add_argument("--base", required=False, help="the commit the cached outputs were built from")
    parser.add_argument("--now", type=float, default=None, help="the timestamp to give the changed files")
    parser.add_argument("--dry-run", action="store_true", help="report the plan without touching anything")
    parser.add_argument("--self-test", action="store_true", help="prove the two rules on a synthetic repository")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    if not args.base:
        print("normalize-mtimes: --base is required", file=sys.stderr)
        return EXIT_ERROR

    repo = os.path.abspath(args.repo)
    now = args.now if args.now is not None else time.time()
    try:
        code, report = normalise(repo, args.base, now, dry_run=args.dry_run)
    except Exception as exc:  # noqa: BLE001 - the report is the point
        print(f"normalize-mtimes: {exc}", file=sys.stderr)
        return EXIT_ERROR

    print(f"[normalize-mtimes] {report}")
    if code == EXIT_UNTRUSTED_BASE:
        print(f"normalize-mtimes: {report}", file=sys.stderr)
        print(
            "normalize-mtimes: the restored outputs cannot be trusted — the caller must "
            "discard them and rebuild from scratch",
            file=sys.stderr,
        )
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
