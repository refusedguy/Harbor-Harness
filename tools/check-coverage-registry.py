#!/usr/bin/env python3
"""The gate: the registry of consciously uncovered files against the measurement.

Stdlib only — no pip, no network, no dotnet. Wired into the nightly lane's
coverage job, and it is a RATCHET in both directions.

WHAT IT CHECKS

    1. COV-DATA-EMPTY     the measurement produced nothing. The gate cannot
                          judge a registry against an empty measurement, and
                          an empty measurement is what an engine with no PDB
                          to work with produces (run 39508517419: 20 empty
                          cobertura files, and every parser that read them
                          saw "100% covered" because `line-rate` was 1).
                          This is the finding that keeps a broken instrument
                          from looking like a clean tree.
    2. COV-UNCOVERED      a product file the measurement says nothing about,
                          and no registry row names it. This is the file that
                          STOPPED BEING COVERED, or the new file that was
                          never covered.
    3. COV-ROW-STALE      a registry row that no uncovered file needs any
                          more. The file got covered, so the row has
                          outlived its reason and has to go.
    4. COV-ROW-MISSING    a registry row that names a path the tree does not
                          have. The file was deleted or moved.
    5. COV-FLOOR          the measurement is smaller than the floor says it
                          may be. A gate that scanned nothing must not report
                          "clean".

THE PARTIAL MEASUREMENT

    The nightly lane runs twenty suites and RECORDS a failure instead of
    failing the run, so a measurement can be incomplete: a suite that died
    before it ran reports nothing, and every file only that suite covers
    looks uncovered. Reporting those as holes would make the gate fire on a
    flake, which is how a gate gets switched off.

    So the gate reads the status file the collect step writes, and when a
    suite did not pass it compares only the direction that stays true under
    a partial measurement — a row that is no longer needed — and says that
    it skipped the other one. The message is in the output; the check that
    could be made was made.

WHY A RATCHET AND NOT A THRESHOLD

    Issue #99 deferred a global percentage gate on purpose: a repository
    that has been alive for a while would fail such a gate on its first
    day, and a gate that is red on its first day is not a gate. So there is
    no number here. There is a LIST: the files that are consciously not
    covered, each with a reason and an owner, and the gate says the list
    and the measurement disagree.

    That is the same shape as `tools/test-project-baseline.txt` and
    `tools/string-switch-exceptions.txt`, and it has the same property:
    the file can only shrink. A file that gets covered has to have its row
    deleted in the same diff that covered it, and a file that stops being
    covered has to have a row added in the same diff that dropped it.

WHAT IT DOES NOT DO

    It does not fail on a percentage, it does not fail on a partially
    covered file, and it does not decide what the coverage should be. The
    REPORT (tools/coverage_report.py) shows the per-file numbers; this
    script only asks whether the list of accepted holes still matches the
    measurement.

USAGE

    check_coverage_registry.py [--coverage-dir DIR] [--registry FILE]
                               [--source-root DIR] [--min-files N]
                               [--min-covered N] [--json] [--self-test]

EXIT CODES

    0  the registry and the measurement agree
    1  they do not (the findings are on stdout)
    2  bad invocation
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from coverage_report import (  # noqa: E402
    DEFAULT_REGISTRY,
    FileCoverage,
    Model,
    RegistryRow,
    load_coverage,
    load_registry,
    product_source_files,
)


class Finding:
    """One disagreement between the registry and the measurement."""

    def __init__(self, code: str, message: str, where: str = ""):
        self.code = code
        self.message = message
        self.where = where

    def __str__(self) -> str:
        head = f"{self.code}: {self.message}"
        return f"{head} ({self.where})" if self.where else head

    def as_dict(self) -> dict[str, str]:
        return {"code": self.code, "message": self.message, "where": self.where}


def read_suite_status(path: str) -> dict[str, str]:
    """The per-suite outcome the collect step writes: `name<TAB>status`."""
    status: dict[str, str] = {}
    if not path or not os.path.exists(path):
        return status
    with open(path, encoding="utf-8") as handle:
        for raw in handle:
            name, _, state = raw.rstrip("\n").partition("\t")
            if name.strip():
                status[name.strip()] = state.strip()
    return status


def evaluate(model: Model, min_files: int = 0, min_covered: int = 0,
             partial: bool = False) -> list[Finding]:
    """Compare the registry with the measurement.

    The findings are the whole answer: an empty list means the two agree.
    `partial` says the measurement is incomplete (a suite did not pass), so
    the direction that a partial measurement cannot support — the uncovered
    files — is left out.
    """
    findings: list[Finding] = []
    product = model.product_files()
    covered = model.covered_product_files()

    # 1. The measurement itself.
    if not model.suites:
        findings.append(Finding(
            "COV-DATA-EMPTY",
            "the coverage directory holds no report at all, so there is "
            "nothing to compare the registry against — an empty measurement "
            "is not a clean tree",
            "coverage",
        ))
    elif not model.files:
        findings.append(Finding(
            "COV-DATA-EMPTY",
            f"the {len(model.suites)} report(s) name no source file at all, so "
            "the measurement is empty — an empty measurement is not a clean tree",
            "coverage",
        ))
    elif not covered:
        findings.append(Finding(
            "COV-DATA-EMPTY",
            "no product file has a single covered line, so the measurement "
            "reached nothing — an empty measurement is not a clean tree",
            "coverage",
        ))

    # 2. The files that are not covered and nobody accepted.
    unaccepted = model.unaccepted_files()
    for path in unaccepted:
        if partial:
            # The measurement is incomplete: a suite that died reports
            # nothing, and the files only it covers look uncovered. Naming
            # them would be a finding about a flake, not about the tree.
            continue
        status = model.status_of(path)
        what = (
            "the measurement saw it and not one line ran"
            if status == "never-ran"
            else "no suite in this lane measured it"
        )
        findings.append(Finding(
            "COV-UNCOVERED",
            f"the file is not covered ({what}) and no registry row names it — "
            "add a row with a reason, or cover the file",
            path,
        ))

    # 3. The rows that are not needed any more.
    for row in model.registry:
        if row.is_directory:
            still = [
                path for path in model.holes()
                if path.startswith(row.path)
            ]
            if still:
                continue
            exists = os.path.isdir(row.path)
            findings.append(Finding(
                "COV-ROW-STALE",
                f"the directory row covers no uncovered file any more"
                + ("" if exists else " — and the directory is not in the tree"),
                row.path,
            ))
        else:
            if not model.is_covered(row.path):
                continue
            findings.append(Finding(
                "COV-ROW-STALE",
                "the file is covered now, so the row has outlived its reason "
                "and has to go",
                row.path,
            ))

    # 4. The rows that name a path the tree does not have.
    for row in model.registry:
        if row.is_directory:
            continue
        if row.path not in product:
            findings.append(Finding(
                "COV-ROW-MISSING",
                "the row names a file the product tree does not have — the "
                "file was deleted or moved, so the row has to go",
                row.path,
            ))

    # 5. The floors: a gate that scanned nothing must not say "clean".
    if len(product) < min_files:
        findings.append(Finding(
            "COV-FLOOR",
            f"the product tree has {len(product)} file(s), the floor is {min_files}",
            "product tree",
        ))
    if len(covered) < min_covered:
        findings.append(Finding(
            "COV-FLOOR",
            f"{len(covered)} product file(s) are covered, the floor is {min_covered}",
            "measurement",
        ))
    return findings


# ---------------------------------------------------------------- self-test


def _write(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def _cobertura(path: str, filename: str, lines: dict[int, int]) -> None:
    """A minimal cobertura file: one class, the given line hits."""
    body = "".join(
        f'        <line number="{number}" hits="{hits}" branch="false" />\n'
        for number, hits in sorted(lines.items())
    )
    _write(path, f"""<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<coverage line-rate="0.5" branch-rate="0" complexity="1" version="1.9" timestamp="0">
  <sources><source>/_</source></sources>
  <packages>
    <package name="Foo" line-rate="0.5" branch-rate="0" complexity="1">
      <classes>
        <class name="Foo.{os.path.basename(filename).replace('.cs', '')}" filename="{filename}" line-rate="0.5" branch-rate="0" complexity="1">
          <lines>
{body}          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
""")


def _fixture(root: str) -> dict[str, str]:
    """A throwaway tree with three product files and one measurement.

    `Covered.cs` ran, `Dead.cs` was measured and never ran a line, and
    `Orphan.cs` belongs to a project no report mentions at all. The
    registry that goes with it accepts the two holes."""
    source_root = os.path.join(root, "repo")
    coverage_dir = os.path.join(source_root, "Coverage")
    registry = os.path.join(root, "registry.txt")
    for name in ("Covered", "Dead"):
        _write(os.path.join(source_root, "src", "Foo", f"{name}.cs"), f"// {name}\n")
    _write(os.path.join(source_root, "src", "Bar", "Orphan.cs"), "// Orphan\n")
    _cobertura(
        os.path.join(coverage_dir, "Harbor.Foo.Tests.cobertura.xml"),
        "src/Foo/Covered.cs",
        {1: 1, 2: 0},
    )
    _cobertura(
        os.path.join(coverage_dir, "Harbor.Foo.Other.Tests.cobertura.xml"),
        "src/Foo/Dead.cs",
        {1: 0},
    )
    _write(registry, """# the holes of the fixture
