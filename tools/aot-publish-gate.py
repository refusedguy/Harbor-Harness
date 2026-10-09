#!/usr/bin/env python3
"""aot-publish-gate.py — the AOT publish gate (issue #413).

WHAT THIS CHECKS
----------------
`dotnet publish` of `apps/Harbor.App.Cli` with NativeAOT produces a log. This
tool reads that log and holds it against a committed record of what that
publish is known to do. It is deliberately unable to pass on a log that was
never produced.

    0  a publish reported its outcome, and that outcome and its diagnostic
       inventory match the committed record exactly
    1  the gate ran and said no — a different outcome, or diagnostic drift
    2  the gate could not RUN: no log, log under the byte floor, or no marker
       saying a publish happened at all

Exit code 2 is a separate value on purpose. "The AOT build is in the state we
recorded" and "nothing was published and I could not tell" are different facts,
and collapsing them is how a gate goes green on a process that died (cf. #988,
where a shard with no zero-test guard reported success having run nothing). A
`--selftest` flag proves each of those states is reachable and distinguishable,
on fixtures where a hit is guaranteed.

WHY THE RECORD INCLUDES FAILURE
-------------------------------
The measured state of this repository, from the CI runs that built this gate,
is that **no AOT publish configuration works**:

  * recipe 2 (`-p:HarborWithAot=true`, full tree) reaches ILC and fails on
    IL3000 x3, IL2072 x1, IL2070 x1 — none of them demoted by
    Harbor.App.Cli.csproj, and ILC runs warnings-as-errors. One of the three
    IL3000 is inside `Microsoft.CodeAnalysis` itself and is not fixable from our
    code;
  * recipe 1 (`HARBOR_MINIMAL=true`, the stripped profile the csproj calls the
    real AOT path) does not compile: `PluginReloadService.cs` is entirely inside
    `#if HARBOR_WITH_PLUGINS` and 11 call sites in apps/Harbor.App.Cli reference
    it unguarded.

So the gate cannot be "the publish is green" without first fixing the AOT build,
and fixing it is #48/S4's decision, not this gate's — #413's own criteria
forbid buying green with blanket suppression, and the Roslyn IL3000 would need
exactly that. A gate that can only ever be red is not a gate; a gate that
ignores the publish is worse than none.

What this implements instead is a RATCHET in both directions:

  * the committed record says the publish FAILS, with this inventory;
  * if it starts succeeding, the gate goes RED and says the AOT state improved
    and the record must be updated — the transition is acknowledged by a human
    rather than absorbed;
  * if it fails differently — a new diagnostic, a changed count, a new
    first-party site — the gate goes RED too;
  * only the recorded state passes.

That is not "zero IL2026" and it is not a rubber stamp. It makes the AOT debt
visible, pins it, and forces a decision the day it is paid or grown. It is also
the honest description of a repository whose AOT claim has never been
evaluated: the first thing this gate can do is record what is actually there.

ROW FORMAT
----------
One row per (diagnostic id, severity), five '|'-separated fields after the key:

    ID | severity | count | verdict | first-party files | reason

  ID        an IL2xxx (trim) or IL3xxx (AOT) id, or a CS id when the publish
            does not get far enough to produce any. IL0xxx compiler diagnostics
            and CS diagnostics are only in scope because the compile failure IS
            the current measured state of the minimal recipe.
  severity  warning | error. Both are recorded: a warning that the csproj
            demotes still has to be accounted for, and an error is the reason
            the publish fails.
  count     exact number of occurrences. A change in EITHER direction fails.
  verdict   ACCEPTED = a concession we carry, with the reason in the last
                      field. It is expected to keep appearing.
            REJECTED = a defect that ought to be fixed. It is ALSO expected to
                      keep appearing, and the gate says so out loud; the point
                      is that when it stops appearing the gate turns red and
                      demands the row be promoted or deleted, so the fix cannot
                      land unrecorded.
  files     '-' when every site is third-party; otherwise the comma-separated
            first-party SITES. A "site" is the Harbor type the diagnostic names
            (e.g. `Harbor.Plugins.Instantiation.ReflectionPluginInstantiator`),
            not a file path: ILC and ILLink print a type and a method, not a
            source file, and the only real path in an ILC log is Roslyn's own
            `src/Compilers/…`, which is third-party. A new site fails even at an
            unchanged count. Method names and line numbers are deliberately
            absent — they move on every unrelated edit.
  reason    >= 40 characters, not a bare URL — the same floor and URL rule
            tests/Harbor.Architecture.Tests/ExemptionReason.cs applies to every
            other table in this repo, so there is one definition of "states a
            reason".

TRIAGE GRANULARITY, AND WHY NOT file:line
-----------------------------------------
The inventory keys on diagnostic id, severity, occurrence count, and the set of
FIRST-PARTY FILES (anything under `src/` or `apps/`). Line numbers are
deliberately excluded: a baseline keyed on them must be edited by every
unrelated edit to the file above it, and a baseline that churns on every commit
is a baseline contributors delete. First-party files are the granularity that is
both actionable and stable — a warning that moves to a new file is a real
change, a warning whose line moved by three is not. Third-party sites are
counted but not listed file-by-file: a package's internals move with its
version and are not actionable per site.

See docs/AOT_PUBLISH.md for the gate's semantics, the recipe, the measured
inventory, and the list of things this tool deliberately does not check.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from collections import Counter, defaultdict

TOOL = "aot-publish-gate"

#: Written by the CI job when the publish step reaches its end, whatever the
#: outcome. Its ABSENCE is what distinguishes "the publish did what we recorded"
#: from "there was no publish here".
DEFAULT_MARKER = "HARBOR_AOT_PUBLISH_DONE"

#: A log smaller than this cannot be a real `dotnet publish` of a 40-project
#: closure — it is a truncated file, an empty file, or the wrong file.
DEFAULT_MIN_BYTES = 2000

#: `path(line,col): warning IL2026: text [project]`, and the same with `error`.
#: The severity is captured because the two are different facts: a demoted
#: warning does not fail the publish, an error does.
_DIAGNOSTIC = re.compile(
    r"(?:^|[\s\]])(?P<severity>warning|error)\s+(?P<id>(?:IL|CS)\d{3,4})\b",
    re.IGNORECASE,
)

#: A first-party SITE, read from the diagnostic's own text rather than from a
#: path. ILC and ILLink name a type and a method — `IL3000:
#: Harbor.Plugins.Compilation.PluginAssemblyReferences.BuildReferences(): ...` —
#: and do NOT print a source file for the site. The two things that look like
#: paths in an ILC log are both wrong answers: `Harbor.App.Cli.cs` is a csproj
#: that merely ends in ".cs", and `src/Compilers/Core/.../CommonCompiler.cs` is
#: ROSLYN'S OWN source path out of Microsoft.CodeAnalysis, which would be
#: recorded as first-party and is not. So the site is the Harbor type name,
#: which is what the message actually carries and what points at the project.
#: A diagnostic with no Harbor type (e.g. `IL2104: Assembly 'MessagePack'
#: produced trim warnings`) is third-party, which is the correct answer.
_SITE = re.compile(r"\b(?P<site>Harbor\.[A-Za-z0-9_.]*[A-Za-z0-9_])")

#: Compiler diagnostics and trim/AOT diagnostics. CS is in scope only because the
#: minimal recipe currently fails to compile at all; if that is fixed the CS rows
#: go away and the gate says so.
_IN_SCOPE = re.compile(r"^(?:IL[23]\d{3}|CS\d{4})$")

_SEVERITIES = ("warning", "error")
_VERDICTS = ("ACCEPTED", "REJECTED")
_STATUSES = ("published", "failed")


class Row:
    """One committed inventory row."""

    __slots__ = ("ident", "severity", "count", "verdict", "files", "reason", "lineno")

    def __init__(self, ident, severity, count, verdict, files, reason, lineno):
        self.ident = ident
        self.severity = severity
        self.count = count
        self.verdict = verdict
        self.files = files  # frozenset[str], empty == third-party only
        self.reason = reason
        self.lineno = lineno

    def key(self):
        return (self.count, self.verdict, self.files)


class Inventory:
    """What the log actually contained."""

    def __init__(self, counts, first_party, markers, log_bytes, log_lines):
        self.counts = counts  # Counter[(id, severity)]
        self.first_party = first_party  # dict[(id, severity), set[str]]
        self.markers = markers  # set[(rid, recipe, status, exit)]
        self.log_bytes = log_bytes
        self.log_lines = log_lines
        # Set by parse_log when --expect-status does not match what the log says.
        # (expected, seen) or None.
        self.status_mismatch = None

    def key(self):
        return {k: (c, frozenset(self.first_party.get(k, ()))) for k, c in self.counts.items()}


class GateError(Exception):
    """The gate could not run (exit 2). Distinct from a gate that ran and said no."""


def read_text(path, what):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError as exc:
        raise GateError(f"cannot read the {what} at {path}: {exc}") from exc


def parse_markers(text, marker):
    """Every completed publish's (rid, recipe, status, exit) it names.

    A log may name more than one if a job ran the publish more than once; the
    caller decides whether that is acceptable.
    """
    found = set()
    for line in text.splitlines():
        if marker not in line:
            continue
        rid = re.search(r"\brid=([A-Za-z0-9._-]+)", line)
        recipe = re.search(r"\brecipe=([A-Za-z0-9._-]+)", line)
        status = re.search(r"\bstatus=(published|failed)\b", line)
        code = re.search(r"\bexit=(-?\d+)", line)
        if rid and recipe and status:
            found.add((rid.group(1), recipe.group(1), status.group(1),
                       int(code.group(1)) if code else None))
    return found


def parse_log(text, log_path, min_bytes, marker, expect_rid, expect_recipe, expect_status):
    """Read a publish log into an Inventory, or raise GateError.

    `expect_rid` / `expect_recipe` mismatches raise: they mean the log is a
    DIFFERENT publish's, and the gate has nothing to say about it. A
    `expect_status` mismatch does NOT raise — it is returned as a problem, because
    a publish that started succeeding is a finding the gate must report loudly,
    not a reason to declare that it could not run.
    """
    log_bytes = len(text.encode("utf-8", errors="replace"))
    if log_bytes < min_bytes:
        raise GateError(
            f"the publish log is {log_bytes} bytes, under the {min_bytes}-byte floor. "
            "That is a truncated or empty file, not a publish of a 40-project closure. "
            "Raising the floor is a decision to make in review, not in the failing job."
        )

    markers = parse_markers(text, marker)
    if not markers:
        raise GateError(
            f"the publish log at {log_path} carries no '{marker} rid=… recipe=… status=…' "
            "line, so no publish reported an outcome into it. Treating this as the recorded "
            "state is how a gate goes green on a process that died: the job writes the marker "
            "only after `dotnet publish` has returned, and its absence is the whole signal."
        )

    mismatches = []
    if expect_rid and not any(r == expect_rid for r, _, _, _ in markers):
        mismatches.append(f"expected rid={expect_rid}")
    if expect_recipe and not any(c == expect_recipe for _, c, _, _ in markers):
        mismatches.append(
            f"expected recipe={expect_recipe} — the two AOT recipes in Harbor.App.Cli.csproj "
            "build DIFFERENT programs, so an inventory from one does not describe the other")
    if mismatches:
        raise GateError(
            f"the publish log at {log_path} does not describe the publish this baseline is "
            "about: " + "; ".join(mismatches) + ". This is a different publish's log.")

    counts = Counter()
    first_party = defaultdict(set)
    for line in text.splitlines():
        match = _DIAGNOSTIC.search(line)
        if match is None:
            continue
        ident = match.group("id").upper()
        if not _IN_SCOPE.match(ident):
            continue
        key = (ident, match.group("severity").lower())
        counts[key] += 1
        site = first_party_site(line)
        if site is not None:
            first_party[key].add(site)

    inventory = Inventory(counts, dict(first_party), markers, log_bytes,
                          len(text.splitlines()))
    inventory.status_mismatch = (
        None if expect_status is None or any(s == expect_status for _, _, s, _ in markers)
        else (expect_status, sorted({s for _, _, s, _ in markers})))
    return inventory


def parse_baseline(text, baseline_path):
    """Read the committed record, or raise GateError."""
    rows = {}
    failures = []
    for lineno, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        parts = [p.strip() for p in line.split("|")]
        if len(parts) < 6:
            failures.append(
                f"{baseline_path}:{lineno}: expected 6 '|'-separated fields "
                "(ID | severity | count | verdict | first-party files | reason) and found "
                f"{len(parts)}: {line!r}"
            )
            continue

        ident, severity, count_text, verdict, files_text, reason = parts[:6]
        ident = ident.upper()
        if not _IN_SCOPE.match(ident):
            failures.append(
                f"{baseline_path}:{lineno}: {ident!r} is not an IL2xxx/IL3xxx/CS id. This "
                "inventory is the AOT publish one.")
            continue
        severity = severity.lower()
        if severity not in _SEVERITIES:
            failures.append(
                f"{baseline_path}:{lineno}: severity {severity!r} is not one of {_SEVERITIES}.")
            continue
        key = (ident, severity)
        if key in rows:
            failures.append(
                f"{baseline_path}:{lineno}: {ident} {severity} already has a row on line "
                f"{rows[key].lineno}")
            continue
        if not count_text.isdigit() or int(count_text) < 1:
            failures.append(
                f"{baseline_path}:{lineno}: count {count_text!r} is not a positive integer. A "
                "row for a diagnostic that does not occur is a permission, not a measurement.")
            continue
        if verdict not in _VERDICTS:
            failures.append(
                f"{baseline_path}:{lineno}: verdict {verdict!r} is not one of {_VERDICTS}. Every "
                "emitted diagnostic is either a concession we carry or a defect to fix; "
                "'tolerated for now' is not one of the two.")
            continue
        if len(reason) < 40:
            failures.append(
                f"{baseline_path}:{lineno}: reason is {len(reason)} characters; a reason that "
                "cannot fit in fewer than 40 is not an argument (same floor as ExemptionReason).")
            continue
        if re.match(r"^\W*https?://\S+[.,;]?\W*$", reason):
            failures.append(
                f"{baseline_path}:{lineno}: the reason is a URL. A URL says where the debt is "
                "tracked, not why it is tolerated here.")
            continue

        sites = frozenset() if files_text == "-" else frozenset(
            f.strip() for f in files_text.split(",") if f.strip())
        for site in sites:
            if not site.startswith("Harbor."):
                failures.append(
                    f"{baseline_path}:{lineno}: {site!r} is not a first-party site. A site is the "
                    "Harbor type the diagnostic names (e.g. "
                    "Harbor.Plugins.Instantiation.ReflectionPluginInstantiator); third-party-only is "
                    "'-'. A file path is not accepted: ILC prints no source file, and the only "
                    "real path in an ILC log is Roslyn's own src/Compilers/…")
        files = sites
        rows[key] = Row(ident, severity, int(count_text), verdict, files, reason, lineno)

    if failures:
        raise GateError("the baseline is not well formed:\n  " + "\n  ".join(failures))
    return rows


#: MSBuild appends the project as a trailing ` [/path/to/Project.csproj]` tag.
#: It must be cut before the site is read: `apps/Harbor.App.Cli/Harbor.App.Cli.csproj`
#: contains the string `Harbor.App.Cli`, and without this every per-assembly
#: rollup (IL2104, IL3053) would be recorded as a first-party site at the CLI.
_PROJECT_TAG = re.compile(r"\s\[/?[^\]]*\]\s*$")


def first_party_site(line):
    """The Harbor site a diagnostic is attributed to, or None.

    The whole line is searched, not just the text after the diagnostic id,
    because the two shapes put the site on opposite sides: MSBuild writes
    `path(line,col): warning IL2026: text` with the site BEFORE the severity,
    while ILC writes `ILC : error IL3000: Harbor.X.Y.Z(): text` with it after.

    The site is the whole dotted run and is NOT trimmed to a type. Trimming was
    tried and is ambiguous in a way that produces wrong answers: ILC writes
    `Namespace.Type.Method(args):` where the last segment is a method, but a
    compiler-style line writes `Namespace.Type(line,col):` where the thing before
    the paren IS the type and the paren opens a line/column. The two shapes are
    indistinguishable from the text, and guessing wrong records a site that does
    not exist. So a site may include a method name where the tool named one;
    that costs a little churn when a method is renamed, and it is cheaper than a
    baseline that lies about where the debt is.
    """
    text = _PROJECT_TAG.sub("", line)
    match = _SITE.search(text)
    return match.group("site") if match else None


def describe(counts, first_party):
    """Never a bare 'found 0' — #901 reported 'Found 0' from a matcher that matched nothing."""
    if not counts:
        return "found 0 distinct diagnostic id/severity pair(s), 0 occurrence(s) (ids: none)"
    by_id = defaultdict(int)
    for (ident, _), count in counts.items():
        by_id[ident] += count
    ids = " ".join(f"{i}({by_id[i]})" for i in sorted(by_id))
    owned = sum(len(v) for v in first_party.values())
    errors = sum(c for (_, s), c in counts.items() if s == "error")
    return (f"found {len(by_id)} distinct diagnostic id(s), {sum(counts.values())} occurrence(s) "
            f"(ids: {ids}); {errors} error-severity; first-party sites: {owned}")


