#!/usr/bin/env python3
"""Second duplication detector: jscpd 5.4.1 (MIT, self-contained Rust binary).

The repository already has a duplication gate — `tools/check-test-dup.py`
(#1778) — and this file does NOT replace it. It asks a different question
with a different engine, and the two answers do not agree, which is the
whole reason this file exists:

  check-test-dup.py   a duplicated TEST METHOD BODY, matched on the shape of
                      the body (literals -> S, identifiers -> I) plus a shared
                      identifier vocabulary. Scope: `tests/**/*.cs`.
  THIS ONE            a duplicated BLOCK OF CODE, matched on the token stream
                      of a window. Scope: the whole maintained C# surface
                      (`src/`, `apps/`, `tests/`), and it sees duplication
                      that lives OUTSIDE a method body: a duplicated record
                      declaration, a duplicated nested class, a duplicated
                      file header, a duplicated interface implementation.

MEASURED, not assumed (the numbers are from `origin/dev`; see
docs/DEVELOPMENT.md §"The second duplication detector"):

  tool            files   clones   duplicated lines   wall
  check-test-dup  1017      206              2987    14.0 s
  jscpd (this)    1971      286              8353     1.2 s

  Overlap, measured line by line on the test tree: the python gate's 6548
  duplicated lines and jscpd's overlap on 1965 of them (30.0% of the
  python gate's, 23.3% of jscpd's). 37 of the python gate's 206 groups sit
  entirely inside a jscpd clone, 25 partly, and 144 not at all. The two
  detectors are complements, not substitutes: jscpd flags 94 files with
  duplication that the python gate does not, and the python gate flags 113
  files that jscpd does not.

WHY THE BASELINE IS A TOTAL AND NOT A LIST OF CLONES

  jscpd ships `--baseline` and `--fail-on-new-clones`, and issue #1847
  proposed exactly that as the gate. It was measured on 13 real commits of
  `dev` and it does NOT work on this tree:

    * The baseline is keyed on a CONTENT HASH of each clone. Editing a
      duplicated body changes the hash, so the edit is reported as NEW
      duplication. Commit 8bde0944 changed one line in each of two files
      that already duplicated each other, and jscpd reported 4 new clones —
      3 of them pairs that were already clones BEFORE the commit.
    * Across those 13 commits the gate was red on 1 commit with the exact
      configuration and on 3 with the renamed one, and every one of those
      commits was ordinary work that added no duplication.

  The python gate was written to avoid exactly this: it compares the
  MEMBERS of a group (paths and names), never the text of the bodies,
  because "the text changes every time somebody edits a duplicated test,
  and an edit is not new duplication" (its own header, #1778).

  So the rule here is the aggregate one, and it is the rule the python gate
  also has (DUP-GROWTH): the tree's duplicated LINES must not exceed the
  number the committed baseline records. Editing a duplicated body does not
  change that total; adding a copy of it does. The aggregate also survives
  the removal programme's moves, which is what made the python gate's
  membership rule go red on #1814.

THE RULES

  DUP-FLOOR           The scan analysed fewer files than the floor. A gate
                      that measured nothing must not be green: the floor is
                      the non-vacuity rule.
  DUP-GROWTH          The tree's duplicated lines are above the baseline's.
                      The baseline is a reviewed snapshot: a change that
                      legitimately grows duplication updates it in the same
                      diff, where a reviewer sees it, and the floor only
                      ever goes down.
  DUP-BASELINE-STALE  The tree's duplicated lines are BELOW the baseline's:
                      the tree got cleaner, and the baseline should be
                      lowered with `--update` when the programme lands. A
                      warning, never a failure.
  DUP-MEASUREMENT     The config or the tool version moved since the
                      baseline was written, so the two numbers are not
                      comparable. Re-measure with `--update`.
  DUP-NEW-CLONES      The number of clones that are new relative to the base
                      ref. Reported; blocking only with `--fail-on-new-clones`,
                      because the number is edit-sensitive (see above).

WHAT THE TOOL DOES AND DOES NOT NEED

  Stdlib Python 3, no pip, no dotnet. It fetches ONE pinned binary (jscpd
  5.4.1, MIT, sha256-pinned per platform) into `tmp/jscpd/`, runs it with
  the repository's `.jscpd.json`, and reads the JSON report. The binary is
  a build-time tool: it never reaches the product, and no `PackageReference`
  is added, so the dependency gate does not see it.

  `--self-test` proves the failure path on fixtures, the way the other
  gates do: a report that measured nothing must fail, a report that grew
  must fail, a clean report must pass, and a corrupted download must be
  refused before the tool is run.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

TOOL_NAME = "jscpd"
TOOL_VERSION = "5.4.1"
TOOL_LICENSE = "MIT"
TOOL_RELEASES = "https://github.com/kucherenko/jscpd/releases/download"

# One pinned asset per platform we support, with the sha256 from the
# release's own checksums.txt. The tool is MIT-licensed; the binary is
# fetched, verified and thrown away with the CI job.
TOOL_ASSETS: dict[str, tuple[str, str]] = {
    "linux-x64": (
        "jscpd-linux-x64-gnu.tar.gz",
        "f2017a17de2a8d5708f8354c6e7223f82da3dc6489675bce543ba00d9d529468",
    ),
    "linux-arm64": (
        "jscpd-linux-arm64-gnu.tar.gz",
        "6f8ddab0aec0232d08a6aa0c36cddb435ddde2c24a447cc9afe603ac8556748c",
    ),
    "darwin-x64": (
        "jscpd-darwin-x64.tar.gz",
        "101a9b9e0d1a4ffea7b9ac86d91003745ef471f00a8a2a921353a65647b5d12f",
    ),
    "darwin-arm64": (
        "jscpd-darwin-arm64.tar.gz",
        "48dedc0b8fa4f2900fc6ac8f06797e8eee7e36a8ab4f67f54b1ad25fd7ae103c",
    ),
}

CONFIG_NAME = ".jscpd.json"
BASELINE_NAME = "tools/jscpd-baseline.json"
REPORT_NAME = "jscpd-report.json"
CACHE_DIR = "tmp/jscpd"

EXIT_OK = 0
EXIT_FINDING = 1


# --------------------------------------------------------------------------
# the binary
# --------------------------------------------------------------------------


def platform_key() -> str:
    """The asset key for the machine we run on."""
    system = platform.system().lower()
    machine = platform.machine().lower()
    if system == "linux":
        if machine in ("x86_64", "amd64"):
            return "linux-x64"
        if machine in ("aarch64", "arm64"):
            return "linux-arm64"
    if system == "darwin":
        if machine in ("x86_64", "amd64"):
            return "darwin-x64"
        if machine in ("aarch64", "arm64"):
            return "darwin-arm64"
    return ""


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_archive(archive: Path, expected: str) -> None:
    """Refuse an archive whose sha256 is not the pinned one."""
    actual = sha256_of(archive)
    if actual != expected:
        raise SystemExit(
            f"  sha256 mismatch for {archive.name}: expected {expected}, got {actual}. "
            f"The download is refused and the tool is not run."
        )


def download_tool(cache_dir: Path, force: bool = False) -> Path:
    """Fetch the pinned binary, verify its sha256, and return its path."""
    key = platform_key()
    if key not in TOOL_ASSETS:
        raise SystemExit(
            f"  no pinned {TOOL_NAME} asset for {platform.system()}/{platform.machine()}; "
            f"the supported platforms are {', '.join(sorted(TOOL_ASSETS))}"
        )
    asset, expected = TOOL_ASSETS[key]
    cache_dir.mkdir(parents=True, exist_ok=True)
    archive = cache_dir / asset
    if force or not archive.exists() or sha256_of(archive) != expected:
        url = f"{TOOL_RELEASES}/v{TOOL_VERSION}/{asset}"
        print(f"  downloading {TOOL_NAME} {TOOL_VERSION} ({asset})")
        payload = b""
        last_error: Exception | None = None
        for attempt in range(1, 4):
            try:
                with urllib.request.urlopen(url, timeout=120) as response:  # noqa: S310
                    payload = response.read()
                break
            except Exception as exc:  # noqa: BLE001 - a network hiccup, retried
                last_error = exc
                print(f"  attempt {attempt} failed: {exc}")
        if not payload:
            raise SystemExit(
                f"  could not download {url} after 3 attempts: {last_error}"
            )
        archive.write_bytes(payload)
        verify_archive(archive, expected)
    target = cache_dir / TOOL_NAME
    with tarfile.open(archive) as tar:
        extracted = tar.extractfile(TOOL_NAME)
        if extracted is None:
            raise SystemExit(f"  {asset} carries no {TOOL_NAME} binary")
        target.write_bytes(extracted.read())
    target.chmod(0o755)
    return target


# --------------------------------------------------------------------------
# the report
# --------------------------------------------------------------------------


def read_report(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise SystemExit(f"  no report at {path}; run with --run first")
    except json.JSONDecodeError as exc:
        raise SystemExit(f"  {path} is not JSON: {exc}")
    if not isinstance(data, dict) or "statistics" not in data:
        raise SystemExit(f"  {path} is not a {TOOL_NAME} report")
    return data


def totals(report: dict) -> dict:
    total = report.get("statistics", {}).get("total", {})
    return {
        "files": int(total.get("sources", 0)),
        "lines": int(total.get("lines", 0)),
        "tokens": int(total.get("tokens", 0)),
        "clones": int(total.get("clones", 0)),
        "duplicated_lines": int(total.get("duplicatedLines", 0)),
        "duplicated_tokens": int(total.get("duplicatedTokens", 0)),
        "percentage": round(float(total.get("percentage", 0.0)), 2),
        "percentage_tokens": round(float(total.get("percentageTokens", 0.0)), 2),
        "new_clones": int(total.get("newClones", 0)),
        "new_duplicated_lines": int(total.get("newDuplicatedLines", 0)),
    }


def read_baseline(path: Path) -> dict:
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"  {path} is not JSON: {exc}")


def config_fingerprint(root: Path) -> str:
    config = root / CONFIG_NAME
    if not config.exists():
        return ""
    return hashlib.sha256(config.read_bytes()).hexdigest()[:16]


# --------------------------------------------------------------------------
# the rules
# --------------------------------------------------------------------------


def analyze(
    report: dict,
    baseline: dict,
    min_files: int,
    fail_on_growth: bool,
    fail_on_new_clones: int | None,
    fingerprint: str = "",
) -> tuple[list[str], list[str], dict]:
    """Return (findings, warnings, summary) for one report."""
    measured = totals(report)
    findings: list[str] = []
    warnings: list[str] = []

    floors = baseline.get("floors", {}) if baseline else {}

    floor_files = int(floors.get("files", 0) or 0)
    if min_files:
        floor_files = min_files
    if floor_files and measured["files"] < floor_files:
        findings.append(
            f"DUP-FLOOR files {measured['files']} < floor {floor_files} — the scan "
            f"analysed less of the tree than it should; a scan that measured "
            f"nothing cannot report that everything is fine"
        )

    floor_lines = floors.get("duplicated_lines")
    if floor_lines is not None:
        if measured["duplicated_lines"] > int(floor_lines):
            message = (
                f"DUP-GROWTH duplicated lines {measured['duplicated_lines']} > "
                f"baseline {floor_lines} — the tree grew more duplication than "
                f"the baseline allows"
            )
            if fail_on_growth:
                findings.append(message)
            else:
                warnings.append(message + " (advisory: --fail-on-growth is off)")
        elif measured["duplicated_lines"] < int(floor_lines) and not findings:
            # The "the tree got cleaner" reading is only true of a scan that
            # measured the tree. A narrowed scan measured nothing, and its
            # smaller total says nothing about the tree.
            warnings.append(
                f"DUP-BASELINE-STALE duplicated lines {measured['duplicated_lines']} < "
                f"baseline {floor_lines} — the tree got cleaner; lower the "
                f"baseline with `--update` when the programme lands"
            )

    if baseline:
        recorded_version = (baseline.get("tool") or {}).get("version")
        if recorded_version and recorded_version != TOOL_VERSION:
            findings.append(
                f"DUP-MEASUREMENT the tool moved from {recorded_version} to "
                f"{TOOL_VERSION} — the two measurements are not comparable; "
                f"re-measure with `--update`"
            )
        recorded_fingerprint = baseline.get("config_fingerprint")
        if recorded_fingerprint and fingerprint and recorded_fingerprint != fingerprint:
            findings.append(
                f"DUP-MEASUREMENT the config moved since the baseline was written "
                f"({recorded_fingerprint} -> {fingerprint}) — the two measurements "
                f"are not comparable; re-measure with `--update`"
            )

    if fail_on_new_clones is not None and measured["new_clones"] > fail_on_new_clones:
        findings.append(
            f"DUP-NEW-CLONES new clones {measured['new_clones']} > "
            f"{fail_on_new_clones} — the clone count relative to the base ref "
            f"grew. NOTE: this number is edit-sensitive (an edit of an "
            f"existing duplicate counts as new); read it as a pointer, not as "
            f"a verdict"
        )

    summary = {
        "files": measured["files"],
        "lines": measured["lines"],
        "tokens": measured["tokens"],
        "clones": measured["clones"],
        "duplicated_lines": measured["duplicated_lines"],
        "duplicated_tokens": measured["duplicated_tokens"],
        "percentage": measured["percentage"],
        "percentage_tokens": measured["percentage_tokens"],
        "new_clones": measured["new_clones"],
        "new_duplicated_lines": measured["new_duplicated_lines"],
        "floor_files": floor_files,
        "floor_duplicated_lines": floor_lines,
    }
    return findings, warnings, summary


def print_summary(summary: dict) -> None:
    floor_lines = summary.get("floor_duplicated_lines")
    print()
    print(f"  files analysed    {summary['files']} (floor {summary['floor_files']})")
    print(f"  lines             {summary['lines']}")
    print(f"  clones            {summary['clones']}")
    if floor_lines is None:
        print(f"  duplicated lines  {summary['duplicated_lines']}")
    else:
        print(f"  duplicated lines  {summary['duplicated_lines']} (baseline {floor_lines})")
    print(
        f"  duplication       {summary['percentage']}% of lines, "
        f"{summary['percentage_tokens']}% of tokens"
    )
    if summary["new_clones"]:
        print(
            f"  new clones        {summary['new_clones']} "
            f"({summary['new_duplicated_lines']} lines) vs the base ref"
        )
    print()


# --------------------------------------------------------------------------
# the run
# --------------------------------------------------------------------------


def run_tool(
    binary: Path,
    root: Path,
    report_path: Path,
    config: Path,
    extra_args: list[str],
) -> None:
    output_dir = report_path.parent if str(report_path.parent) else Path(".")
    output_dir.mkdir(parents=True, exist_ok=True)
    command = [
        str(binary),
        "--config",
        str(config),
        "--reporters",
        "json",
        "--output",
        str(output_dir),
        "--report-name",
        report_path.stem,
        *extra_args,
    ]
    print(f"  $ {' '.join(command)}")
    # A stale report from an earlier run must never be read as this run's
    # verdict: the run removes it first, and a run that produced no report at
    # all is a failure, not a measurement.
    report_path.unlink(missing_ok=True)
    completed = subprocess.run(command, cwd=root, capture_output=True, text=True)
    if completed.returncode != 0:
        # jscpd writes its report BEFORE the fail-on-empty check, so a scan
        # that measured nothing still leaves a report to read. The verdict
        # belongs to the rules below, in the house diagnostic, not to the
        # tool's exit code.
        sys.stderr.write(completed.stdout)
        sys.stderr.write(completed.stderr)
        print(f"  {TOOL_NAME} exited with {completed.returncode}")
    if not report_path.exists():
        raise SystemExit(f"  {TOOL_NAME} wrote no report at {report_path}")


def build_baseline(report: dict, root: Path) -> dict:
    measured = totals(report)
    existing = read_baseline(root / BASELINE_NAME)
    floors = dict(existing.get("floors", {}))
    # The file floor is a HAND-SET ratchet: it is lowered in the same diff as
    # a legitimate removal, where a reviewer sees it, and `--update` does not
    # touch it. The duplicated-lines floor is the reviewed measurement, and
    # `--update` rewrites it — downwards as the programme lands.
    floors.setdefault("files", measured["files"])
    floors["duplicated_lines"] = measured["duplicated_lines"]
    return {
        "version": 1,
        "generator": "tools/check-dup-jscpd.py",
        "tool": {
            "name": TOOL_NAME,
            "version": TOOL_VERSION,
            "license": TOOL_LICENSE,
        },
        "policy": {
            "scope": (
                "the repository root, **/*.cs, minus bin/, obj/, contrib/, "
                "samples/, tools/, build/, analyzers/ and "
                "tests/Harbor.Architecture.Tests/"
            ),
            "min_tokens": 100,
            "min_lines": 6,
            "mode": "weak",
            "format": "csharp",
        },
        "config_fingerprint": config_fingerprint(root),
        "census": {
            "files": measured["files"],
            "lines": measured["lines"],
            "tokens": measured["tokens"],
        },
        "metrics": {
            "clones": measured["clones"],
            "duplicated_lines": measured["duplicated_lines"],
            "duplicated_tokens": measured["duplicated_tokens"],
            "percentage": measured["percentage"],
        },
        "floors": floors,
    }


# --------------------------------------------------------------------------
# the self-test
# --------------------------------------------------------------------------


def _fake_report(files: int, clones: int, duplicated_lines: int, new_clones: int = 0) -> dict:
    return {
        "duplicates": [],
        "statistics": {
            "total": {
                "sources": files,
                "lines": 1000,
                "tokens": 5000,
                "clones": clones,
                "duplicatedLines": duplicated_lines,
                "duplicatedTokens": duplicated_lines * 5,
                "percentage": 2.0,
                "percentageTokens": 2.0,
                "newClones": new_clones,
                "newDuplicatedLines": new_clones * 10,
            }
        },
    }


def self_test() -> int:
    failures: list[str] = []

    def expect(condition: bool, message: str) -> None:
        if not condition:
            failures.append(message)

    baseline = {
        "tool": {"name": TOOL_NAME, "version": TOOL_VERSION},
        "metrics": {"clones": 286, "duplicated_lines": 8353},
        "floors": {"files": 1700, "duplicated_lines": 8353},
        "config_fingerprint": "abc123",
    }

    # 1. a clean report passes.
    findings, warnings, summary = analyze(
        _fake_report(1971, 286, 8353), baseline, 0, True, None, "abc123"
    )
    expect(not findings, f"a clean report must pass, got {findings}")
    expect(not warnings, f"a clean report must not warn, got {warnings}")
    expect(summary["files"] == 1971, "the summary must carry the file count")

    # 2. an empty scan fails the floor. This is the load-bearing fixture:
    #    a scan that measured nothing must not be green.
    findings, _warnings, _ = analyze(
        _fake_report(0, 0, 0), baseline, 0, True, None, "abc123"
    )
    expect(
        any(f.startswith("DUP-FLOOR") for f in findings),
        f"an empty scan must fail the floor, got {findings}",
    )

    # 3. a narrowed scan fails the floor too.
    findings, _warnings, _ = analyze(
        _fake_report(12, 1, 10), baseline, 0, True, None, "abc123"
    )
    expect(
        any(f.startswith("DUP-FLOOR") for f in findings),
        f"a narrowed scan must fail the floor, got {findings}",
    )

    # 4. growth fails, and only when the growth rule is on.
    findings, warnings, _ = analyze(
        _fake_report(1971, 300, 8400), baseline, 0, True, None, "abc123"
    )
    expect(
        any(f.startswith("DUP-GROWTH") for f in findings),
        f"growth must fail the gate, got {findings}",
    )
    findings, warnings, _ = analyze(
        _fake_report(1971, 300, 8400), baseline, 0, False, None, "abc123"
    )
    expect(
        not any(f.startswith("DUP-GROWTH") for f in findings),
        f"growth must stay advisory when the rule is off, got {findings}",
    )
    expect(
        any(w.startswith("DUP-GROWTH") for w in warnings),
        f"an advisory growth must be reported, got {warnings}",
    )

    # 5. a cleaner tree warns and does not fail.
    findings, warnings, _ = analyze(
        _fake_report(1971, 280, 8000), baseline, 0, True, None, "abc123"
    )
    expect(not findings, f"a cleaner tree must not fail, got {findings}")
    expect(
        any(w.startswith("DUP-BASELINE-STALE") for w in warnings),
        f"a cleaner tree must warn about the stale baseline, got {warnings}",
    )

    # 5b. a scan that measured nothing does not get to say the tree got
    #     cleaner: it measured nothing, and its total says nothing.
    findings, warnings, _ = analyze(
        _fake_report(0, 0, 0), baseline, 0, True, None, "abc123"
    )
    expect(
        not any(w.startswith("DUP-BASELINE-STALE") for w in warnings),
        f"an empty scan must not claim the tree got cleaner, got {warnings}",
    )

    # 6. the new-clone count is only blocking when it is asked for.
    findings, _warnings, _ = analyze(
        _fake_report(1971, 290, 8353, new_clones=4), baseline, 0, True, None, "abc123"
    )
    expect(not findings, f"the new-clone count must be advisory by default, got {findings}")
    findings, _warnings, _ = analyze(
        _fake_report(1971, 290, 8353, new_clones=4), baseline, 0, True, 0, "abc123"
    )
    expect(
        any(f.startswith("DUP-NEW-CLONES") for f in findings),
        f"--fail-on-new-clones must fail on a new clone, got {findings}",
    )

    # 7. a moved config or a moved tool version is not comparable.
    findings, _warnings, _ = analyze(
        _fake_report(1971, 286, 8353), baseline, 0, True, None, "deadbeef"
    )
    expect(
        any(f.startswith("DUP-MEASUREMENT") for f in findings),
        f"a moved config must fail the comparison, got {findings}",
    )
    moved = dict(baseline)
    moved["tool"] = {"name": TOOL_NAME, "version": "5.0.0"}
    findings, _warnings, _ = analyze(
        _fake_report(1971, 286, 8353), moved, 0, True, None, "abc123"
    )
    expect(
        any(f.startswith("DUP-MEASUREMENT") for f in findings),
        f"a moved tool version must fail the comparison, got {findings}",
    )

    # 8. a corrupted download is refused before the tool is run.
    with tempfile.TemporaryDirectory() as tmp:
        archive = Path(tmp) / "fake.tar.gz"
        archive.write_bytes(b"not a tarball")
        try:
            verify_archive(archive, "0" * 64)
        except SystemExit:
            pass
        else:
            failures.append("a corrupted archive must be refused")

    if failures:
        for failure in failures:
            print(f"  FAIL {failure}")
        return EXIT_FINDING
    print(
        "  ok: the empty scan, the narrowed scan, the growth, the stale "
        "baseline, the new-clone count, the moved config and the corrupted "
        "download all behave"
    )
    return EXIT_OK


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Second duplication detector: jscpd 5.4.1 over the maintained C# "
            "surface, compared with the committed baseline."
        )
    )
    parser.add_argument("--root", default=str(REPO_ROOT), help="the repository root")
    parser.add_argument("--run", action="store_true", help="download and run the tool")
    parser.add_argument("--download", action="store_true", help="only fetch the pinned binary")
    parser.add_argument("--jscpd", default="", help="path to a jscpd binary (skips the download)")
    parser.add_argument("--report", default=REPORT_NAME, help="the JSON report to read or write")
    parser.add_argument("--baseline", default=BASELINE_NAME, help="the committed baseline")
    parser.add_argument("--config", default=CONFIG_NAME, help="the jscpd config")
    parser.add_argument("--cache-dir", default=CACHE_DIR, help="where the binary is cached")
    parser.add_argument("--min-files", type=int, default=0, help="the scan-size floor")
    parser.add_argument(
        "--fail-on-growth",
        dest="fail_on_growth",
        action="store_true",
        default=True,
        help="fail when the tree grew more duplication than the baseline allows",
    )
    parser.add_argument(
        "--no-fail-on-growth",
        dest="fail_on_growth",
        action="store_false",
        help="report the growth as a warning instead of failing",
    )
    parser.add_argument(
        "--fail-on-new-clones",
        type=int,
        default=None,
        help="fail when more than N clones are new relative to the base ref",
    )
    parser.add_argument(
        "--baseline-ref",
        default="",
        help="a git ref to compare against (jscpd's --baseline-from-ref)",
    )
    parser.add_argument("--update", action="store_true", help="rewrite the baseline")
    parser.add_argument("--self-test", action="store_true", help="prove the failure path")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()

    if args.self_test:
        return self_test()

    if args.download:
        binary = download_tool(root / args.cache_dir, force=True)
        print(f"  {TOOL_NAME} {TOOL_VERSION} at {binary}")
        return EXIT_OK

    report_path = Path(args.report)
    if not report_path.is_absolute():
        report_path = root / report_path
    baseline_path = Path(args.baseline)
    if not baseline_path.is_absolute():
        baseline_path = root / baseline_path
    config_path = Path(args.config)
    if not config_path.is_absolute():
        config_path = root / config_path

    if args.run:
        binary = Path(args.jscpd) if args.jscpd else download_tool(root / args.cache_dir)
        extra = ["--baseline-from-ref", args.baseline_ref] if args.baseline_ref else []
        run_tool(binary, root, report_path, config_path, extra)

    if args.update:
        report = read_report(report_path)
        baseline = build_baseline(report, root)
        baseline_path.parent.mkdir(parents=True, exist_ok=True)
        baseline_path.write_text(
            json.dumps(baseline, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        print(f"  wrote {baseline_path}")
        return EXIT_OK

    report = read_report(report_path)
    baseline = read_baseline(baseline_path)
    if not baseline:
        print(f"  no baseline at {baseline_path}; nothing to compare against")
    findings, warnings, summary = analyze(
        report,
        baseline,
        args.min_files,
        args.fail_on_growth,
        args.fail_on_new_clones,
        config_fingerprint(root),
    )
    print_summary(summary)
    for warning in warnings:
        print(f"  warn  {warning}")
    for finding in findings:
        print(f"  FAIL  {finding}")
    if findings:
        return EXIT_FINDING
    print("OK: no duplication beyond the committed baseline")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
