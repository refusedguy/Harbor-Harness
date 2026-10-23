#!/usr/bin/env bash
# verify-all.sh -- the equivalence proof for the whole family, on one tree.
#
# Every gate is run TWICE on the same tree: the python original (taken from
# git, at the commit it was deleted from) and the compiled port. The stdout,
# the stderr and the exit code must agree, byte for byte.
set -u

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 2

BIN="tools/bin/harbor-gates-linux-x64"
FAILED=0
EXPECTED=0

# THE JSCPD REPORT FIXTURE.
#
# The report case compares the ANALYSIS: the gate reads the report the tool
# wrote and compares it with the committed baseline. Without a report there
# is nothing to analyse, and the two sides then differ in the exit code
# ALONE: the python original raises SystemExit (1) and the binary returns 2
# -- "the gate could not run" is 2 in the contract of the binary, and the
# python had no way to say it. That difference is real and documented, but
# it is not what this harness is for, and a leftover file in /tmp made the
# difference appear or disappear from run to run.
#
# So the fixture is written HERE, every run, in a directory of this run's
# own: the comparison is of the analysis and not of the absence of a file,
# and it is the same on a clean machine and on a busy one.
REPORT_DIR="$(mktemp -d)"
trap 'rm -rf "$REPORT_DIR"' EXIT
JSCPD_REPORT="$REPORT_DIR/jscpd-report.json"
cat > "$JSCPD_REPORT" <<'JSON'
{
  "statistics": {
    "total": {
      "sources": 0,
      "lines": 0,
      "tokens": 0,
      "clones": 0,
      "duplicatedLines": 0,
      "duplicatedTokens": 0,
      "percentage": 0.0,
      "percentageTokens": 0.0,
      "newClones": 0,
      "newDuplicatedLines": 0
    }
  }
}
JSON

run() {
    local label="$1"; shift
    echo "=== $label"
    if bash tools/equivalence.sh "$@"; then
        echo "    PASS"
    else
        echo "    FAIL"
        FAILED=1
    fi
}

# run_expected runs a pair whose difference is KNOWN and is not a defect of
# the port. The pair is still run and still shown, and the harness stays
# green: a harness that reports a known difference as a failure teaches its
# reader to ignore it.
run_expected() {
    local label="$1"; shift
    echo "=== $label"
    if bash tools/equivalence.sh "$@"; then
        echo "    PASS"
    else
        echo "    DIFFERENT, and expected to be (see the note below)"
        EXPECTED=$((EXPECTED + 1))
    fi
}

# py_original says where the python original of a gate lives in git.
#
# The original is the file the gate was BEFORE it moved into the binary, so
# it is taken from a commit that still has it: dev's tip while dev still
# carries the file, and the parent of the commit that deleted it after that.
# A harness that reads a path that no longer exists proves nothing: it
# fails on the file it cannot find, and the failure reads like a
# divergence.
py_original() {
    local path="$1"
    local last
    if git cat-file -e "origin/dev:$path" 2>/dev/null; then
        echo "origin/dev:$path"
        return 0
    fi
    last="$(git rev-list -1 origin/dev -- "$path")"
    if [ -z "$last" ] || ! git cat-file -e "$last^:$path" 2>/dev/null; then
        echo "MISSING:$path"
        return 1
    fi
    echo "$last^:$path"
}

PIN="$(py_original tools/check-tool-schema-pin.py),$(py_original tools/tool_schema_model.py)"
JSON_REFLECTION="$(py_original tools/check-test-json-reflection.py)"
JSCPD="$(py_original tools/check-dup-jscpd.py)"
PACKAGES="$(py_original tools/check-test-packages.py)"
TUNIT="$(py_original tools/check-tunit-analyzer-gate.py)"
DUPTEST="$(py_original tools/check-test-dup.py)"
PROJECTS="$(py_original tools/check-test-projects.py)"

run "check-tool-schema-pin (the gate)"        "$PIN" --min-tools 25 -- check-tool-schema-pin --min-tools 25
run "check-tool-schema-pin (the self-test)"  "$PIN" --selftest -- check-tool-schema-pin --selftest
run "check-tool-schema-pin (the report)"     "$PIN" --report -- check-tool-schema-pin --report
run "check-tunit-analyzer-gate (self-test)"   "$TUNIT" --selftest -- check-tunit-analyzer-gate --selftest
run "check-test-json-reflection (the gate)"   "$JSON_REFLECTION" -- check-test-json-reflection
run "check-test-json-reflection (report)"     "$JSON_REFLECTION" --report -- check-test-json-reflection --report
# THE TWO REFLECTION-JSON CASES BELOW ARE EXPECTED TO DIFFER, AND HERE IS
# WHY.
#
# dev ported the reflection-JSON gate too (#2177), and it did two things at
# once: it moved the gate into the binary, and it FIXED the gate -- the two
# paths of the original counted a used exception differently, and the new
# one counts it the same way on every path. The json-out form grew a
# `stale_exceptions` key and the self-test grew a fixture, so the new port
# and the old original are not the same program any more: the difference is
# the upstream fix, and not a defect of the port.
#
# The tree keeps dev's port: one gate, one implementation. The proof for
# that port is that pull request's own; these two cases are run and shown
# here, and they do not make the harness red.
run_expected "check-test-json-reflection (json-out)"  "$JSON_REFLECTION" --json-out -- check-test-json-reflection --json-out
run_expected "check-test-json-reflection (self-test)" "$JSON_REFLECTION" --self-test -- check-test-json-reflection --self-test
run "check-dup-jscpd (the self-test)"         "$JSCPD" --self-test -- check-dup-jscpd --self-test
run "check-dup-jscpd (the report)"           "$JSCPD" --report "$JSCPD_REPORT" -- check-dup-jscpd --report "$JSCPD_REPORT"
run "check-test-packages (the self-test)"     "$PACKAGES" --self-test -- check-test-packages --self-test
run "check-test-packages (the gate)"          "$PACKAGES" -- check-test-packages
run "check-test-packages (the report)"        "$PACKAGES" --report -- check-test-packages --report
run "check-test-packages (the floors)"        "$PACKAGES" --min-projects 43 --min-declared 50 --min-packages 1400 --allow-missing-assets -- check-test-packages --min-projects 43 --min-declared 50 --min-packages 1400 --allow-missing-assets
run "check-test-dup (the self-test)"          "$DUPTEST" --self-test -- check-test-dup --self-test
run "check-test-dup (the gate)"               "$DUPTEST" --min-files 800 --min-methods 9000 --warn-only -- check-test-dup --min-files 800 --min-methods 9000 --warn-only
run "check-test-dup (the blocking gate)"      "$DUPTEST" -- check-test-dup
run "check-test-dup (the report)"             "$DUPTEST" --report -- check-test-dup --report
run "check-test-projects (the self-test)"     "$PROJECTS" --self-test -- check-test-projects --self-test
run "check-test-projects (the gate)"          "$PROJECTS" --min-projects 43 --min-tested 38 --min-lists 7 --min-claims 37 --min-contexts 41 -- check-test-projects --min-projects 43 --min-tested 38 --min-lists 7 --min-claims 37 --min-contexts 41
run "check-test-projects (the report)"        "$PROJECTS" --report -- check-test-projects --report

if [ "$FAILED" = "0" ]; then
    echo
    if [ "$EXPECTED" = "0" ]; then
        echo "ALL EQUIVALENT"
    else
        echo "ALL EQUIVALENT ($EXPECTED case(s) differ by the upstream change, and are marked above)"
    fi
else
    echo
    echo "SOME GATE DIVERGED"
fi
exit $FAILED