def compare(baseline, inventory):
    """Both directions: a new diagnostic fails, and a row nothing produces fails.

    Returns (problems, notes). Only `problems` affect the exit code; `notes` are
    printed on a pass so the reader knows what they are passing ON, which is the
    difference between a ratchet and a rubber stamp.
    """
    problems = []
    notes = []
    expected = {k: row.key() for k, row in baseline.items()}
    actual = inventory.key()

    if inventory.status_mismatch is not None:
        want, seen = inventory.status_mismatch
        direction = ("the AOT state IMPROVED" if "published" in seen
                     else "the AOT state got WORSE")
        problems.append(
            f"OUTCOME the committed record says the publish reports status={want}, and this run "
            f"reported {seen} — {direction}. That is not something the gate may absorb: if the "
            "build now publishes, or now fails differently, the AOT record has to be updated in "
            "the same diff by whoever read the new log. See docs/AOT_PUBLISH.md.")

    for key in sorted(set(actual) - set(expected)):
        ident, severity = key
        sites = sorted(inventory.first_party.get(key, ()))
        where = f" at {', '.join(sites)}" if sites else " (third-party only)"
        problems.append(
            f"NEW  {ident} {severity}: {inventory.counts[key]} occurrence(s){where} — the baseline "
            f"has no row for it. Add a row with a verdict and a reason, or fix what emits it. If "
            "this is the AOT state IMPROVING, say so in the row's reason: the reason field is where "
            "the next reader learns whether this is progress or a regression.")

    for key in sorted(set(expected) - set(actual)):
        ident, severity = key
        verdict = baseline[key].verdict
        tail = ("That is a REJECTED row, so this is the AOT state IMPROVING — promote the row or "
                "delete it in this same diff, or the fix lands unrecorded and the gate goes red "
                "again the next time it reappears."
                if verdict == "REJECTED" else
                "The warning was fixed or the dependency moved — delete the row in this same diff, "
                "or it has become a permission for a concession nobody is making.")
        problems.append(f"GONE  {ident} {severity}: the baseline carries a row the publish no "
                        f"longer produces. {tail}")

    for key in sorted(set(expected) & set(actual)):
        ident, severity = key
        want_count, want_verdict, want_files = expected[key]
        got_count, got_files = actual[key]
        if got_count != want_count:
            problems.append(
                f"COUNT {ident} {severity}: {got_count} occurrence(s) now, {want_count} in the "
                "baseline. Re-triage the delta and update the count in the same diff.")
        if got_files != want_files:
            added = sorted(got_files - want_files)
            removed = sorted(want_files - got_files)
            detail = []
            if added:
                detail.append(f"new first-party site(s): {', '.join(added)}")
            if removed:
                detail.append(f"site(s) that no longer warn: {', '.join(removed)}")
            problems.append(
                f"SITES {ident} {severity}: " + "; ".join(detail) + " (baseline: "
                f"{', '.join(sorted(want_files)) if want_files else '-'}, now: "
                f"{', '.join(sorted(got_files)) if got_files else '-'} — '-' means third-party only).")

    rejected = sorted(k for k in expected if baseline[k].verdict == "REJECTED")
    if rejected:
        notes.append(
            "NOTE  recorded as REJECTED, i.e. defects that ought to be fixed and are still "
            "present: " + ", ".join(f"{i} {s}" for i, s in rejected) + ". This run PASSES on them "
            "because that is the state the repository is in. The pass is a ratchet, not an "
            "endorsement: when one of them stops appearing, the gate goes red and the row has to "
            "be promoted or deleted. See docs/AOT_PUBLISH.md.")

    return problems, notes