src/Foo/Dead.cs | NEVER-RAN | The measurement saw the file and ran none of its lines. | @refusedguy
src/Bar/Orphan.cs | NOT-MEASURED | The project belongs to is not loaded by any suite of the lane. | @refusedguy
""")
    return {"root": root, "source_root": source_root, "coverage": coverage_dir,
            "registry": registry}


def _model_for(fixture: dict[str, str]) -> Model:
    model = load_coverage(fixture["coverage"], fixture["source_root"])
    model.registry = load_registry(fixture["registry"])
    return model


def self_test() -> int:
    """Prove the gate fires on both sides of the ratchet.

    The two defects the gate exists for are planted one at a time:
      * a file that STOPPED BEING COVERED and is not in the registry;
      * a registry row that is no longer NEEDED because the file is covered.
    """
    failures: list[str] = []
    tmp = tempfile.mkdtemp(prefix="harbor-coverage-registry-")
    try:
        # The clean fixture: the registry matches the measurement.
        clean = _fixture(os.path.join(tmp, "clean"))
        findings = evaluate(_model_for(clean), min_files=3, min_covered=1)
        if findings:
            failures.append(
                "the clean fixture is not clean: "
                + "; ".join(str(finding) for finding in findings)
            )

        # (a) A file that stopped being covered: the registry still says
        #     nothing about it, and the measurement now says it never ran.
        stopped = _fixture(os.path.join(tmp, "stopped"))
        _cobertura(
            os.path.join(stopped["coverage"], "Harbor.Foo.Tests.cobertura.xml"),
            "src/Foo/Covered.cs",
            {1: 0, 2: 0},
        )
        findings = evaluate(_model_for(stopped), min_files=3, min_covered=0)
        codes = [finding.code for finding in findings]
        if "COV-UNCOVERED" not in codes:
            failures.append(
                "a file that stopped being covered did not raise COV-UNCOVERED; "
                f"got {codes or 'no findings'}"
            )
        else:
            where = [f.where for f in findings if f.code == "COV-UNCOVERED"]
            if "src/Foo/Covered.cs" not in where:
                failures.append(
                    "the finding did not name the file that stopped being "
                    f"covered; it named {where}"
                )

        # (b) A registry row that is no longer needed: the file it accepts
        #     is covered now.
        stale = _fixture(os.path.join(tmp, "stale"))
        with open(stale["registry"], "a", encoding="utf-8") as handle:
            handle.write(
                "src/Foo/Covered.cs | NEVER-RAN | A row for a file that is "
                "covered, so the registry is stale in the planted fixture. | @refusedguy\n"
            )
        findings = evaluate(_model_for(stale), min_files=3, min_covered=1)
        codes = [finding.code for finding in findings]
        if "COV-ROW-STALE" not in codes:
            failures.append(
                "a registry row that is no longer needed did not raise "
                f"COV-ROW-STALE; got {codes or 'no findings'}"
            )
        else:
            where = [f.where for f in findings if f.code == "COV-ROW-STALE"]
            if "src/Foo/Covered.cs" not in where:
                failures.append(
                    "the stale-row finding did not name the row; it named "
                    f"{where}"
                )

        # (c) An empty measurement: the gate must not call a tree with no
        #     data at all clean.
        empty = _fixture(os.path.join(tmp, "empty"))
        for name in os.listdir(empty["coverage"]):
            os.remove(os.path.join(empty["coverage"], name))
        findings = evaluate(_model_for(empty), min_files=3, min_covered=0)
        codes = [finding.code for finding in findings]
        if "COV-DATA-EMPTY" not in codes:
            failures.append(
                "an empty measurement did not raise COV-DATA-EMPTY; got "
                f"{codes or 'no findings'}"
            )

        # (c2) A report that cannot be READ is a measurement that did not
        #      happen: the files it would have covered look uncovered, so
        #      the comparison has to be skipped.
        unreadable = _fixture(os.path.join(tmp, "unreadable"))
        with open(os.path.join(unreadable["coverage"],
                               "Harbor.Foo.Tests.cobertura.xml"), "w",
                  encoding="utf-8") as handle:
            handle.write("<coverage><packages>truncated")
        model = _model_for(unreadable)
        if not model.unreadable:
            failures.append(
                "the unreadable report was not reported as unreadable"
            )
        findings = evaluate(model, min_files=3, min_covered=0, partial=True)
        codes = [finding.code for finding in findings]
        if "COV-UNCOVERED" in codes:
            failures.append(
                "an unreadable report still produced hole findings, so a "
                "broken report would be reported as a hole in the tree"
            )

        # (d) A row that names a file the tree does not have.
        missing = _fixture(os.path.join(tmp, "missing"))
        with open(missing["registry"], "a", encoding="utf-8") as handle:
            handle.write(
                "src/Foo/Gone.cs | NEVER-RAN | A row for a file that is not in "
                "the tree any more, so the registry is stale. | @refusedguy\n"
            )
        findings = evaluate(_model_for(missing), min_files=3, min_covered=1)
        codes = [finding.code for finding in findings]
        if "COV-ROW-MISSING" not in codes:
            failures.append(
                "a row for a file that is not in the tree did not raise "
                f"COV-ROW-MISSING; got {codes or 'no findings'}"
            )

        # (e) A PARTIAL measurement: the direction that a partial
        #     measurement cannot support is skipped, the other one is not.
        partial = _fixture(os.path.join(tmp, "partial"))
        _cobertura(
            os.path.join(partial["coverage"], "Harbor.Foo.Tests.cobertura.xml"),
            "src/Foo/Covered.cs",
            {1: 0, 2: 0},
        )
        findings = evaluate(_model_for(partial), min_files=3, min_covered=0,
                            partial=True)
        codes = [finding.code for finding in findings]
        if "COV-UNCOVERED" in codes:
            failures.append(
                "a partial measurement still reported the uncovered files, so "
                "a flaky suite would make the gate fire on a flake"
            )
        stale = _fixture(os.path.join(tmp, "partial-stale"))
        with open(stale["registry"], "a", encoding="utf-8") as handle:
            handle.write(
                "src/Foo/Covered.cs | NEVER-RAN | A row for a file that is "
                "covered, so the registry is stale in the planted fixture. | @refusedguy\n"
            )
        findings = evaluate(_model_for(stale), min_files=3, min_covered=1,
                            partial=True)
        codes = [finding.code for finding in findings]
        if "COV-ROW-STALE" not in codes:
            failures.append(
                "a partial measurement stopped reporting the stale rows, so a "
                "flake would hide a row that has to go"
            )
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if failures:
        for failure in failures:
            print(f"self-test FAILED: {failure}", file=sys.stderr)
        return 1
    print(
        "self-test OK: the gate reports a file that stopped being covered, a "
        "registry row that is no longer needed, an empty measurement and a row "
        "for a file that is gone — the clean fixture stays clean, and a "
        "partial measurement (a failed suite, an unreadable report) compares "
        "only the direction it supports"
    )
    return 0


# ------------------------------------------------------------------- main


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="check_coverage_registry.py",
        description="Check the coverage registry against the measurement.",
    )
    parser.add_argument("--coverage-dir", default="Coverage",
                        help="the directory with the cobertura files")
    parser.add_argument("--registry", default=DEFAULT_REGISTRY,
                        help="the registry of consciously uncovered files")
    parser.add_argument("--source-root", default=".",
                        help="the repository root")
    parser.add_argument("--min-files", type=int, default=0,
                        help="fail unless the product tree has at least this many files")
    parser.add_argument("--min-covered", type=int, default=0,
                        help="fail unless at least this many product files are covered")
    parser.add_argument("--status", default="",
                        help="the per-suite status file the collect step writes "
                             "('name<TAB>status'); a suite that did not pass makes "
                             "the measurement partial")
    parser.add_argument("--strict", action="store_true",
                        help="fail when the measurement is partial instead of "
                             "comparing only the direction a partial measurement supports")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the gate still fires on a planted defect")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    if not os.path.isdir(args.coverage_dir):
        print(f"error: no coverage directory: {args.coverage_dir}", file=sys.stderr)
        return 2
    if not os.path.exists(args.registry):
        print(f"error: no registry: {args.registry}", file=sys.stderr)
        return 2

    model = load_coverage(args.coverage_dir, args.source_root)
    model.registry = load_registry(args.registry)
    status = read_suite_status(args.status) if args.status else {}
    failed = sorted(name for name, state in status.items() if state != "pass")
    partial = bool(failed) or bool(model.unreadable)
    findings = evaluate(model, min_files=args.min_files, min_covered=args.min_covered,
                        partial=partial)

    if partial and args.strict:
        print(
            f"error: the measurement is partial — {len(failed)} suite(s) did not "
            f"pass ({', '.join(failed)}), so the registry cannot be compared",
            file=sys.stderr,
        )
        return 2

    if args.json:
        payload = {
            "suites": model.suites,
            "unreadable_reports": model.unreadable,
            "partial": partial,
            "failed_suites": failed,
            "product_files": len(model.product_files()),
            "covered_files": len(model.covered_product_files()),
            "uncovered_files": len(model.holes()),
            "accepted_files": len(model.accepted_files()),
            "unaccepted_files": len(model.unaccepted_files()),
            "registry_rows": len(model.registry),
            "findings": [finding.as_dict() for finding in findings],
        }
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        for finding in findings:
            print(finding)
        print(
            f"product files: {len(model.product_files())}   covered: "
            f"{len(model.covered_product_files())}   uncovered: "
            f"{len(model.holes())}   in the registry: "
            f"{len(model.accepted_files())}   registry rows: {len(model.registry)}"
        )
        if partial:
            print(
                f"note: the measurement is PARTIAL — {len(failed)} suite(s) did "
                f"not pass ({', '.join(failed)}), "
                f"{len(model.unreadable)} report(s) could not be read "
                f"({', '.join(model.unreadable)}), so the uncovered files were "
                "NOT compared against the registry; the stale rows were"
            )
        print(f"{len(findings)} finding(s)")

    return 1 if findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
