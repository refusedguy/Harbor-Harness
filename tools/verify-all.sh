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

run "check-tool-schema-pin (the gate)"        origin/dev:tools/check-tool-schema-pin.py,origin/dev:tools/tool_schema_model.py --min-tools 25 -- check-tool-schema-pin --min-tools 25
run "check-tool-schema-pin (the self-test)"  origin/dev:tools/check-tool-schema-pin.py,origin/dev:tools/tool_schema_model.py --selftest -- check-tool-schema-pin --selftest
run "check-tool-schema-pin (the report)"     origin/dev:tools/check-tool-schema-pin.py,origin/dev:tools/tool_schema_model.py --report -- check-tool-schema-pin --report
run "check-tunit-analyzer-gate (self-test)"   origin/dev:tools/check-tunit-analyzer-gate.py --selftest -- check-tunit-analyzer-gate --selftest
run "check-test-json-reflection (the gate)"   origin/dev:tools/check-test-json-reflection.py -- check-test-json-reflection
run "check-test-json-reflection (report)"     origin/dev:tools/check-test-json-reflection.py --report -- check-test-json-reflection --report
run "check-test-json-reflection (json-out)"   origin/dev:tools/check-test-json-reflection.py --json-out -- check-test-json-reflection --json-out
run "check-test-json-reflection (self-test)"  origin/dev:tools/check-test-json-reflection.py --self-test -- check-test-json-reflection --self-test
run "check-dup-jscpd (the self-test)"         origin/dev:tools/check-dup-jscpd.py --self-test -- check-dup-jscpd --self-test
run "check-dup-jscpd (the report)"           origin/dev:tools/check-dup-jscpd.py --report "$JSCPD_REPORT" -- check-dup-jscpd --report "$JSCPD_REPORT"
run "check-test-packages (the self-test)"     origin/dev:tools/check-test-packages.py --self-test -- check-test-packages --self-test
run "check-test-packages (the gate)"          origin/dev:tools/check-test-packages.py -- check-test-packages
run "check-test-packages (the report)"        origin/dev:tools/check-test-packages.py --report -- check-test-packages --report
run "check-test-packages (the floors)"        origin/dev:tools/check-test-packages.py --min-projects 43 --min-declared 50 --min-packages 1400 --allow-missing-assets -- check-test-packages --min-projects 43 --min-declared 50 --min-packages 1400 --allow-missing-assets
run "check-test-dup (the self-test)"          origin/dev:tools/check-test-dup.py --self-test -- check-test-dup --self-test
run "check-test-dup (the gate)"               origin/dev:tools/check-test-dup.py --min-files 800 --min-methods 9000 --warn-only -- check-test-dup --min-files 800 --min-methods 9000 --warn-only
run "check-test-dup (the blocking gate)"      origin/dev:tools/check-test-dup.py -- check-test-dup
run "check-test-dup (the report)"             origin/dev:tools/check-test-dup.py --report -- check-test-dup --report
run "check-test-projects (the self-test)"     origin/dev:tools/check-test-projects.py --self-test -- check-test-projects --self-test
run "check-test-projects (the gate)"          origin/dev:tools/check-test-projects.py --min-projects 43 --min-tested 38 --min-lists 7 --min-claims 37 --min-contexts 41 -- check-test-projects --min-projects 43 --min-tested 38 --min-lists 7 --min-claims 37 --min-contexts 41
run "check-test-projects (the report)"        origin/dev:tools/check-test-projects.py --report -- check-test-projects --report

if [ "$FAILED" = "0" ]; then
    echo
    echo "ALL EQUIVALENT"
else
    echo
    echo "SOME GATE DIVERGED"
fi
exit $FAILED