def run(log_path, baseline_path, marker, min_bytes, expect_rid, expect_recipe,
        expect_status, out):
    """Returns the process exit code."""
    try:
        log_text = read_text(log_path, "publish log")
        inventory = parse_log(log_text, log_path, min_bytes, marker,
                              expect_rid, expect_recipe, expect_status)
        baseline = parse_baseline(read_text(baseline_path, "baseline"), baseline_path)
    except GateError as exc:
        out(f"{TOOL}: CANNOT RUN (exit 2) — {exc}")
        return 2

    outcomes = ", ".join(f"rid={r} recipe={c} status={s} exit={e}"
                         for r, c, s, e in sorted(inventory.markers))
    out(f"{TOOL}: publish log {log_path} — {inventory.log_bytes} bytes, "
        f"{inventory.log_lines} lines; publish outcome: {outcomes}")
    out(f"{TOOL}: {describe(inventory.counts, inventory.first_party)}")
    out(f"{TOOL}: baseline {baseline_path} — {len(baseline)} row(s): "
        f"{', '.join(f'{i} {s}' for i, s in sorted(baseline)) if baseline else 'none'}")

    problems, notes = compare(baseline, inventory)
    if notes:
        out("")
        for note in notes:
            out(f"{TOOL}: {note}")
        out("")
    if problems:
        out("")
        out(f"{TOOL}: FAIL (exit 1) — the publish does not match the committed record:")
        for problem in problems:
            out(f"  {problem}")
        out("")
        out("Gate semantics: the pass criterion is NOT 'zero IL2026 warnings'. It is 'the AOT")
        out("publish's outcome and diagnostics are exactly what the committed record says, and")
        out("that record was written by a human who read them'. See docs/AOT_PUBLISH.md.")
        return 1

    out(f"{TOOL}: PASS (exit 0) — the publish's outcome and inventory match the committed record.")
    return 0


