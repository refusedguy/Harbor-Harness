#!/usr/bin/env python3
"""Gate: did a local squash-merge into dev drop content?

Stdlib only. No pip, no network, no dotnet build, no restore, no clone. It
reads the git history that is already in the object store and answers one
question per squash-merge commit.

WHY THIS FILE EXISTS

    A pull request that touches `.github/workflows/` cannot be merged through
    the GitHub API with this repository's token: the token has no `workflow`
    scope, so the API refuses the merge. Those pull requests were merged
    LOCALLY instead — `git merge --squash` onto `dev`, then
    `git push origin HEAD:dev` — with the conflicted files resolved by hand.

    The resolution kept the YAML valid and the build green, and it dropped
    lines. Measured on this repository's history, the conflicted file was
    replaced WHOLESALE by the pull request branch's version of it, and the
    pull request branch was older than `dev`. Every line `dev` had added to
    that file since the merge base was therefore gone. Seven files, all
    real, all still missing until this gate's pull request put them back:

      PR #1967  CHANGELOG.md                the "Proj0037/Proj0040" section
      PR #1967  .github/workflows/ci.yml    the absolute --results-directory
                                            (and the TRX path that goes with
                                            it: issue #618's whole mechanism)
      PR #1956  CHANGELOG.md                the "#1795" golden-frames section
      PR #1956  tools/README.md             the `test-skips.py` table row
      PR #1956  .github/workflows/ci.yml    the "Report skipped tests" and
                                            "zero-test guard can go red" steps
      PR #1956  .github/workflows/docs.yml  the "# Issue #1795" comment
      PR #1944  CHANGELOG.md                the "#1884" benchmark-baselines
                                            section
      PR #1944  .github/workflows/ci.yml    the "Report the test census" step
      PR #1969  docs/ANALYZERS.md           the "Public API freeze" section

    Nothing caught it. The files were valid, the workflow files parsed, the
    build passed, and a reader of `CHANGELOG.md` saw a plausible document.
    A gate that asks "is the section still there" cannot be written for every
    section of every file. This gate asks the question that generalises:
    **for every local squash-merge, is anything the merge dropped still
    missing from dev?**

WHAT THE GATE ANSWERS

    For every commit whose body carries

        Squash-merged into dev (PR #<n>, head <sha>)

    the gate takes the PULL REQUEST'S OWN diff — `merge-base..<sha>` — and
    compares it with dev:

      (a) every line the pull request ADDED must be present in dev NOW.

      (b) every line dev had in that file BEFORE the merge, that the pull
          request did not remove, must be present in dev NOW.

    A line the pull request removed is not a loss: the pull request asked for
    it. A file the pull request deleted is not a loss either, for the same
    reason. Lines that differ only in whitespace are the same line. A line
    repeated N times is ONE finding, not N. A line that a NORMAL commit (a
    pull request merged through the GitHub API, a direct commit) removed on
    purpose is not a loss either: somebody asked for it to go.

    Nothing here goes to the network. The pull request's head commit is
    named in the merge commit's own body, and the merge base is computed
    locally, so the whole history can be checked on every run.

    A pull request's head commit is not always reachable from a branch, so
    the gate needs it in the object store. `git fetch origin
    '+refs/pull/<n>/head:refs/remotes/pull/<n>/head'` puts it there; the CI
    job does that for every pull request the gate names. A head that is
    missing is an ERROR, not a silent skip: a gate that cannot see a merge
    is a gate that is blind to it.

WHAT COUNTS AS A FINDING

    A loss is reported when the dropped content is STILL MISSING from dev.
    A loss that a later commit put back is history, not a hole, and the gate
    says so instead of failing.

EXIT CODES

    0  no live loss
    1  a loss is still missing from dev (the findings are on stdout)
    2  usage error, a pull request head is missing, or the history could
       not be read

USAGE

    check-local-merge-loss.py [--range <rev>] [--tip <rev>] [--merge <sha>]
                              [--report-all] [--min-merges <n>]
                              [--allow-missing-heads] [--json] [--self-test]

    --range <rev>          tip of the DEV LINE to walk (default: origin/dev)
    --tip <rev>            the tree to check the lost content in (default:
                           HEAD — the tree CI is testing, so a pull request
                           that puts the content back turns the gate green)
    --merge <sha>          check only this merge commit (repeatable)
    --report-all           report historical losses even if the content is
                           back in the tree now
    --min-merges <n>       fail if fewer than <n> merges were examined
                           (default 1)
    --allow-missing-heads  warn instead of failing when a pull request's head
                           commit is not in the object store
    --json                 machine-readable output
    --self-test            prove the checker still fires (synthetic history)
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from dataclasses import dataclass, field

# The marker the local squash-merges carry in their body. The pull request
# number and the pull request's head commit are both in it, which is what
# lets this gate work without the network.
MARKER = re.compile(
    r"Squash-merged into dev \(PR #(?P<pr>\d+), head (?P<head>[0-9a-f]{7,40})\)"
)


# --------------------------------------------------------------------------
# git plumbing
# --------------------------------------------------------------------------


def git_env() -> dict[str, str]:
    """Environment for our own `git` calls: never prompt, never inherit a
    pager, and never take a lock the checkout does not want."""
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    env["GIT_PAGER"] = "cat"
    env["GIT_OPTIONAL_LOCKS"] = "0"
    return env


def git(*args: str, cwd: str | None = None) -> tuple[int, str, str]:
    """Run `git` and return (exit code, stdout, stderr)."""
    proc = subprocess.run(
        ["git", *args],
        capture_output=True,
        text=True,
        errors="replace",
        env=git_env(),
        cwd=cwd,
        check=False,
    )
    return proc.returncode, proc.stdout, proc.stderr


def git_text(*args: str, cwd: str | None = None) -> str | None:
    """Run `git` and return stdout, or None when git failed."""
    code, out, _ = git(*args, cwd=cwd)
    return out if code == 0 else None


def git_ok(*args: str, cwd: str | None = None) -> bool:
    code, _, _ = git(*args, cwd=cwd)
    return code == 0


def object_exists(sha: str, cwd: str | None = None) -> bool:
    return git_ok("cat-file", "-e", f"{sha}^{{commit}}", cwd=cwd)


def file_lines(rev: str, path: str, cwd: str | None = None) -> list[str] | None:
    """The lines of `path` at `rev`, or None when the file is not there.

    A file that does not exist at that revision is not an error: a pull
    request that adds a file has no "before" version of it."""
    code, out, _ = git("show", f"{rev}:{path}", cwd=cwd)
    if code != 0:
        return None
    return out.splitlines()


# --------------------------------------------------------------------------
# the comparison
# --------------------------------------------------------------------------


def normalize(line: str) -> str:
    """Two lines that differ only in whitespace are the same line.

    The merge that lost content rewrote whole files, and a re-indent is not
    a loss. Collapsing the whitespace keeps the check on the content."""
    return " ".join(line.split())


@dataclass
class Loss:
    """One piece of content that a squash-merge dropped."""

    pr: int
    merge: str
    merge_subject: str
    path: str
    line: str
    side: str  # "pull-request" (a) or "dev" (b)
    restored: bool = False  # back in dev now


@dataclass
class MergeReport:
    """What one squash-merge cost."""

    merge: str
    pr: int
    subject: str
    head: str
    base: str | None = None
    files: int = 0
    losses: list[Loss] = field(default_factory=list)
    skipped: str | None = None


class Caches:
    """The per-file lookups the checker needs, computed once per run.

    The tip's version of a file and the lines a normal commit removed from
    it are the same for every merge, so they are computed once and shared."""

    def __init__(self, tip: str, squash_merges: set[str], cwd: str | None = None) -> None:
        self.tip = tip
        self.squash_merges = squash_merges
        self.cwd = cwd
        self._tip: dict[str, Counter[str] | None] = {}
        self._deliberate: dict[str, set[str]] = {}

    def tip_lines(self, path: str) -> Counter[str] | None:
        if path not in self._tip:
            lines = file_lines(self.tip, path, cwd=self.cwd)
            self._tip[path] = (
                None if lines is None else Counter(normalize(line) for line in lines)
            )
        return self._tip[path]

    def deliberate(self, path: str) -> set[str]:
        if path not in self._deliberate:
            self._deliberate[path] = deliberate_removals(
                path, self.tip, self.squash_merges, cwd=self.cwd
            )
        return self._deliberate[path]


def diff_added_removed(base: str, head: str, path: str, cwd: str | None = None) -> tuple[list[str], list[str]]:
    """The lines the pull request added and removed for one file.

    The diff is the pull request's own (`base..head`), never the diff of the
    merge commit: the merge commit's diff is against `dev` and therefore says
    nothing about what the pull request intended."""
    code, out, _ = git("diff", "--unified=0", "--no-color", base, head, "--", path, cwd=cwd)
    if code != 0:
        return [], []
    added: list[str] = []
    removed: list[str] = []
    for raw in out.splitlines():
        if raw.startswith("+++") or raw.startswith("---"):
            continue
        if raw.startswith("+"):
            added.append(raw[1:])
        elif raw.startswith("-"):
            removed.append(raw[1:])
    return added, removed


def merge_base(head: str, parent: str, cwd: str | None = None) -> str | None:
    out = git_text("merge-base", head, parent, cwd=cwd)
    return out.strip() if out and out.strip() else None


def deliberate_removals(
    path: str,
    tip: str,
    squash_merges: set[str],
    cwd: str | None = None,
) -> set[str]:
    """The lines that NORMAL commits removed from `path`.

    A normal commit is one that did not come through this repository's
    local squash-merge procedure: a pull request merged through the GitHub
    API, or a direct commit. When such a commit removes a line, somebody
    asked for it to go, and the gate must not call that a loss.

    The local squash-merges are excluded on purpose: their removals are
    exactly what this gate is looking for."""
    out = git_text(
        "log",
        "--first-parent",
        "-p",
        "--format=@@COMMIT %H",
        tip,
        "--",
        path,
        cwd=cwd,
    )
    if out is None:
        return set()
    removed: set[str] = set()
    commit = ""
    for raw in out.splitlines():
        if raw.startswith("@@COMMIT "):
            commit = raw[len("@@COMMIT ") :].strip()
            continue
        if commit and commit in squash_merges:
            continue
        if raw.startswith("---") or raw.startswith("+++"):
            continue
        if raw.startswith("-"):
            removed.add(normalize(raw[1:]))
    return removed


def load_exceptions(path: str) -> list[tuple[str, int, re.Pattern[str], str]]:
    """The accepted losses, read from the exceptions file.

    An exception is a piece of content a squash-merge dropped that is NOT a
    live loss: it was restored in another form, or an equivalent form was
    restored instead. The file names the pull request, the file and a regex
    that picks the accepted line out of the losses the gate reports."""
    if not os.path.exists(path):
        return []
    out: list[tuple[str, int, re.Pattern[str], str]] = []
    with open(path, encoding="utf-8") as handle:
        for number, raw in enumerate(handle, start=1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            fields = [field.strip() for field in line.split("|")]
            if len(fields) != 4 or not all(fields):
                print(
                    f"error: {path}:{number}: an exception needs four fields "
                    "(path | PR | regex | reason)",
                    file=sys.stderr,
                )
                raise ValueError(path)
            try:
                pattern = re.compile(fields[2])
            except re.error as exc:
                print(f"error: {path}:{number}: bad regex: {exc}", file=sys.stderr)
                raise ValueError(path) from exc
            out.append((fields[0], int(fields[1].removeprefix("#")), pattern, fields[3]))
    return out


def check_merge(
    merge: str,
    subject: str,
    pr: int,
    head: str,
    tip: str,
    squash_merges: set[str],
    report_all: bool,
    cwd: str | None = None,
    cache: "Caches | None" = None,
) -> MergeReport:
    """Compare one squash-merge against the pull request it squashed."""
    parent = (git_text("rev-parse", f"{merge}^", cwd=cwd) or "").strip()
    report = MergeReport(merge=merge, pr=pr, subject=subject, head=head)

    if not parent:
        report.skipped = "the merge commit has no parent to compare against"
        return report
    if not object_exists(head, cwd=cwd):
        report.skipped = (
            f"the pull request head {head[:12]} is not in the local object store"
        )
        return report

    base = merge_base(head, parent, cwd=cwd)
    if base is None:
        report.skipped = f"no merge base between the pull request head and {parent[:12]}"
        return report
    report.base = base

    names = git_text("diff", "--name-status", "--no-renames", base, head, cwd=cwd)
    if names is None:
        report.skipped = "the pull request diff could not be read"
        return report

    paths: list[str] = []
    for raw in names.splitlines():
        if not raw.strip():
            continue
        parts = raw.split("\t")
        if len(parts) < 2:
            continue
        status, path = parts[0], parts[-1]
        if status.startswith("D"):
            # The pull request deleted the file. That is the pull request's
            # decision, not a loss.
            continue
        paths.append(path)

    cache = cache or Caches(tip=tip, squash_merges=squash_merges, cwd=cwd)

    for path in paths:
        report.files += 1
        ours = file_lines(parent, path, cwd=cwd) or []
        merged = file_lines(merge, path, cwd=cwd) or []
        added, removed = diff_added_removed(base, head, path, cwd=cwd)

        merged_norm = Counter(normalize(line) for line in merged)
        removed_norm = {normalize(line) for line in removed}

        def record(line: str, side: str) -> None:
            key = normalize(line)
            if not key:
                return
            tip_norm = cache.tip_lines(path)
            restored = tip_norm is not None and tip_norm.get(key, 0) >= 1
            if not restored and key in cache.deliberate(path):
                # A normal commit removed this line on purpose. The file may
                # even be gone from the tree now — a commit that deleted the
                # file removed every line in it.
                restored = True
            report.losses.append(
                Loss(
                    pr=pr,
                    merge=merge,
                    merge_subject=subject,
                    path=path,
                    line=line,
                    side=side,
                    restored=restored,
                )
            )

        # (a) every line the pull request added must be in the merge result.
        #     A resolution that dropped the pull request's own work is a loss
        #     too, and this is the half that catches it.
        seen: set[str] = set()
        for line in added:
            key = normalize(line)
            if not key or key in seen:
                continue
            seen.add(key)
            if merged_norm.get(key, 0) >= 1:
                continue
            record(line, "pull-request")

        # (b) every line dev had before the merge that the pull request did
        #     not remove must be in the merge result. This is the half that
        #     catches the wholesale file takeover.
        seen = set()
        for line in ours:
            key = normalize(line)
            if not key or key in seen:
                continue
            seen.add(key)
            if key in removed_norm:
                # The pull request removed this line on purpose.
                continue
            if merged_norm.get(key, 0) >= 1:
                continue
            record(line, "dev")

    if report_all:
        for loss in report.losses:
            loss.restored = False
    return report


# --------------------------------------------------------------------------
# history walk
# --------------------------------------------------------------------------


def find_merges(tip: str, cwd: str | None = None) -> list[tuple[str, str, int, str]]:
    """Every commit on the first-parent line whose body carries the marker.

    Returns (sha, subject, pr number, pull request head sha)."""
    out = git_text("log", "--first-parent", "--format=%H%x1f%s%x1f%B%x1e", tip, cwd=cwd)
    if out is None:
        return []
    found: list[tuple[str, str, int, str]] = []
    for record in out.split("\x1e"):
        record = record.strip("\n")
        if not record:
            continue
        parts = record.split("\x1f")
        if len(parts) < 3:
            continue
        sha, subject, body = parts[0], parts[1], parts[2]
        match = MARKER.search(body)
        if not match:
            continue
        found.append((sha, subject, int(match.group("pr")), match.group("head")))
    return found


# --------------------------------------------------------------------------
# self-test
# --------------------------------------------------------------------------


def _fixture(root: str, keep_dev_line: bool) -> dict[str, str]:
    """A throwaway repository with one squash-merge.

    The history is the merge base, a pull request branch that adds a line, a
    `main` that adds a line of its own, and the squash-merge that lands the
    pull request on `main`. With `keep_dev_line=False` the merge takes the
    pull request's version of the file and drops `main`'s line — the failure
    this gate exists to catch. With `keep_dev_line=True` it keeps it."""
    os.makedirs(root, exist_ok=True)
    git("init", "-q", "-b", "main", root, cwd=root)
    git("config", "user.name", "selftest", cwd=root)
    git("config", "user.email", "selftest@example.invalid", cwd=root)
    git("config", "commit.gpgsign", "false", cwd=root)

    def write(text: str) -> None:
        with open(os.path.join(root, "doc.md"), "w", encoding="utf-8") as handle:
            handle.write(text)

    write("# doc\n\nbase line\n")
    git("add", "doc.md", cwd=root)
    git("commit", "-q", "-m", "base", cwd=root)
    base = (git_text("rev-parse", "HEAD", cwd=root) or "").strip()

    git("checkout", "-q", "-b", "pr", cwd=root)
    write("# doc\n\nbase line\npull request line\n")
    git("commit", "-q", "-am", "pull request work", cwd=root)
    pr_head = (git_text("rev-parse", "HEAD", cwd=root) or "").strip()

    git("checkout", "-q", "main", cwd=root)
    write("# doc\n\nbase line\ndev line\n")
    git("commit", "-q", "-am", "dev work", cwd=root)

    if keep_dev_line:
        write("# doc\n\nbase line\npull request line\ndev line\n")
    else:
        write("# doc\n\nbase line\npull request line\n")
    git(
        "commit",
        "-q",
        "-am",
        "squash\n\nSquash-merged into dev (PR #4242, head %s)." % pr_head,
        cwd=root,
    )
    merge = (git_text("rev-parse", "HEAD", cwd=root) or "").strip()
    return {"root": root, "base": base, "pr_head": pr_head, "merge": merge}


def self_test() -> int:
    """Prove the checker fires on a merge that dropped a line, and stays quiet
    on a merge that did not."""
    failures: list[str] = []
    tmp = tempfile.mkdtemp(prefix="harbor-merge-loss-selftest-")
    try:
        broken = _fixture(os.path.join(tmp, "broken"), keep_dev_line=False)
        root = broken["root"]
        merges = find_merges(root, cwd=root)
        if len(merges) != 1:
            failures.append(f"the marker scan found {len(merges)} merges, expected 1")
        else:
            sha, subject, pr, head = merges[0]
            report = check_merge(sha, subject, pr, head, sha, set(), True, cwd=root)
            lost = [loss for loss in report.losses if normalize(loss.line) == "dev line"]
            if not lost:
                failures.append(
                    "the checker did not report the line the merge dropped "
                    f"(merge={sha[:12]}, findings={len(report.losses)})"
                )
            if not any(loss.pr == 4242 for loss in report.losses):
                failures.append("the checker did not name the pull request")

        clean = _fixture(os.path.join(tmp, "clean"), keep_dev_line=True)
        clean_root = clean["root"]
        sha, subject, pr, head = find_merges(clean_root, cwd=clean_root)[0]
        report = check_merge(sha, subject, pr, head, sha, set(), True, cwd=clean_root)
        if report.losses:
            failures.append(
                "the checker reported a loss on a merge that kept every line: "
                + "; ".join(f"{loss.path}: {loss.line!r}" for loss in report.losses)
            )

        # A line a normal commit removed on purpose is not a loss: the merge
        # kept it, and a later ordinary commit took it away on purpose.
        deliberate = _fixture(os.path.join(tmp, "deliberate"), keep_dev_line=True)
        deliberate_root = deliberate["root"]
        with open(os.path.join(deliberate_root, "doc.md"), "w", encoding="utf-8") as handle:
            handle.write("# doc\n\nbase line\npull request line\n")
        git("commit", "-q", "-am", "an ordinary commit removes the dev line", cwd=deliberate_root)
        sha, subject, pr, head = find_merges(deliberate_root, cwd=deliberate_root)[0]
        report = check_merge(
            sha, subject, pr, head, sha, {sha}, True, cwd=deliberate_root
        )
        if report.losses:
            failures.append(
                "the checker reported a line that an ordinary commit removed on purpose: "
                + "; ".join(f"{loss.path}: {loss.line!r}" for loss in report.losses)
            )
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if failures:
        for failure in failures:
            print(f"self-test FAILED: {failure}", file=sys.stderr)
        return 1
    print(
        "self-test OK: the checker reports a dropped line, stays quiet on a "
        "clean merge, and ignores a line a normal commit removed on purpose"
    )
    return 0


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="check-local-merge-loss.py",
        description="Prove that the local squash-merges into dev dropped no content.",
    )
    parser.add_argument(
        "--range",
        dest="range_",
        default="origin/dev",
        help="tip of the DEV LINE to walk (default: origin/dev)",
    )
    parser.add_argument(
        "--tip",
        dest="tip",
        default="HEAD",
        help="the tree to check for the lost content (default: HEAD)",
    )
    parser.add_argument(
        "--merge",
        dest="merges",
        action="append",
        default=[],
        help="check only this merge commit (repeatable)",
    )
    parser.add_argument(
        "--report-all",
        action="store_true",
        help="report historical losses even if the content is back in dev now",
    )
    parser.add_argument(
        "--min-merges", type=int, default=1, help="fail if fewer than N merges were examined"
    )
    parser.add_argument(
        "--allow-missing-heads",
        action="store_true",
        help="warn instead of failing when a pull request head is not in the object store",
    )
    parser.add_argument(
        "--exceptions",
        default=os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "merge-loss-exceptions.txt"
        ),
        help="the file naming the accepted losses",
    )
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    parser.add_argument("--self-test", action="store_true", help="prove the checker still fires")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.self_test:
        return self_test()

    tip = args.tip
    if not git_ok("rev-parse", "--verify", f"{tip}^{{commit}}"):
        print(f"error: {tip} is not a commit", file=sys.stderr)
        return 2
    if not git_ok("rev-parse", "--verify", f"{args.range_}^{{commit}}"):
        print(f"error: {args.range_} is not a commit", file=sys.stderr)
        return 2

    all_merges = find_merges(args.range_)
    merges = all_merges
    if args.merges:
        wanted = args.merges
        merges = [
            item for item in merges if any(item[0].startswith(w) for w in wanted)
        ]
    if len(merges) < args.min_merges:
        print(
            f"error: only {len(merges)} squash-merge(s) examined, the floor is {args.min_merges}",
            file=sys.stderr,
        )
        return 2

    # Every local squash-merge in the history, not only the ones under
    # report: a commit that is one of them has its removals EXCLUDED from
    # the "somebody removed this on purpose" set, and that set must not
    # change when the report is narrowed to one merge.
    squash_merges = {item[0] for item in all_merges}
    cache = Caches(tip=tip, squash_merges=squash_merges)
    reports: list[MergeReport] = []
    for sha, subject, pr, head in merges:
        reports.append(
            check_merge(
                sha, subject, pr, head, tip, squash_merges, args.report_all, cache=cache
            )
        )

    live: list[Loss] = []
    for report in reports:
        live.extend(loss for loss in report.losses if not loss.restored)

    exceptions = load_exceptions(args.exceptions)
    accepted: list[Loss] = []
    still_live: list[Loss] = []
    used: set[int] = set()
    for loss in live:
        key = normalize(loss.line)
        hit = None
        for index, (path, pr, pattern, _reason) in enumerate(exceptions):
            if path != loss.path or pr != loss.pr:
                continue
            if pattern.search(key):
                hit = index
                break
        if hit is None:
            still_live.append(loss)
        else:
            used.add(hit)
            accepted.append(loss)
    stale = [
        (index, exception)
        for index, exception in enumerate(exceptions)
        if index not in used
    ]

    missing = [report for report in reports if report.skipped]
    if missing and not args.allow_missing_heads:
        for report in missing:
            print(
                f"error: PR #{report.pr}: {report.skipped}\n"
                f"    fetch it with: git fetch origin '+refs/pull/{report.pr}/head:refs/remotes/pull/{report.pr}/head'",
                file=sys.stderr,
            )
        return 2

    if args.json:
        payload = {
            "tip": tip,
            "merges_examined": len(reports),
            "files_examined": sum(report.files for report in reports),
            "skipped": [
                {"merge": report.merge, "pr": report.pr, "reason": report.skipped}
                for report in reports
                if report.skipped
            ],
            "losses": [
                {
                    "pr": loss.pr,
                    "merge": loss.merge,
                    "path": loss.path,
                    "line": loss.line,
                    "side": loss.side,
                    "restored": loss.restored,
                }
                for loss in still_live
            ],
            "accepted_losses": len(accepted),
            "stale_exceptions": [
                {"path": e[0], "pr": e[1], "regex": e[2].pattern, "reason": e[3]}
                for _index, e in stale
            ],
            "historical_losses": sum(len(report.losses) for report in reports),
        }
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        for report in missing:
            print(f"skipped  {report.merge[:12]}  PR #{report.pr}: {report.skipped}")
        for loss in still_live:
            print(
                f"PR #{loss.pr}  {loss.merge[:12]}  {loss.path}: "
                f"lost {loss.side} line: {loss.line.strip()[:200]}"
            )
        print(
            f"merges examined: {len(reports)}   files examined: "
            f"{sum(report.files for report in reports)}   "
            f"live losses: {len(still_live)}   "
            f"accepted: {len(accepted)}   "
            f"historical losses: {sum(len(report.losses) for report in reports)}"
        )
        for _index, exception in stale:
            if args.merges:
                # The report was narrowed: an exception that matches nothing
                # in one merge says nothing about the whole history.
                continue
            print(
                f"note: the exception for PR #{exception[1]} {exception[0]} "
                f"({exception[2].pattern}) matches nothing — the content is back, "
                "so the exception can go"
            )

    return 1 if still_live else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