# ----------------------------------------------------------------------------
# Self-test. Every case below is a fixture where the outcome is guaranteed, and
# every assertion checks the exit code AND the substance of the message, so a
# matcher that fails for the wrong reason cannot pass.
# ----------------------------------------------------------------------------

_SELFTEST_LOG = """\
Using .NET SDK 10.0.302 [/home/runner/work/Harbor-Harness/Harbor-Harness/global.json]
  Determining projects to restore...
  Restored /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/Harbor.Application.csproj (in 412 ms).
  Harbor.Application -> /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/bin/Release/net10.0/Harbor.Application.dll
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Application.Session(88,13): warning IL2026: The 'PublishAot' analyzer warned: 'this call site will not be preserved' [HARBOR_APP_CLI]
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Application.Session(91,9): warning IL2026: The 'PublishAot' analyzer warned: 'this call site will not be preserved' [HARBOR_APP_CLI]
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Plugins.Compilation.RoslynPluginCompiler(140,22): warning IL3050: The 'PublishAot' analyzer warned: 'call site can cause AOT analysis warnings' [HARBOR_APP_CLI]
/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Plugins.Instantiation.Reflect(9,1): error IL2070: Trim analysis error: 'this' argument does not satisfy PublicParameterlessConstructor [HARBOR_APP_CLI]
Trimmer warnings summary:
  ILLink: 118 warning(s) from 6 assembly(s) — Microsoft.CodeAnalysis.dll, ...
  Total: 118
  {marker} rid={rid} recipe={recipe} status={status} exit={code}
"""


def fixture(rid="linux-x64", recipe="fulltree", status="failed", code=1,
            marker=DEFAULT_MARKER, extra_lines=(), body=None):
    """A publish log whose bytes exceed the floor, so only the intended defect can fail it."""
    text = body if body is not None else _SELFTEST_LOG.format(
        rid=rid, recipe=recipe, status=status, code=code, marker=marker)
    padding = "\n".join(f"  linker: pass 0x{i:x} ilc: emit rodata" for i in range(200))
    return text + "\n" + padding + "\n" + "\n".join(extra_lines) + "\n"


def selftest(out):
    failures = []
    checks = 0

    def check(name, got, want_exit, must_contain=(), must_not_contain=()):
        nonlocal checks
        checks += 1
        lines = []
        code = run(*got, out=lines.append)
        blob = "\n".join(lines)
        if code != want_exit:
            failures.append(f"{name}: exit {code}, wanted {want_exit}\n    {blob}")
            return
        for needle in must_contain:
            if needle not in blob:
                failures.append(f"{name}: exit was right but the message never said {needle!r}\n    {blob}")
        for needle in must_not_contain:
            if needle in blob:
                failures.append(f"{name}: message must not say {needle!r}\n    {blob}")

    with _tmpdir() as tmp:
        def path(name):
            return os.path.join(tmp, name)

        def write(name, text):
            with open(path(name), "w", encoding="utf-8") as handle:
                handle.write(text)
            return path(name)

        empty_baseline = "# nothing emitted yet\n"
        rows = (
            "IL2026 | warning | 2 | ACCEPTED | Harbor.Application.Session | "
            "first-party site, argument recorded in docs/AOT_PUBLISH.md\n"
            "IL3050 | warning | 1 | ACCEPTED | Harbor.Plugins.Compilation.RoslynPluginCompiler | "
            "Roslyn needs the JIT; the split point is #419 and the ADR is #418\n"
            "IL2070 | error | 1 | REJECTED | Harbor.Plugins.Instantiation.Reflect | "
            "missing DynamicallyAccessedMembers annotation; #48/S4 owns the plugin split point\n"
        )
        # (log, baseline, marker, min_bytes, rid, recipe, status)
        def args(log, base, rid="linux-x64", recipe="fulltree", status="failed", path_=None):
            return (path_ or log, base, DEFAULT_MARKER, 2000, rid, recipe, status)

        # 1. The green case: the recorded state reproduces exactly.
        check("green_recorded_state",
              args(write("log1", fixture()), write("base1", rows)),
              0,
              must_contain=["found 3 distinct diagnostic id(s), 4 occurrence(s)",
                            "status=failed exit=1", "1 error-severity", "PASS (exit 0)",
                            "REJECTED", "ratchet, not an"])

        # 2. THE IMPROVEMENT CASE. The whole point of the ratchet: the publish
        #    succeeding is a CHANGE, and the gate must not absorb it silently.
        check("publish_starting_to_succeed_is_still_red",
              args(write("log2", fixture(status="published", code=0)),
                     write("base2", rows), status="failed"),
              1,
              must_contain=["FAIL (exit 1)"])

        # 3. THE VACUITY CASE (#988 form): no marker, i.e. no publish reported
        #    anything. Must be exit 2 and must never be 0, even though the log is
        #    large, well-formed and matches the inventory exactly.
        check("no_marker_cannot_go_green",
              args(write("log3", fixture(marker="SOMETHING_ELSE")), write("base3", rows)),
              2,
              must_contain=["CANNOT RUN (exit 2)", "carries no 'HARBOR_AOT_PUBLISH_DONE"])

        # 4. A different platform's log, and the other recipe's log, are both
        #    refused — the two build different programs.
        check("wrong_rid_is_rejected",
              args(write("log4", fixture(rid="win-x64")), write("base4", rows), rid="linux-x64"),
              2,
              must_contain=["expected rid=linux-x64"])
        check("wrong_recipe_is_rejected",
              args(write("log4b", fixture(recipe="minimal")), write("base4b", rows),
                     recipe="fulltree"),
              2,
              must_contain=["expected recipe=fulltree", "DIFFERENT programs"])

        # 5. THE PLANTED-HIT CASE (#591 form): one extra id in the log, in scope,
        #    that the baseline knows nothing about. The message must NAME it.
        check("planted_new_id_fails",
              args(write("log5", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Tui.CellForge.Screen(40,5): "
                  "error IL3000: planted for the self-test [HARBOR_APP_CLI]"])),
                  write("base5", rows)),
              1,
              must_contain=["NEW  IL3000 error", "Harbor.Tui.CellForge.Screen", "FAIL (exit 1)"])

        # 5b. A new id at a THIRD-PARTY site is still red. A matcher that skipped
        #     unresolvable paths would pass here.
        check("planted_third_party_id_fails",
              args(write("log5b", fixture(extra_lines=[
                  "  /home/runner/.nuget/packages/microsoft.codeanalysis.csharp/5.6.0/lib/netstandard2.0/Microsoft.CodeAnalysis.dll(9,9): "
                  "error IL3000: linker warning: planted [HARBOR_APP_CLI]"])),
                  write("base5b", rows)),
              1,
              must_contain=["NEW  IL3000 error", "third-party only"])

        # 5d. MSBuild's trailing project tag must not become a site. This one is
        #     pinned because it actually bit: the tag is
        #     `[.../apps/Harbor.App.Cli/Harbor.App.Cli.csproj]`, which contains the
        #     string `Harbor.App.Cli`, so without stripping it every
        #     per-assembly rollup (IL2104, IL3053) records the CLI as a
        #     first-party site. The line carries a real IL2104 diagnostic, since
        #     a fixture without a diagnostic id proves nothing.
        check("msbuild_project_tag_is_not_a_site",
              args(write("log5d", fixture(extra_lines=[
                      "/home/runner/.nuget/packages/messagepack/3.1.4/lib/net8.0/MessagePack.dll: "
                      "warning IL2104: Assembly 'MessagePack' produced trim warnings. For more "
                      "information see https://aka.ms/il2104 "
                      "[/home/runner/work/Harbor-Harness/Harbor-Harness/apps/Harbor.App.Cli/Harbor.App.Cli.csproj]"])),
                  write("base5d", rows)),
              1,
              must_contain=["NEW  IL2104 warning", "third-party only"],
              must_not_contain=["Harbor.App.Cli"])

        # 5c. An out-of-scope id is not drift.
        check("out_of_scope_id_is_ignored",
              args(write("log5c", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Hosting.HostBuilder(9,1): "
                  "warning IL9999: not a trim or AOT id [HARBOR_APP_CLI]"])),
                  write("base5c", rows)),
              0,
              must_contain=["found 3 distinct diagnostic id(s), 4 occurrence(s)"])

        # 6. THE HALVED-RULE CASE (#591 form): the rule is "count occurrences",
        #    and a matcher that reported one line per id instead of counting
        #    would pass here.
        check("occurrence_count_is_not_deduplicated",
              args(write("log6", fixture()), write("base6", rows)),
              0,
              must_contain=["4 occurrence(s)"])
        check("a_fourth_occurrence_turns_it_red",
              args(write("log7", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Application.Session(120,5): "
                  "warning IL2026: analyzer warned: one more [HARBOR_APP_CLI]"])),
                  write("base7", rows)),
              1,
              must_contain=["COUNT IL2026 warning: 3 occurrence(s) now, 2 in the baseline"])

        # 7. Severity is part of the key. The baseline has IL2026 as a WARNING;
        #    the same id arriving as an ERROR is a different row. A matcher that
        #    keyed on id alone would either match the warning row (hiding a new
        #    error) or report a confused count.
        check("severity_is_part_of_the_key",
              args(write("log8", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Hosting.HostBuilder(1,1): "
                  "error IL2026: same id as the warning row, but an error [HARBOR_APP_CLI]"])),
                  write("base8", rows)),
              1,
              must_contain=["NEW  IL2026 error"],
              must_not_contain=["NEW  IL2026 warning"])

        # 8. A first-party site that appears is drift even at an unchanged count.
        check("new_first_party_site_fails",
              args(write("log9", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Tui.CellForge.Screen(41,5): "
                  "warning IL3050: our code now calls Roslyn [HARBOR_APP_CLI]"])),
                  write("base9", rows)),
              1,
              must_contain=["SITES IL3050 warning", "Harbor.Tui.CellForge.Screen"])

        # 9. Liveness: a row for a diagnostic nothing emits is a stale permission.
        check("stale_baseline_row_fails",
              args(write("log10", fixture()), write("base10",
                  rows + "IL2104 | warning | 7 | ACCEPTED | - | per-assembly trim rollup inside a referenced package, tracked in #413\n")),
              1,
              must_contain=["GONE  IL2104 warning", "delete the row in this same diff"])

        # 10. A REJECTED row that stops appearing is an IMPROVEMENT and must also
        #     be red — otherwise the fix lands unrecorded.
        check("rejected_row_disappearing_is_still_red",
              args(write("log11", fixture(extra_lines=[], body=fixture().replace(
                  "/home/runner/work/Harbor-Harness/Harbor-Harness/Harbor.Plugins.Instantiation.Reflect(9,1): error IL2070: Trim analysis error: 'this' argument does not satisfy PublicParameterlessConstructor [HARBOR_APP_CLI]\n", ""))),
                  write("base11", rows)),
              1,
              must_contain=["GONE  IL2070 error", "the AOT state IMPROVING"])

        # 11. A short log is a truncated file, not a publish.
        check("truncated_log_cannot_run",
              args(write("log12", DEFAULT_MARKER + " rid=linux-x64 recipe=fulltree status=failed exit=1\n"),
                     write("base12", empty_baseline), status="failed"),
              2,
              must_contain=["under the 2000-byte floor"])

        # 12. No log at all.
        check("absent_log_cannot_run",
              args(None, write("base13", empty_baseline), path_=path("does-not-exist.log")),
              2,
              must_contain=["CANNOT RUN (exit 2)"])

        # 13. Baseline rows that would not survive review are refused, so a
        #     permission cannot be added by editing the record alone.
        for name, row, needle in [
            ("no_verdict", "IL2026 | warning | 2 | TOLERATED | - | some reason that is long enough to pass the floor\n",
             "verdict 'TOLERATED' is not one of"),
            ("zero_count", "IL2026 | warning | 0 | ACCEPTED | - | a row for something that does not occur is a permission not a measurement\n",
             "is not a positive integer"),
            ("url_reason", "IL2026 | warning | 2 | ACCEPTED | - | https://github.com/refusedguy/Harbor-Harness/issues/413\n",
             "the reason is a URL"),
            ("short_reason", "IL2026 | warning | 2 | ACCEPTED | - | later\n", "is 5 characters"),
            ("out_of_scope", "CA1822 | warning | 2 | ACCEPTED | - | an ordinary analyzer id is not a trim or AOT diagnostic at all\n",
             "is not an IL2xxx/IL3xxx/CS id"),
            ("wrong_field_count", "IL2026 | warning | 2 | ACCEPTED\n",
             "expected 6 '|'-separated fields"),
            ("path_claimed_as_a_site",
             "IL2026 | warning | 2 | ACCEPTED | src/Harbor.Hosting/Configuration/JsonAppConfigStore.cs | a file path is not a site: ILC prints the type, not the file\n",
             "is not a first-party site"),
            ("bad_severity", "IL2026 | note | 2 | ACCEPTED | - | severity must be one of the two the log can actually produce\n",
             "severity 'note' is not one of"),
        ]:
            check(f"baseline_{name}",
                  args(write(f"log14{name}", fixture()), write(f"base14{name}", row)),
                  2,
                  must_contain=[needle, "the baseline is not well formed"])

    if failures:
        out(f"{TOOL}: SELFTEST FAILED — {len(failures)} of {checks} case(s) wrong")
        for failure in failures:
            out(f"  {failure}")
        return 1

    out(f"{TOOL}: SELFTEST PASSED — {checks} cases, each on a fixture where the outcome is "
        "guaranteed (planted extra id, planted extra occurrence, severity collision, planted "
        "first-party site, no marker, wrong rid, wrong recipe, unexpected publish success, "
        "truncated log, absent log, 8 malformed baseline rows).")
    return 0


class _tmpdir:
    """A scratch directory, so the self-test writes nothing into the checkout."""

    def __enter__(self):
        import tempfile

        self._dir = tempfile.TemporaryDirectory(prefix="aot-gate-selftest-")
        return self._dir.name

    def __exit__(self, *exc):
        self._dir.cleanup()
        return False


def main(argv):
    parser = argparse.ArgumentParser(
        prog=TOOL,
        description="Hold an AOT publish log against the committed record of what it does.")
    parser.add_argument("--log", help="path to the captured `dotnet publish` log")
    parser.add_argument("--baseline", help="path to the committed record")
    parser.add_argument("--marker", default=DEFAULT_MARKER,
                        help=f"token the publish step writes on completion (default: {DEFAULT_MARKER})")
    parser.add_argument("--min-bytes", type=int, default=DEFAULT_MIN_BYTES,
                        help=f"floor on the log's size (default: {DEFAULT_MIN_BYTES})")
    parser.add_argument("--expect-rid", default=None,
                        help="require the marker to name this runtime identifier")
    parser.add_argument("--expect-recipe", default=None,
                        help="require the marker to name this AOT recipe (e.g. 'fulltree')")
    parser.add_argument("--expect-status", default=None, choices=_STATUSES,
                        help="require the publish to have reported this outcome")
    parser.add_argument("--selftest", action="store_true",
                        help="run the self-test against guaranteed-hit fixtures and exit")
    args = parser.parse_args(argv)

    out = lambda line: print(line, file=sys.stdout, flush=True)  # noqa: E731

    if args.selftest:
        return selftest(out)
    if not args.log or not args.baseline:
        parser.error("--log and --baseline are both required (or pass --selftest)")
    return run(args.log, args.baseline, args.marker, args.min_bytes,
               args.expect_rid, args.expect_recipe, args.expect_status, out)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
